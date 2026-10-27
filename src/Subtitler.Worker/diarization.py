"""Local diarization providers and recording-scoped word association.

No ASR dependency: this module runs in the Nemotron diarization environment.
Raw speaker turns are cached independently of recognized words and subtitle style.
"""
import hashlib
import json
import math
from pathlib import Path
from collections import defaultdict

from progress_reporting import loads_model, report

MODELS = {
    "nemotron-3": ("Nemotron 3 Diarization", "nvidia/Nemotron-3-Diarization", "f667ed73aee57d40cc39428eb768b4fd87a0a29e"),
}
VERSION = "1-offline-quality"


def recording_key(source, model, speakers=0, maximum=0):
    path = Path(source["FilePath"]).resolve()
    stat = path.stat()
    identity = [str(path).casefold(), stat.st_size, stat.st_mtime_ns,
                source["StreamIndex"], source.get("FirstChannel", 0), source.get("ChannelCount", 0)]
    # Identities are stable across trims, event moves, playback rate and ASR changes.
    scope = hashlib.sha256(json.dumps(identity).encode()).hexdigest()
    key = hashlib.sha256(json.dumps([identity, MODELS[model], VERSION, speakers]).encode()).hexdigest()
    return scope, key


def limited_key(key, maximum):
    return hashlib.sha256((key + ":voice-cap-v3:" + str(maximum)).encode()).hexdigest() if maximum else key


def normalize_turns(turns):
    result = []
    for turn in turns:
        start, end, speaker = float(turn["Start"]), float(turn["End"]), str(turn["Speaker"])
        if not math.isfinite(start) or not math.isfinite(end) or end <= start or start < 0:
            raise ValueError("The diarizer returned invalid speaker timing.")
        result.append({"Start": start, "End": end, "Speaker": speaker})
    return sorted(result, key=lambda t: (t["Start"], t["End"], t["Speaker"]))


def merge_labels(turns, mapping):
    """Union only same-speaker spans. Keep the complete original speech coverage."""
    by_speaker = defaultdict(list)
    for turn in normalize_turns(turns):
        by_speaker[mapping.get(turn["Speaker"], turn["Speaker"])].append(turn)
    result = []
    for speaker, spans in by_speaker.items():
        start, end = spans[0]["Start"], spans[0]["End"]
        for span in spans[1:]:
            if span["Start"] <= end + 1e-6: end = max(end, span["End"])
            else:
                result.append({"Start": start, "End": end, "Speaker": speaker})
                start, end = span["Start"], span["End"]
        result.append({"Start": start, "End": end, "Speaker": speaker})
    return normalize_turns(result)


def cluster_speaker_labels(turns, embeddings, maximum):
    """Keep well-supported voice anchors, then attach excess labels by cosine.

    Short outliers must not survive at the expense of two established voices.
    The cap only merges labels; it never creates voices to reach an exact count.
    """
    import numpy as np
    labels = list(dict.fromkeys(t["Speaker"] for t in normalize_turns(turns)))
    if maximum <= 0 or len(labels) <= maximum: return {s: s for s in labels}
    durations = {s: sum(t["End"] - t["Start"] for t in merge_labels(turns, {}) if t["Speaker"] == s) for s in labels}
    vectors = {s: np.asarray(embeddings[s], dtype=np.float64) for s in labels}
    if any(not np.isfinite(v).all() or np.linalg.norm(v) < 1e-9 for v in vectors.values()):
        raise ValueError("Not enough usable voice evidence to apply the maximum speaker count. Review the speaker assignments or use automatic counting.")
    vectors = {s: v / np.linalg.norm(v) for s, v in vectors.items()}
    ranked = sorted(labels, key=lambda s: (-durations[s], labels.index(s)))
    reliable = [s for s in ranked if durations[s] >= max(2.5, .08 * sum(durations.values()))]
    # Keep the candidate pool large enough for the requested maximum, including
    # genuinely brief speakers when there aren't enough established voices.
    for speaker in ranked:
        if len(reliable) >= maximum: break
        if speaker not in reliable: reliable.append(speaker)
    anchors = [reliable[0]]
    while len(anchors) < maximum:
        def novelty(speaker):
            distance = min(max(0., 1 - float(vectors[speaker] @ vectors[a])) for a in anchors)
            return distance * math.sqrt(durations[speaker]), durations[speaker], -labels.index(speaker)
        anchors.append(max((s for s in reliable if s not in anchors), key=novelty))
    return {s: s if s in anchors else max(anchors, key=lambda a: (float(vectors[s] @ vectors[a]), durations[a], -labels.index(a)))
            for s in labels}


