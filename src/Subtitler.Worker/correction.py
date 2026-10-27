"""Word and punctuation correction of recognized words by a local language model.

Runs in the recognition worker after the recognizer and aligner are released, so only
one model occupies GPU memory. The model may change punctuation, capitalization and
words misheard as a similar-sounding word. Every edit is checked here before it is
applied: spoken words are never added, dropped, reordered or paraphrased, and each
word keeps its aligned timing.
"""
import difflib
import hashlib
import json
from pathlib import Path
import re

from progress_reporting import report

VERSION = "correction-v1"
LOW_CONFIDENCE = 0.65
# Words of transcript and context sent in one request. Typical 7-10 minute recordings
# fit in one; longer ones are corrected in windows with neighbouring lines as context.
WORD_BUDGET = 4000
WINDOW_WORDS, BEFORE_WORDS, AFTER_WORDS = 350, 250, 100
LINE_WORDS, LINE_PAUSE, SPEAKER_PAUSE = 20, 0.5, 0.1

PROMPT = """You proofread speech recognition transcripts that will become video subtitles.
The user message is a JSON list of transcript lines in time order. Each line has an id, a speaker, a time and the recognized text. A sentence can continue on the same speaker's next line.
Correct only:
- punctuation: end each sentence with a period, question mark or exclamation mark (a question gets a question mark), and add commas where a reader needs them;
- capitalization: the first word of each sentence, the word I, and names;
%s
%s Keep filler words (um, uh, like), repetitions, slang, informal forms such as gonna and wanna, profanity and the speaker's grammar exactly as spoken, and keep numbers as written.
Words starting with ~ had low recognition confidence and are more likely to be misheard. Leave the ~ out of your answer.
Lines marked "context": true are from other subtitle tracks or nearby parts of the transcript, for reference only: never return them.
Answer with every other line you changed, including lines where you only changed punctuation or capitalization: its id and its complete corrected text, without the speaker, time or id. Leave out lines that need no change.
For example, the line {"id": "s1", "speaker": "Mic", "time": "00:01", "text": "your going to love this one"} is answered as {"id": "s1", "text": "You're going to love this one."}."""
# Sound-alike words only. A thorough mode that also asked for other misheard words was
# removed on 2026-10-24: on the gold set it raised WER (11.7% to 12.0%).
HOMOPHONES = ("- a word written as a different word with the same pronunciation, such as \"weave\" for \"we've\", \"there\" for \"their\" or \"to\" for \"too\": "
              "use the word that fits the sentence.", "Never add, remove, reorder or paraphrase words.")
# What the speaker said, not a mishearing to correct: a model's urge to formalize them.
SPOKEN_FORMS = {"gonna", "wanna", "gotta", "kinda", "sorta", "dunno", "lemme", "gimme", "ain't", "y'all", "ya", "yeah", "yep", "nope",
                "cause", "'cause", "cuz", "em", "'em", "outta", "lotta", "tryna", "finna"}

SCHEMA = {
    "type": "object",
    "properties": {"lines": {"type": "array", "items": {
        "type": "object", "properties": {"id": {"type": "string"}, "text": {"type": "string"}},
        "required": ["id", "text"], "additionalProperties": False}}},
    "required": ["lines"], "additionalProperties": False,
}


class RuntimeUnavailable(Exception):
    """The correction runtime or model cannot be used; generation continues without it."""


def key(text):
    """Comparable form of a word: lower case letters, digits and inner apostrophes."""
    return re.sub(r"[^\w']", "", text.lower().replace("’", "'")).strip("'_")


# Spellings that sound alike, applied in order: silent letters, then consonant spellings.
SOUND_RULES = [(re.compile(a), b) for a, b in (
    ("^wr", "r"), ("^kn", "n"), ("^gn", "n"), ("^ps", "s"), ("^wh", "w"), ("gh", ""), ("ph", "f"), ("ck", "k"),
    ("q", "k"), ("x", "ks"), ("c(?=[eiy])", "s"), ("c", "k"), ("z", "s"), ("dg", "j"))]