def speaker_clips(turns, speaker, sample_count):
    """Distributed clean speech excerpts; omit overlap for stronger voice evidence."""
    spans = [(t["Start"], t["End"]) for t in turns if t["Speaker"] == speaker]
    for other in turns:
        if other["Speaker"] == speaker: continue
        next_spans = []
        for start, end in spans:
            if other["End"] <= start or other["Start"] >= end: next_spans.append((start, end)); continue
            if start < other["Start"]: next_spans.append((start, other["Start"]))
            if other["End"] < end: next_spans.append((other["End"], end))
        spans = next_spans
    clean = [(a, b) for a, b in spans if b - a >= .5]
    if not clean:
        clean = [(t["Start"], t["End"]) for t in turns if t["Speaker"] == speaker]
    clips = []
    for start, end in clean:
        length = end - start
        for fraction in ([.2, .5, .8] if length > 8 else [.5]):
            center = start + length * fraction
            a, b = max(start, center - 2), min(end, center + 2)
            left, right = max(0, round(a * 16000)), min(sample_count, round(b * 16000))
            if right - left >= 1600: clips.append((left, right))
    clips.sort()
    if len(clips) > 12: clips = [clips[round(i * (len(clips) - 1) / 11)] for i in range(12)]
    return clips


class SpeakerLimiter:
    def __init__(self):
        self.device = "cpu"
        self.model = None

    @loads_model("Diarizer", lambda self: "WeSpeaker voice matching")
    def load(self):
        import torch
        from huggingface_hub import hf_hub_download
        from pyannote.audio.core.task import Problem, Resolution, Specifications
        from pyannote.audio.pipelines.speaker_verification import PyannoteAudioPretrainedSpeakerEmbedding
        checkpoint = hf_hub_download("pyannote/wespeaker-voxceleb-resnet34-LM", "pytorch_model.bin",
                                    revision="837717ddb9ff5507820346191109dc79c958d614")
        with torch.serialization.safe_globals([torch.torch_version.TorchVersion, Problem, Resolution, Specifications]):
            self.model = PyannoteAudioPretrainedSpeakerEmbedding(checkpoint, device=torch.device("cpu"))

    def apply(self, result, samples, maximum, embedding=None):
        labels = list(dict.fromkeys(t["Speaker"] for t in result["Turns"]))
        if maximum <= 0 or len(labels) <= maximum: return result
        import numpy as np
        import torch
        if embedding is None:
            if self.model is None: self.load()
            embedding = self.model
        vectors = {}
        for index, label in enumerate(labels):
            report("Matching speakers", completed=index, total=len(labels))
            values = []
            for start, end in speaker_clips(result["Turns"], label, len(samples)):
                clip = samples[start:end]
                # Very short labels still retain their speech; padding lets the
                # embedding network run while the resulting assignment is reviewed.
                if len(clip) < 16000: clip = np.tile(clip, math.ceil(16000 / len(clip)))[:16000]
                value = np.asarray(embedding(torch.from_numpy(clip).reshape(1, 1, -1)))[0]
                norm = np.linalg.norm(value)
                if np.isfinite(value).all() and norm > 1e-9: values.append(value / norm)
            if not values: raise ValueError("Not enough voice evidence to merge speaker " + label + ". Review the labels or use automatic counting.")
            vectors[label] = np.mean(values, axis=0)
        mapping = cluster_speaker_labels(result["Turns"], vectors, maximum)
        changed = {old for old, new in mapping.items() if old != new}
        report("Matching speakers", completed=len(labels), total=len(labels))
        return {**result, "Turns": merge_labels(result["Turns"], mapping),
                "Exclusive": merge_labels(result.get("Exclusive", []), mapping),
                "ReviewTurns": [t for t in result["Turns"] if t["Speaker"] in changed]}