def sound_key(word):
    """A crude phonetic key: spellings normalized, any leading vowel alike, then the
    consonants without h/w/y, with repeats collapsed (weave and we've are both wv)."""
    letters = re.sub(r"[^a-z]", "", word)
    if not letters:
        return word
    for pattern, replacement in SOUND_RULES:
        letters = pattern.sub(replacement, letters)
    if not letters:
        return word
    head = "a" if letters[0] in "aeiou" else letters[0]
    return re.sub(r"(.)\1+", r"\1", head + re.sub(r"[aeiouhwy]", "", letters[1:]))


def sound_alike(old, new):
    """Whether old and new sound the same: digits for words, or a different word form
    such as gonna/going, do not."""
    if bool(re.search(r"\d", old)) != bool(re.search(r"\d", new)):
        return False
    return sound_key(old) == sound_key(new) or difflib.SequenceMatcher(None, old, new).ratio() >= 0.75


VOICING = str.maketrans("bdgvz", "ptkfs")


def acoustic_key(word):
    """sound_key with voiced and voiceless pairs merged (b/p, d/t, g/k, v/f, z/s), which a
    recognizer confuses in fast speech: kidding and kitten become ktnk and ktn."""
    return re.sub(r"(.)\1+", r"\1", sound_key(word).translate(VOICING))


def misheard_alike(old, new):
    """Whether new could be what was said when the recognizer wrote old, meaning the same
    sound or mostly the same consonants once voicing is ignored: such a replacement keeps
    its recognized words instead of rejecting the line. car/truck and right/left are not;
    kitten/kidding and should of/should've are."""
    if bool(re.search(r"\d", old)) != bool(re.search(r"\d", new)):
        return False
    return sound_alike(old, new) or difflib.SequenceMatcher(None, acoustic_key(old), acoustic_key(new)).ratio() >= 0.65