def refine_word_boundaries(records, scope, model, turn_onsets=None):
    """Resolve ambiguous edge words only when ASR overlap and phrase context agree.

    Timing alone cannot identify a voice. In particular, never smooth away a
    confidently detected short reply or infer that ASR overlap proves two voices.
    """
    ordered = sorted(records, key=lambda r: r["source_index"])
    reset = [False] * len(ordered)
    for i in range(1, len(ordered)):
        before, current = ordered[i - 1]["word"], ordered[i]["word"]
        reset[i] = current["Start"] < before["Start"] - .08 or current["Start"] < before["End"] - .15

    def sentence_end(word):
        return word["Text"].rstrip().rstrip('"\')]').endswith((".", "?", "!"))

    def context(index, direction):
        chain, previous = [], ordered[index]["word"]
        if direction > 0 and sentence_end(previous): return None
        for step in range(1, 4):
            other = index + direction * step
            if other < 0 or other >= len(ordered): break
            record = ordered[other]; word = record["word"]
            if not record["strong"]: break
            if direction > 0:
                gap = word["Start"] - previous["End"]
            else:
                if sentence_end(word): break
                gap = previous["Start"] - word["End"]
            if gap < -.05 or gap > .25: break
            if chain and record["label"] != chain[0]["label"]: break
            chain.append(record); previous = word
            if direction > 0 and sentence_end(word): break
        if len(chain) < 2 or sum(r["word"]["End"] - r["word"]["Start"] for r in chain) < .22: return None
        return chain[0]["label"]

    def edge_labels(record, tail):
        word = record["word"]
        start, end = word["Start"] + record["offset"], word["End"] + record["offset"]
        point = end - min(.025, (end - start) / 4) if tail else start + min(.025, (end - start) / 4)
        return {speaker for speaker, spans in record["intervals"].items() if any(a <= point < b for a, b in spans)}

    latest_reset = None
    for index, record in enumerate(ordered):
        if reset[index]:
            latest_reset = index
            record["output"]["SpeakerReview"] = True
        word = record["word"]
        # Only an ambiguous, long alignment spanning actual competing turns may
        # move. A clear short interjection keeps its own speaker assignment.
        if record["strong"] or len(record["scores"]) < 2 or word["End"] - word["Start"] < .4: continue
        head, tail = edge_labels(record, False), edge_labels(record, True)
        right, left = context(index, 1), context(index, -1)
        replacement = None
        # A reordered ASR phrase can begin under the previous voice and continue
        # cleanly into the next voice (the reported "me and Thornton" case).
        overlapping_phrase = latest_reset is not None and index - latest_reset <= 3 and \
            word["Start"] - ordered[latest_reset]["word"]["Start"] <= 2
        if overlapping_phrase and right and right != record["label"] and tail == {right} and right in record["scores"]:
            replacement = right
        # Symmetric case: a new overlapping ASR phrase begins before the previous
        # phrase's final stretched word ends. Retain its established left voice.
        next_resets = index + 1 < len(reset) and reset[index + 1]
        if replacement is None and next_resets and left and left != record["label"] and head == {left} and left in record["scores"]:
            replacement = left
        if replacement is not None:
            record["output"]["SpeakerId"] = scope + ":" + model + ":" + replacement
            record["output"]["SpeakerReview"] = True

    # A correctly timed new phrase can still begin while the diarizer leaves the
    # previous voice active. Use its actual new-turn onset plus clear continuation;
    # punctuation, pauses and certain short replies are never smoothed away.
    for index in range(1, len(ordered)):
        previous = ordered[index - 1]
        if not previous["strong"]: continue
        first = ordered[index]["word"]
        gap = first["Start"] - previous["word"]["End"]
        if not .25 <= gap <= 2: continue
        prefix = []
        for other in range(index, min(index + 3, len(ordered))):
            record = ordered[other]; word = record["word"]
            if record["strong"] or len(record["scores"]) < 2 or not record["output"]["SpeakerOverlap"]: break
            if sentence_end(word) or not .03 <= word["End"] - word["Start"] <= .8: break
            if prefix and not -.03 <= word["Start"] - prefix[-1]["word"]["End"] <= .15: break
            prefix.append(record)
        if not prefix: continue
        right = context(index + len(prefix) - 1, 1)
        if not right or right == previous["label"]: continue
        onset = first["Start"] + ordered[index]["offset"]
        if not any(abs(t - onset) <= .12 for t in (turn_onsets or {}).get(right, [])): continue
        if any(right not in r["scores"] for r in prefix): continue
        for record in prefix:
            record["output"]["SpeakerId"] = scope + ":" + model + ":" + right
            record["output"]["SpeakerReview"] = True