def clock(seconds):
    seconds = max(0, int(seconds))
    return "%d:%02d:%02d" % (seconds // 3600, seconds // 60 % 60, seconds % 60) if seconds >= 3600 else "%02d:%02d" % (seconds // 60, seconds % 60)


def build_lines(sources, results, context):
    """Transcript lines in timeline order: phrases of each source's words, and read-only
    context lines (subtitles on other tracks). Times are mapped as Timing.Map does.

    Words that carry a Speaker name (diarized before correction) label their line with it
    instead of the track name. A line ends at a speaker change only after a pause of
    SPEAKER_PAUSE: on the gold set every diarized change without one fell mid-phrase
    ("two | hundred"), and the model ends a sentence wherever a line ends."""
    lines = []
    for source in sources:
        words = results[source["Id"]]["Words"]
        start, rate = float(source["Start"]), float(source["Rate"])
        label = source.get("TrackName") or source.get("Label") or "Speaker"
        current = []
        for index, word in enumerate(words):
            if current:
                previous = words[current[-1]]
                pause = word["Start"] - previous["End"]
                if (len(current) >= LINE_WORDS or pause >= LINE_PAUSE or previous["Text"].rstrip("\"')]").endswith((".", "?", "!")) or
                        word.get("Speaker") != previous.get("Speaker") and pause >= SPEAKER_PAUSE):
                    lines.append(_line(source, current, words, start, rate, words[current[0]].get("Speaker") or label))
                    current = []
            current.append(index)
        if current:
            lines.append(_line(source, current, words, start, rate, words[current[0]].get("Speaker") or label))
    for item in context:
        text = " ".join((item.get("Text") or "").split())
        if text:
            lines.append({"Source": None, "Indices": [], "Start": float(item["Start"]), "Label": item.get("Label") or "Other track",
                          "Text": text, "Words": len(text.split())})
    lines.sort(key=lambda line: (line["Start"], line["Source"] is None))
    targets = contexts = 0
    for line in lines:
        if line["Source"] is None:
            contexts += 1; line["Id"] = "c%d" % contexts
        else:
            targets += 1; line["Id"] = "s%d" % targets
    return lines


def _line(source, indices, words, start, rate, label):
    tokens = []
    for i in indices:
        text = words[i]["Text"].strip()
        if not text:
            continue
        probability = words[i].get("Probability")
        tokens.append(("~" if probability is not None and probability < LOW_CONFIDENCE else "") + text)
    return {"Source": source["Id"], "Indices": indices, "Start": start + words[indices[0]]["Start"] / rate, "Label": label,
            "Text": " ".join(tokens), "Words": len(tokens)}


def plan_windows(lines):
    """Requests: each corrects its target lines and shows nearby lines as read-only context."""
    targets = [i for i, line in enumerate(lines) if line["Source"] is not None]
    if not targets:
        return []
    if sum(line["Words"] for line in lines) <= WORD_BUDGET:
        return [{"Lines": list(range(len(lines))), "Targets": targets}]
    windows, group, count = [], [], 0
    for i in targets:
        group.append(i); count += lines[i]["Words"]
        if count >= WINDOW_WORDS:
            windows.append(group); group, count = [], 0
    if group:
        windows.append(group)
    planned = []
    for group in windows:
        first, last = group[0], group[-1]
        before, words = first, 0
        while before > 0 and words < BEFORE_WORDS:
            before -= 1; words += lines[before]["Words"]
        after, words = last, 0
        while after + 1 < len(lines) and words < AFTER_WORDS:
            after += 1; words += lines[after]["Words"]
        planned.append({"Lines": list(range(before, after + 1)), "Targets": group})
    return planned


def messages(lines, window):
    targets = set(window["Targets"])
    rows = []
    for i in window["Lines"]:
        line = lines[i]
        row = {"id": line["Id"], "speaker": line["Label"], "time": clock(line["Start"]), "text": line["Text"]}
        # Other tracks' subtitles, and this transcript's lines outside the window.
        if i not in targets:
            row["context"] = True
        rows.append(json.dumps(row, ensure_ascii=False))
    return [{"role": "system", "content": PROMPT % HOMOPHONES}, {"role": "user", "content": "[\n" + ",\n".join(rows) + "\n]"}]


def strip_echo(text, line):
    """The corrected words alone: models sometimes repeat a line's id or speaker label."""
    text = text.strip()
    for prefix in (line["Id"], line["Label"]):
        if text.lower().startswith(prefix.lower()) and text[len(prefix):].lstrip()[:1] in (":", "|"):
            text = text[len(prefix):].lstrip()[1:].strip()
    return text


def estimate_tokens(text):
    return int(len(text) / 3.2) + 16


def context_length(requests):
    """Context to load the model with: the largest request and its longest possible answer."""
    largest = max(estimate_tokens(m[0]["content"] + m[1]["content"]) + answer_tokens(m) for m in requests)
    return max(4096, -(-int(largest * 1.3) // 4096) * 4096)


def answer_tokens(request):
    # Every line returned in full, as JSON, plus room for reasoning a model emits anyway.
    return int(estimate_tokens(request[1]["content"]) * 1.5) + 1024


def cache_path(root, model, request):
    material = json.dumps({"Version": VERSION, "Model": model, "Messages": request}, sort_keys=True, ensure_ascii=False)
    return Path(root) / (hashlib.sha256(material.encode("utf-8")).hexdigest() + ".json")


def parse_answer(content):
    """The model's corrected lines from its JSON answer; reasoning is never part of it."""
    if isinstance(content, dict):
        data = content
    else:
        text = (content or "").strip()
        # Some templates still wrap the JSON in a fenced block or a closed <think> section.
        text = re.sub(r"(?s)^.*</think>", "", text).strip()
        text = re.sub(r"(?s)^```(?:json)?\s*(.*?)\s*```$", r"\1", text)
        data = json.loads(text)
    lines = data.get("lines") if isinstance(data, dict) else None
    if not isinstance(lines, list):
        raise ValueError("The correction model did not return a list of lines.")
    return [{"id": str(item.get("id")), "text": str(item.get("text", ""))} for item in lines if isinstance(item, dict)]


def _set_text(word, text):
    original = word.get("Original", word["Text"])
    word["Text"] = text
    if text == original:
        word.pop("Original", None)
    else:
        word["Original"] = original


def _keep(output, group, replacement):
    """Keeps a word or phrase the model replaced as recognized, with the capital letter and
    closing punctuation the model gave its replacement."""
    first, last = output[group[0]], output[group[-1]]
    text = first["Text"].lstrip()
    if replacement[0][:1].isupper() and text[:1].islower():
        _set_text(first, text[:1].upper() + text[1:])
    closing = re.search(r"[^\w']+$", replacement[-1])
    if closing and not re.search(r"[^\w']$", last["Text"].rstrip()):
        _set_text(last, last["Text"].strip() + closing.group())


def apply_line(words, corrected):
    """The line's words with the model's corrections, and whether they were accepted.
    A word replaced by one that does not sound alike (a digit, a synonym, or a misheard
    word) keeps its recognized text while the line's other
    edits apply. An edit that would add or drop a word or replace an informal spoken
    form, or a line whose words would mostly be kept that way, rejects the whole line:
    it was rewritten rather than corrected, so none of its edits can be trusted, and its
    words come back as unchanged copies."""
    tokens = []
    for token in (t.replace("~", "") for t in corrected.split()):
        if not token:
            continue
        if not key(token) and tokens:
            tokens[-1] += token  # punctuation the model separated with a space: "right ?"
        elif key(token):
            tokens.append(token)
    present = [i for i, word in enumerate(words) if key(word["Text"])]
    old = [key(words[i]["Text"]) for i in present]
    new = [key(t) for t in tokens]
    output = [dict(word) for word in words]
    blocks, kept = {}, 0
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, old, new, autojunk=False).get_opcodes():
        group, replacement = present[i1:i2], tokens[j1:j2]
        if tag != "equal" and (tag != "replace" or any(o in SPOKEN_FORMS for o in old[i1:i2])):
            return [dict(word) for word in words], False
        joined_old, joined_new = "".join(old[i1:i2]).replace("'", ""), "".join(new[j1:j2]).replace("'", "")
        small, paired = max(i2 - i1, j2 - j1) <= 3, i2 - i1 == j2 - j1
        if tag == "equal" or (paired and all(sound_alike(o, n) for o, n in zip(old[i1:i2], new[j1:j2]))):
            for k in range(i2 - i1):
                if replacement[k] != words[group[k]]["Text"]:
                    _set_text(output[group[k]], replacement[k])
        elif small and joined_old == joined_new:
            # A merge or split ("any more" / "anymore").
            blocks[group[0]] = (group, replacement)
        elif paired:
            for k in range(i2 - i1):
                if sound_alike(old[i1 + k], new[j1 + k]):
                    _set_text(output[group[k]], replacement[k])
                else:
                    _keep(output, group[k:k + 1], replacement[k:k + 1]); kept += 1
        elif small and misheard_alike(joined_old, joined_new):
            # A misheard phrase: "should of" stays as recognized.
            _keep(output, group, replacement); kept += i2 - i1
        else:
            return [dict(word) for word in words], False
    # Mostly other words: a paraphrase, or the answer for a different line.
    if kept * 2 > len(old):
        return [dict(word) for word in words], False
    if not blocks:
        return output, True
    result, skip = [], set()
    for index, word in enumerate(output):
        if index in skip:
            continue
        if index not in blocks:
            result.append(word)
            continue
        group, replacement = blocks[index]
        skip.update(group)
        first, last = words[group[0]], words[group[-1]]
        lengths = [len(key(t)) or 1 for t in replacement]
        position, span = first["Start"], last["End"] - first["Start"]
        probabilities = [words[i].get("Probability") for i in group if words[i].get("Probability") is not None]
        for n, token in enumerate(replacement):
            length = span * lengths[n] / sum(lengths)
            item = dict(first, Text=token, Start=position, End=position + length, Probability=min(probabilities) if probabilities else None)
            item["Original"] = " ".join(words[i]["Text"] for i in group) if n == 0 else ""
            result.append(item); position += length
    return result, True


def apply_corrections(sources, results, lines, corrections):
    """Rewrites each source's words in place with the accepted corrections.
    Returns the numbers of corrected and rejected lines."""
    corrected_lines = rejected_lines = 0
    for source in sources:
        words = results[source["Id"]]["Words"]
        rebuilt = []
        for line in (l for l in lines if l["Source"] == source["Id"]):
            part = [words[i] for i in line["Indices"]]
            corrected = corrections.get(line["Id"])
            if corrected is None:
                rebuilt.extend(dict(w) for w in part)
                continue
            fixed, accepted = apply_line(part, corrected)
            if not accepted:
                rejected_lines += 1
            elif any("Original" in w for w in fixed):
                corrected_lines += 1
            rebuilt.extend(fixed)
        results[source["Id"]]["Words"] = rebuilt
    return corrected_lines, rejected_lines


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def run(sources, results, context, settings, root, runtime=None):
    """Corrects every source's words in place. Returns notes for the panel's status."""
    model = settings.get("Corrector")
    if not model:
        return []
    lines = build_lines(sources, results, context)
    windows = plan_windows(lines)
    if not windows:
        return []
    requests = [messages(lines, window) for window in windows]
    answers, pending = {}, []
    for index, request in enumerate(requests):
        path = cache_path(root, model, request)
        if path.is_file():
            answers[index] = json.loads(path.read_text(encoding="utf-8"))
        else:
            pending.append((index, path))
    notes, invalid = [], 0
    if pending:
        try:
            if runtime is None:
                from correction_runtime import create_runtime
                runtime = create_runtime(settings)
            runtime.load(context_length([requests[i] for i, _ in pending]))
            for done, (index, path) in enumerate(pending):
                report("Correcting", completed=done, total=len(pending), detail=runtime.label)
                # An answer is cached only once it parses; one retry for malformed JSON.
                for attempt in range(2):
                    answer = runtime.complete(requests[index], SCHEMA, answer_tokens(requests[index]))
                    try:
                        parse_answer(answer)
                    except (ValueError, json.JSONDecodeError):
                        continue
                    save(path, answer)
                    answers[index] = answer
                    break
                else:
                    invalid += 1
            report("Correcting", completed=len(pending), total=len(pending), detail=runtime.label)
        except RuntimeUnavailable as error:
            notes.append("Correction skipped: " + str(error) + (" Some lines were corrected." if answers else " Subtitles use the recognized wording."))
        finally:
            if runtime is not None:
                runtime.release()
    else:
        report("Using cached corrections")
    if invalid:
        notes.append("The correction model returned unreadable answers for %d of %d parts; those parts keep the recognized wording." % (invalid, len(requests)))
    corrections = {}
    for index, answer in answers.items():
        targets = {lines[i]["Id"]: lines[i] for i in windows[index]["Targets"]}
        for item in parse_answer(answer):
            if item["id"] in targets:
                corrections[item["id"]] = strip_echo(item["text"], targets[item["id"]])
    corrected, rejected = apply_corrections(sources, results, lines, corrections)
    report("Corrections applied", detail="%d lines corrected, %d rewritten lines rejected" % (corrected, rejected))
    return notes