def assign_words(words, turns, exclusive, media_offset, scope, model, review_turns=None, maximum=0):
    """Assign once per word, using overlap duration, never a made-up confidence.

    Exclusive turns, from a provider that gives them, resolve competing labels; original overlapping turns
    still flag review. A word without speech evidence remains Unknown. Interval sweep
    keeps long recordings linear in words + locally active speaker turns.
    """
    turns = normalize_turns(turns)
    assignment = normalize_turns(exclusive) if exclusive else turns
    words = list(words)
    word_order = sorted(range(len(words)), key=lambda i: words[i]["Start"])
    sorted_words = [words[i] for i in word_order]
    # With an explicit maximum, uncertainty belongs on an existing voice's row,
    # not on an extra Unknown output track. Keep the provisional assignment flagged.
    starts = [t["Start"] for t in assignment]
    by_end = sorted(assignment, key=lambda t: t["End"])
    ends = [t["End"] for t in by_end]
    def nearest_speaker(start, end):
        import bisect
        previous = bisect.bisect_right(ends, start) - 1
        following = bisect.bisect_left(starts, end)
        before = by_end[previous] if previous >= 0 else None
        after = assignment[following] if following < len(assignment) else None
        if before and after and before["Speaker"] == after["Speaker"]: return before["Speaker"]
        if before and (not after or start - before["End"] <= after["Start"] - end): return before["Speaker"]
        return after["Speaker"] if after else "unknown"
    def evidence(segments):
        cursor, active = 0, []
        for word in sorted_words:
            start, end = word["Start"] + media_offset, word["End"] + media_offset
            while cursor < len(segments) and segments[cursor]["Start"] < end:
                active.append(segments[cursor]); cursor += 1
            active = [t for t in active if t["End"] > start]
            scores = defaultdict(float)
            intervals = defaultdict(list)
            for turn in active:
                a, b = max(start, turn["Start"]), min(end, turn["End"])
                if b > a:
                    intervals[turn["Speaker"]].append((a, b))
            # Union each speaker's intervals so duplicate/adjacent turns don't inflate evidence.
            for speaker, spans in intervals.items():
                left, right = spans[0]
                for a, b in spans[1:]:
                    if a <= right: right = max(right, b)
                    else: scores[speaker] += right - left; left, right = a, b
                scores[speaker] += right - left
            yield word, start, end, scores, intervals
    original = list(evidence(turns))
    selected = list(evidence(assignment))
    reviews = iter(evidence(normalize_turns(review_turns or [])))
    assigned, records = [], []
    for position, ((_, start, end, raw_scores, intervals), (word, _, _, scores, selected_intervals)) in enumerate(zip(original, selected)):
        merged_review = bool(next(reviews)[3])
        ranked = sorted(scores.items(), key=lambda x: (-x[1], x[0]))
        label = ranked[0][0] if ranked else "unknown"
        if not ranked and maximum > 0 and assignment: label = nearest_speaker(start, end)
        duration = max(end - start, 1e-6)
        ambiguous = not ranked or ranked[0][1] < .5 * duration or (len(ranked) > 1 and ranked[1][1] >= .8 * ranked[0][1])
        # Multiple turns inside a word may be a boundary, not simultaneous speech.
        overlap = any(min(b, d) - max(a, c) > .02
                      for i, (speaker, spans) in enumerate(intervals.items())
                      for other, others in list(intervals.items())[i + 1:]
                      for a, b in spans for c, d in others)
        assigned.append({**word, "SpeakerId": scope + ":" + model + ":" + label,
                         "SpeakerReview": bool(ambiguous or overlap or merged_review), "SpeakerOverlap": overlap})
        records.append({"word": word, "output": assigned[-1], "source_index": word_order[position], "label": label,
                        "strong": bool(ranked and not ambiguous and not overlap and ranked[0][1] >= .7 * duration),
                        "scores": scores, "intervals": selected_intervals, "offset": media_offset})
    onsets = defaultdict(list)
    for turn in assignment: onsets[turn["Speaker"]].append(turn["Start"])
    refine_word_boundaries(records, scope, model, onsets)
    return assigned


def selected_device(settings):
    import torch
    device = settings.get("Device", "auto")
    if device == "auto": device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cuda" and not torch.cuda.is_available():
        raise ValueError("CUDA was selected, but this diarization runtime cannot use the GPU. Select auto or cpu.")
    return device


class Provider:
    def __init__(self, settings):
        self.settings = settings
        self.name = settings["DiarizationModel"]
        self.label, self.repo, self.revision = MODELS[self.name]
        self.device = selected_device(settings)
        self.model = None

    @loads_model("Diarizer", lambda self: self.label)
    def load(self):
        if self.name == "nemotron-3":
            from transformers import AutoModelForAudioFrameClassification, AutoProcessor
            self.processor = AutoProcessor.from_pretrained(self.repo, revision=self.revision)
            self.model = AutoModelForAudioFrameClassification.from_pretrained(self.repo, revision=self.revision).to(self.device)
            self.model.eval()
            if int(self.model.config.head_config.num_speakers) != 8:
                raise ValueError("This runtime did not load the eight-speaker Nemotron 3 model.")
            # Offline forward uses the checkpoint's 340 + 40 frame context and
            # persistent speaker cache internally; do not enable low-latency mode.
            if self.model.config.chunk_length != 340 or self.model.config.chunk_right_context != 40:
                raise ValueError("Unexpected Nemotron offline context configuration.")
            self.processor.streaming_modes["offline_quality"] = (340, 40)
            self.processor.set_streaming_mode("offline_quality")

    def diarize(self, samples, wav):
        import torch
        with torch.inference_mode():
            if self.name == "nemotron-3":
                from transformers.models.nemotron3_diarization.modeling_nemotron3_diarization import Nemotron3DiarizationSpeakerCache
                # The offline context, FIFO and cache update settings are retained,
                # but audio features are bounded to one 30.4s window on the GPU.
                # One continuous cache preserves returning speakers for the session.
                cache = Nemotron3DiarizationSpeakerCache(self.model.config.streaming_config,
                    fifo_length=self.model.config.fifo_length,
                    speaker_cache_update_period=self.model.config.speaker_cache_update_period)
                logits, mel_frame, index = [], 0, 0
                total = max(1, math.ceil(len(samples) / 16000 / 27.2))
                while True:
                    first = index == 0
                    start = 0 if first else self.processor.audio_chunk_start(mel_frame)
                    count = self.processor.num_samples_first_audio_chunk if first else self.processor.num_samples_per_audio_chunk
                    last = start + count >= len(samples)
                    inputs = self.processor(samples[start:] if last else samples[start:start + count], sampling_rate=16000,
                                            is_streaming=True, is_first_audio_chunk=first, is_last_audio_chunk=last)
                    inputs = inputs.to(self.model.device, dtype=self.model.dtype)
                    output = self.model(**inputs, speaker_cache=cache)
                    cache = output.speaker_cache
                    logits.append(output.logits.cpu())
                    index += 1
                    report("Detecting speakers", completed=min(index, total), total=total)
                    if last: break
                    mel_frame += self.processor.num_mel_frames_per_step
                segments = self.processor.extract_speaker_dict(torch.cat(logits, dim=1))[0]
                turns = [{"Start": s["Start"], "End": s["End"], "Speaker": str(s["Speaker"])} for s in segments]
                return {"Turns": normalize_turns(turns), "Exclusive": []}

    def release(self):
        self.model = None
        import gc
        gc.collect()
        import torch
        if torch.cuda.is_available(): torch.cuda.empty_cache()
