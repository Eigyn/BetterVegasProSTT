"""Local recognition worker. JSON lines on stdout; diagnostics stay on stderr.

Runs in the recognition environment Setup.cmd installs (runtime\\recognition). It
transcribes with a tier's recognizer, times the words with Qwen3-ForcedAligner Ultra and
corrects them with the correction model; diarization_worker.py runs afterwards.
`worker.py --download MODEL ...` fetches models ahead of their first use.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import unicodedata
from progress_reporting import configure, install_download_hooks, loads_model, model_scope, report
from alignment_boundaries import VERSION as BOUNDARY_VERSION, boundary_windows, apply_boundaries

SAMPLE_RATE = 16000
WORKER_VERSION = "2.2"
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# Model name -> (engine, Hugging Face repository, revision): the recognizers of the
# pipeline tiers (RecognitionPipeline in WorkerClient.cs).
TORCH_MODELS = {
    "parakeet-tdt-0.6b-v2": ("parakeet", "nvidia/parakeet-tdt-0.6b-v2", "ae9ad07059c7c739ffaf932226a8fe64ae2620b0"),
    # Loads the repository's own model code (trust_remote_code), so it is pinned to
    # the revision whose code was reviewed.
    "ark-asr-3b": ("ark-asr", "Edge0/ARK-ASR-3B", "1e28271b79edc97635783bea65abc89195a09ed3"),
    "qwen3-asr-1.7b": ("qwen3-asr", "Qwen/Qwen3-ASR-1.7B", "7278e1e70fe206f11671096ffdd38061171dd6e5"),
}
# These return text only; an aligner times their words in a second stage.
TEXT_ENGINES = {"ark-asr", "qwen3-asr"}
DEFAULT_ALIGNERS = {"ark-asr-3b": "qwen3-ultra", "qwen3-asr-1.7b": "qwen3-ultra"}
# A community fine-tune of Qwen/Qwen3-ForcedAligner-0.6B with a boundary refinement
# head; weights and the reviewed head package are pinned to this revision.
QWEN_ALIGNER = ("17slever17/Qwen3-ForcedAligner-Ultra", "765dc1d714e4c80196782a5df5bfbcb3e1416736")


# stdout carries the JSON-lines protocol. NeMo, Lightning and native libraries print
# progress there, so the protocol keeps a private copy of the handle and everything
# else written to stdout, from Python or native code, goes to stderr.
_protocol = os.fdopen(os.dup(1), "w", encoding="utf-8", newline="\n")
os.dup2(2, 1)
sys.stdout = sys.stderr


def emit(kind, **values):
    _protocol.write(json.dumps({"Type": kind, **values}, ensure_ascii=False) + "\n")
    _protocol.flush()


configure(emit)
MODEL_LABELS = {"parakeet-tdt-0.6b-v2": "Parakeet TDT 0.6B v2", "ark-asr-3b": "ARK-ASR 3B", "qwen3-asr-1.7b": "Qwen3-ASR 1.7B"}


def model_label(name):
    return MODEL_LABELS.get(name, name)


# Decoded audio kept for regenerating (decode with a cache folder), as 16 kHz mono 16-bit:
# about 115 MB per hour. The least recently used spans are removed beyond this size.
AUDIO_CACHE_BYTES = 2 * 1024 ** 3
AUDIO_VERSION = "audio-1"


def recording_name(path, stream, first, count):
    """Cache name of a recording's decoded audio: the file as it is now, with its stream
    and channel selection."""
    stat = Path(path).stat()
    material = json.dumps({"FilePath": str(path), "Size": stat.st_size, "Modified": stat.st_mtime_ns, "Stream": stream,
                           "First": first, "Count": count, "Version": AUDIO_VERSION}, sort_keys=True)
    return hashlib.sha256(material.encode()).hexdigest()[:32]


def span_extent(path):
    """(first sample, samples, whether it runs to the end of the file) of a cached span."""
    _, offset, tail = path.stem.split("-")
    return int(offset), path.stat().st_size // 2, tail == "eof"


def cached_span(cache, name, begin, end):
    """Samples begin:end of a recording (end None: to the end of the file) from a cached
    span that covers them, or None."""
    import numpy as np
    for path in Path(cache).glob(name + "-*.s16"):
        try:
            offset, length, eof = span_extent(path)
        except (ValueError, OSError):
            continue
        # Decoders return a sample or two more or fewer than asked: 10 ms short still covers.
        if offset <= begin and (eof or end is not None and offset + length >= end - SAMPLE_RATE // 100):
            stop = offset + length if end is None else min(end, offset + length)
            try:
                samples = np.fromfile(path, dtype=np.int16, count=max(0, stop - begin), offset=2 * (begin - offset))
                os.utime(path)  # most recently used
            except OSError:
                continue
            return samples.astype(np.float32) / 32768
    return None


def store_span(cache, name, begin, data, eof):
    """Keeps a decoded span, removing the spans of the recording it covers, then the least
    recently used spans beyond AUDIO_CACHE_BYTES."""
    if len(data) > AUDIO_CACHE_BYTES:
        return
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / ("%s-%d-%s.s16" % (name, begin, "eof" if eof else "cut"))
    temporary = target.with_suffix(".tmp")
    temporary.write_bytes(data)
    os.replace(temporary, target)
    end = begin + len(data) // 2
    kept = []
    for path in cache.glob("*.s16"):
        try:
            offset, length, other_eof = span_extent(path)
            used = path.stat()
        except (ValueError, OSError):
            continue
        if path != target and path.stem.startswith(name + "-") and offset >= begin and (eof or not other_eof and offset + length <= end):
            try:
                path.unlink()
                continue
            except OSError:
                pass
        kept.append((used.st_mtime, used.st_size, path))
    total = sum(size for _, size, _ in kept)
    for _, size, path in sorted(kept):
        if total <= AUDIO_CACHE_BYTES:
            break
        if path != target:
            try:
                path.unlink()
                total -= size
            except OSError:
                pass


def decode(ffmpeg, path, start, duration, stream=None, first=0, count=0, cache=None):
    """Mono 16 kHz float32 samples of path from start seconds.

    With a cache folder, audio decoded before that covers the request is read from there,
    and newly decoded audio is kept there. FFmpeg reads a whole multi-track recording to
    extract one stream: after a 1 s trim, an 11.3 GB OBS recording took 48 s to read again.
    """
    import numpy as np
    asked = duration
    if cache is not None:
        name = recording_name(path, stream, first, count)
        begin = int(round(start * SAMPLE_RATE))
        end = None if duration is None else begin + int(round(max(duration, 0.0) * SAMPLE_RATE))
        found = cached_span(cache, name, begin, end)
        if found is not None:
            return found
        # Half a second more than needed: getting less shows the span reaches the end of
        # the file, so diarization's read of the whole recording can reuse an event that
        # spans it. Asked for exactly an MP3's length, FFmpeg returned all of it.
        asked = None if duration is None else max(duration, 0.0) + 0.5
    args = [ffmpeg, "-nostdin", "-v", "error"]
    if start > 0:
        args += ["-ss", "%.6f" % start]
    args += ["-i", str(path)]
    if asked is not None:
        args += ["-t", "%.6f" % max(asked, 0.0)]
    if stream is not None:
        args += ["-map", "0:a:" + str(stream)]
    args += ["-vn"]
    if count > 0:
        terms = "+".join(str(1 / count) + "*c" + str(first + i) for i in range(count))
        args += ["-af", "pan=mono|c0=" + terms]
    args += ["-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "s16le", "-"]
    result = subprocess.run(args, capture_output=True, creationflags=NO_WINDOW)
    if result.returncode:
        raise RuntimeError("Audio extraction failed: " + result.stderr.decode("utf-8", "replace").strip())
    data = result.stdout
    if cache is not None:
        # Shorter than asked means the decode reached the end of the file.
        store_span(cache, name, begin, data, asked is None or len(data) // 2 < int(round(asked * SAMPLE_RATE)) - SAMPLE_RATE // 100)
        if end is not None:
            data = data[:2 * (end - begin)]
    return np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768


def load_audio(source, ffmpeg, delay, cache=None):
    """The audio VEGAS plays for the event, as recognition input."""
    import numpy as np
    rate = float(source["Rate"])
    duration = float(source["Duration"]) * rate
    if duration <= 0 or rate <= 0:
        raise ValueError("Reversed or empty audio events are not supported in this build.")
    # VEGAS media time t is FFmpeg time t - delay.
    start = float(source["Offset"]) - delay
    lead = max(0.0, -start)
    samples = decode(ffmpeg, source["FilePath"], max(0.0, start), duration - lead, source["StreamIndex"],
                     int(source.get("FirstChannel", 0)), int(source.get("ChannelCount", 0)), cache)
    expected = int(round(duration * SAMPLE_RATE))
    if lead > 0:
        samples = np.concatenate([np.zeros(int(round(lead * SAMPLE_RATE)), np.float32), samples])
    missing = expected - len(samples)
    # VEGAS already checked the event against its own media length; decoders report
    # slightly different lengths for the same file (MP3 padding, AAC priming).
    if missing > 0.5 * SAMPLE_RATE:
        raise ValueError("An event extends past its source media. Looped events need explicit handling; no partial transcription was inserted.")
    if missing > 0:
        samples = np.concatenate([samples, np.zeros(missing, np.float32)])
    return samples[:expected]


def best_lag(signal, reference):
    """Offset of reference inside signal with the highest normalized correlation."""
    import numpy as np
    n, m = len(signal), len(reference)
    size = 1 << (n + m - 1).bit_length()
    spectrum = np.fft.rfft(signal, size) * np.conj(np.fft.rfft(reference, size))
    correlation = np.fft.irfft(spectrum, size)[: n - m + 1]
    energy = np.concatenate([[0.0], np.cumsum(signal.astype(np.float64) ** 2)])
    window = energy[m:] - energy[: n - m + 1]
    normalized = correlation / (np.sqrt(np.maximum(window, 0) * np.sum(reference.astype(np.float64) ** 2)) + 1e-12)
    lag = int(np.argmax(normalized))
    return lag, float(normalized[lag])


def measure_delay(source, ffmpeg):
    """Seconds VEGAS's decode lags FFmpeg's, from a VEGAS render of the file."""
    import numpy as np
    reference = decode(ffmpeg, source["SyncReference"], 0, None)
    if len(reference) < SAMPLE_RATE or np.sqrt(np.mean(reference ** 2)) < 1e-3:
        return None
    offset, margin = float(source["SyncOffset"]), 0.5
    # The search covers delays of +-margin. For an event at the start of the file, VEGAS
    # plays priming samples before FFmpeg's first sample, so the part of the window
    # before the file is silence rather than missing.
    start = offset - margin
    lead = max(0.0, -start)
    signal = decode(ffmpeg, source["FilePath"], max(0.0, start), len(reference) / SAMPLE_RATE + 2 * margin - lead, source["StreamIndex"])
    if lead > 0:
        signal = np.concatenate([np.zeros(int(round(lead * SAMPLE_RATE)), np.float32), signal])
    if len(signal) < len(reference):
        signal = np.concatenate([signal, np.zeros(len(reference) - len(signal), np.float32)])
    lag, quality = best_lag(signal, reference)
    delay = offset - start - lag / SAMPLE_RATE
    if quality < 0.6 or abs(delay) > margin - 0.05:
        return None
    return round(delay, 5)


def cache_key(source, model, delay, snap, boost=False):
    stat = Path(source["FilePath"]).stat()
    values = {key: source.get(key) for key in ("FilePath", "Offset", "Duration", "Rate", "StreamIndex", "FirstChannel", "ChannelCount")}
    values.update(Size=stat.st_size, Modified=stat.st_mtime_ns, Model=model, Delay=round(delay, 4), Snap=snap, Worker=WORKER_VERSION)
    # Only when on, so results cached before the setting existed stay valid. The gap-piece
    # rule is part of it, so a stricter rule recomputes what the earlier one let through.
    if boost:
        values["Boost"] = "%s; gap speech %.2f" % (BOOST_FILTER, GAP_SPEECH)
    return hashlib.sha256(json.dumps(values, sort_keys=True).encode()).hexdigest()


def snapshot(repo, revision):
    """Local folder of a Hugging Face repository at a fixed revision (downloaded once).

    One file at a time: without the symlink privilege on Windows, huggingface_hub's
    symlink check races between parallel downloads, and a thread that reads the
    provisional answer fails with WinError 1314. Large files still download in parallel.
    """
    from huggingface_hub import snapshot_download
    return snapshot_download(repo_id=repo, revision=revision, max_workers=1)


def gpu_free():
    """Free GPU memory in bytes. SUBTITLER_GPU_MEMORY_GB caps what this process may use,
    to test smaller cards on a larger one."""
    import torch
    free, total = torch.cuda.mem_get_info()
    limit = os.environ.get("SUBTITLER_GPU_MEMORY_GB")
    if limit:
        cap = float(limit) * 1024 ** 3
        torch.cuda.set_per_process_memory_fraction(min(1.0, cap / total))
        free = min(free, cap - torch.cuda.memory_reserved())
    return free


def sentence_case(text, sentence_start=True):
    """ARK-ASR writes lower case: capitalize sentence starts and the pronoun I. Names
    stay lower case."""
    import re
    text = re.sub(r"(?<![\w'])i(?=(?:'m|'ve|'ll|'d)?(?![\w']))", "I", text)
    text = re.sub(r"([.!?]\s+)([a-z])", lambda m: m.group(1) + m.group(2).upper(), text)
    return text[:1].upper() + text[1:] if sentence_start else text


def release_gpu():
    """Returns the memory of released models to the GPU, so the next stage's model fits."""
    import gc
    gc.collect()
    try:
        import torch
        if torch.cuda.is_available():
            # cuBLAS keeps its workspace after the model is gone, and a cached block is only
            # returned once nothing in it is in use. Inside the block transformers reserves
            # while loading Qwen3-ASR, those 8.5 MB held 3.45 GB that the correction model,
            # in its own process, then could not use.
            clear = getattr(torch._C, "_cuda_clearCublasWorkspaces", None)
            if clear is not None:
                clear()
            torch.cuda.empty_cache()
    except ImportError:
        pass


def torch_device(settings):
    import torch
    requested = settings.get("Device", "auto")
    if requested == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to PyTorch in the NeMo environment. Choose CPU or Auto in the recognition options.")
    if requested == "auto":
        return "cuda" if torch.cuda.is_available() else "cpu"
    return requested


def speech_pieces(samples, longest=30, gap=1.0, pad=0.5):
    """(start, end) sample ranges to recognise: speech found by Silero VAD, padded by
    pad seconds, merged across pauses shorter than gap, at most longest seconds each.

    Long silences are left out. On a gameplay recording with sparse speech, Parakeet
    v3 dropped 40 of 90 words when given the whole event (whole utterances after long
    pauses), and WhisperX credited a 24 s pause to the first word after it. NVIDIA
    recommends clips of 5 to 25 s for these models anyway.
    """
    from faster_whisper.vad import VadOptions, get_speech_timestamps
    regions = get_speech_timestamps(samples, VadOptions(min_silence_duration_ms=300, speech_pad_ms=200, max_speech_duration_s=longest - 2 * pad - 1))
    pieces = []
    for region in regions:
        start = max(0, int(region["start"] - pad * SAMPLE_RATE))
        end = min(len(samples), int(region["end"] + pad * SAMPLE_RATE))
        if pieces and start - pieces[-1][1] <= gap * SAMPLE_RATE and end - pieces[-1][0] <= longest * SAMPLE_RATE:
            pieces[-1][1] = max(pieces[-1][1], end)
        else:
            pieces.append([start, end])
    return [(start, end) for start, end in pieces if end > start]


# Silero's mean speech probability over a gap piece, at least. The gold set's quiet court
# questions scored 0.20-0.50 once levelled. A sniffle and a cough on a clean close
# microphone scored 0.07, which ARK-ASR still wrote as words ("Hmm.", "Two").
GAP_SPEECH = 0.15


def gap_pieces(samples, pieces, longest=30, gap=1.0, pad=0.5, shortest=0.3, likely=GAP_SPEECH):
    """Speech VAD finds in samples between the pieces already recognized: (start, end)
    sample ranges padded and merged as in speech_pieces, but never into a piece.
    Stretches of less than shortest seconds of speech are left out, and so are pieces
    whose mean speech probability is below likely: levelling raises breaths and other
    non-speech sounds until a moment of them passes for speech."""
    import numpy as np
    from faster_whisper.vad import VadOptions, get_speech_timestamps, get_vad_model
    regions = get_speech_timestamps(samples, VadOptions(min_silence_duration_ms=300, speech_pad_ms=200, max_speech_duration_s=longest - 2 * pad - 1))
    edges = [0] + [edge for piece in sorted(pieces) for edge in piece] + [len(samples)]
    found = []
    for low, high in zip(edges[::2], edges[1::2]):
        merged = []
        for region in regions:
            start, end = max(low, region["start"]), min(high, region["end"])
            if end - start < shortest * SAMPLE_RATE:
                continue
            start, end = max(low, int(start - pad * SAMPLE_RATE)), min(high, int(end + pad * SAMPLE_RATE))
            if merged and start - merged[-1][1] <= gap * SAMPLE_RATE and end - merged[-1][0] <= longest * SAMPLE_RATE:
                merged[-1][1] = max(merged[-1][1], end)
            else:
                merged.append([start, end])
        found += [(start, end) for start, end in merged]
    if found:
        window = 512  # Silero's step: one probability per 32 ms
        audio = np.asarray(samples, np.float32)
        probability = np.asarray(get_vad_model()(np.pad(audio, (0, window - len(audio) % window)))).reshape(-1)
        found = [(start, end) for start, end in found if probability[start // window:end // window + 1].mean() >= likely]
    return found


# Writing systems whose letters mix within one language count as one.
SCRIPT_FAMILIES = {"HIRAGANA": "CJK", "KATAKANA": "CJK", "KATAKANA-HIRAGANA": "CJK"}


def script(text):
    """The writing system of most of text's letters (LATIN, CJK, CYRILLIC...), or None."""
    counts = {}
    for character in text:
        if character.isalpha():
            name = unicodedata.name(character, "UNKNOWN").split()[0]
            name = SCRIPT_FAMILIES.get(name, name)
            counts[name] = counts.get(name, 0) + 1
    return max(counts, key=counts.get) if counts else None


def same_script(texts, reference):
    """texts, with each one written in another writing system than reference emptied.

    ARK-ASR can answer quiet or noisy English in Chinese: a quiet "Who is she?" came back
    as four Chinese characters. Gap pieces are the quietest audio it is given.
    """
    expected = script(" ".join(reference))
    return [text if expected is None or script(text) in (expected, None) else "" for text in texts]


# FFmpeg's dynamic normalizer: gain per 250 ms frame, smoothed over 11 frames, at most 20x.
# Boundary mode (b=1) keeps that gain at the event's edges; without it the gain fades to
# 1 over the first and last 2 s, so a quiet question that opens an event stayed quiet.
BOOST_FILTER = "dynaudnorm=f=250:g=11:m=20:b=1"


def boost_quiet_speech(samples, ffmpeg):
    """samples with quiet stretches raised toward the level of the loud ones, for
    BoostQuietSpeech. Text engines recognize from this copy only the gap_pieces: speech
    VAD finds in it between the pieces found in the original. Everything else is
    detected, recognized and timed as without the setting. Parakeet, which finds speech
    itself, ignores the setting; boosting what it hears throughout
    is what cost ARK-ASR its sentence breaks.

    On a courtroom recording the examiner was 20-25 dB quieter than the witness: Silero
    VAD left most questions out (WER 45%, 29% with gap pieces). Recognizing everything
    boosted reached 22% there, but raised WER on recordings without a quiet speaker by up
    to 1.4 points, since raising the pauses between turns cost ARK-ASR sentence breaks.
    Gap pieces left those recordings unchanged. The filter only scales the samples, so
    the length and timing stay the same.
    """
    import numpy as np
    result = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "-",
                             "-af", BOOST_FILTER, "-f", "f32le", "-"], input=np.asarray(samples, np.float32).tobytes(),
                            capture_output=True, creationflags=NO_WINDOW)
    if result.returncode:
        raise RuntimeError("Boosting quiet speech failed: " + result.stderr.decode("utf-8", "replace").strip())
    boosted = np.frombuffer(result.stdout, np.float32)
    return np.concatenate([boosted, np.zeros(max(0, len(samples) - len(boosted)), np.float32)])[:len(samples)]


class ParakeetEngine:
    def __init__(self, name, repo, revision, settings):
        self.name, self.repo, self.revision, self.settings = name, repo, revision, settings
        self.device = torch_device(settings)
        self.model = None

    @loads_model("Transcriber", lambda self: model_label(self.name))
    def load(self):
        import nemo.collections.asr as nemo_asr
        from nemo.collections.asr.parts.utils.asr_confidence_utils import ConfidenceConfig, ConfidenceMethodConfig
        from omegaconf import OmegaConf, open_dict
        package = Path(snapshot(self.repo, self.revision)) / (self.repo.split("/")[1] + ".nemo")
        self.model = nemo_asr.models.ASRModel.restore_from(str(package), map_location=self.device)
        self.model.eval()
        # Per-word confidence (lowest token entropy confidence) for the panel's
        # low-confidence highlighting; names and numbers score lowest.
        decoding = self.model.cfg.decoding
        with open_dict(decoding):
            decoding.confidence_cfg = OmegaConf.structured(ConfidenceConfig(
                preserve_word_confidence=True, preserve_token_confidence=True, aggregation="min",
                method_cfg=ConfidenceMethodConfig(name="entropy", entropy_type="tsallis", alpha=0.5, entropy_norm="exp")))
        self.model.change_decoding_strategy(decoding, verbose=False)

    def transcribe(self, samples, label):
        import torch
        if self.model is None:
            self.load()
        pieces = speech_pieces(samples)
        if not pieces:
            return []
        with torch.inference_mode():
            output = self.model.transcribe([samples[start:end] for start, end in pieces], batch_size=8, timestamps=True,
                                           return_hypotheses=True, num_workers=0, verbose=False)
        hypotheses = output[0] if isinstance(output, tuple) else output
        result = []
        for (start, _), hypothesis in zip(pieces, hypotheses):
            words = (hypothesis.timestamp or {}).get("word", [])
            confidence = list(hypothesis.word_confidence or [])
            if len(confidence) != len(words):
                confidence = [None] * len(words)
            offset = start / SAMPLE_RATE
            result += [{"Text": word["word"].strip(), "Start": offset + float(word["start"]), "End": offset + float(word["end"]),
                        "Probability": None if score is None else round(float(score), 4)}
                       for word, score in zip(words, confidence) if word["word"].strip()]
        return result


class Qwen3AsrEngine:
    """Text only, with punctuation and capitals; the aligner times its words. About 4.4 GB
    of GPU memory in bfloat16, so it fits beside VEGAS on an 8 GB card, where ARK-ASR does
    not. The Medium tier's recognizer."""
    LONGEST = 30
    BATCH = 8

    def __init__(self, name, repo, revision, settings):
        self.name, self.repo, self.revision = name, repo, revision
        self.device = torch_device(settings)
        self.model = None

    @loads_model("Transcriber", lambda self: model_label(self.name))
    def load(self):
        import torch
        from qwen_asr import Qwen3ASRModel
        path = snapshot(self.repo, self.revision)
        self.model = Qwen3ASRModel.from_pretrained(path, max_inference_batch_size=self.BATCH, max_new_tokens=256,
                                                   torch_dtype=torch.bfloat16 if self.device == "cuda" else torch.float32,
                                                   device_map="cuda:0" if self.device == "cuda" else "cpu")

    def transcribe_pieces(self, samples, pieces, label):
        if self.model is None:
            self.load()
        texts = []
        for first in range(0, len(pieces), self.BATCH):
            report("Transcribing", completed=first, total=len(pieces), detail=label)
            batch = [(samples[start:end], SAMPLE_RATE) for start, end in pieces[first:first + self.BATCH]]
            texts += [result.text.strip() for result in self.model.transcribe(batch)]
        report("Transcribing", completed=len(pieces), total=len(pieces), detail=label)
        return texts

    def release(self):
        self.model = None
        release_gpu()


class ArkAsrEngine:
    """Text only. ARK-ASR's Whisper-style encoder takes at most 30 s per call."""
    LONGEST = 30
    WEIGHTS = 8.2 * 1024 ** 3

    def __init__(self, name, repo, revision, settings):
        self.name, self.repo, self.revision = name, repo, revision
        self.device = torch_device(settings)
        self.model = None

    @loads_model("Transcriber", lambda self: model_label(self.name))
    def load(self):
        import torch
        from transformers import AutoModelForCausalLM, AutoProcessor, AutoTokenizer
        path = snapshot(self.repo, self.revision)
        self.dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        self.processor = AutoProcessor.from_pretrained(path, trust_remote_code=True)
        self.tokenizer = AutoTokenizer.from_pretrained(path, trust_remote_code=True)
        options = dict(trust_remote_code=True, torch_dtype=self.dtype, attn_implementation="sdpa")
        free = gpu_free() if self.device == "cuda" else 0
        # 8.2 GB of weights plus room to decode. On a smaller GPU the layers that do not
        # fit stay in system memory: slower, but it runs.
        split = self.device == "cuda" and free < self.WEIGHTS + 1.5 * 1024 ** 3
        if split:
            options.update(device_map="auto", max_memory={0: int(max(free - 1.5 * 1024 ** 3, 1024 ** 3)), "cpu": 64 * 1024 ** 3})
        self.model = AutoModelForCausalLM.from_pretrained(path, **options)
        if not split:
            self.model = self.model.to(self.device)
        self.model.eval()
        if split:
            report("Using GPU + system memory", detail=self.name + " needs about 9.5 GB of GPU memory and " + format(free / 1024 ** 3, ".1f") +
                   " GB is free; part of it runs from system memory.")
        eos = self.tokenizer.eos_token_id
        keep = {eos} if isinstance(eos, int) else set(eos or [])
        # Special tokens other than end-of-text would start a non-transcription answer.
        banned = set(self.tokenizer.all_special_ids) - keep
        banned.update(i for token, i in self.tokenizer.get_added_vocab().items() if token.startswith("<") and token.endswith(">") and i not in keep)
        self.banned = [[i] for i in sorted(banned)]

    def transcribe_pieces(self, samples, pieces, label):
        import torch
        if self.model is None:
            self.load()
        texts = []
        for index, (start, end) in enumerate(pieces):
            report("Transcribing", completed=index, total=len(pieces), detail=label)
            conversation = [{"role": "user", "content": [{"type": "audio", "array": samples[start:end]},
                                                         {"type": "text", "text": "Please transcribe this audio."}]}]
            inputs = self.processor.apply_chat_template(conversation, add_generation_prompt=True, return_tensors="pt", sampling_rate=SAMPLE_RATE,
                                                        audio_padding="longest", text_kwargs={"padding": "longest"},
                                                        audio_max_length=self.LONGEST * SAMPLE_RATE).to(self.device)
            if "audios" in inputs:
                inputs["audios"] = inputs["audios"].to(dtype=self.dtype)
            with torch.inference_mode():
                output = self.model.generate(**inputs, do_sample=False, max_new_tokens=256, pad_token_id=self.tokenizer.pad_token_id,
                                             eos_token_id=self.tokenizer.eos_token_id, bad_words_ids=self.banned)
            text = self.tokenizer.batch_decode(output[:, inputs["input_ids"].shape[1]:], skip_special_tokens=True)[0].strip()
            # A piece starts a sentence unless the previous one stopped mid-sentence.
            texts.append(sentence_case(text, not texts or texts[-1].rstrip().endswith((".", "!", "?"))))
            report("Transcribing", completed=index + 1, total=len(pieces), detail=label)
        return texts

    def release(self):
        self.model = None
        release_gpu()


def comparable(text):
    import unicodedata
    return "".join(c for c in unicodedata.normalize("NFKC", text).lower() if c.isalnum() or c == "'")


def map_words(original, timed):
    """(start, end) for each recognizer word from an aligner's word list, or None.

    Aligners return their own normalized tokens: lower case, no punctuation, and
    sometimes a word split in two or two merged. Tokens are matched by text; where the
    two lists disagree a group of words shares the group's span by length.
    """
    import difflib
    keys = [comparable(word) for word in original]
    spans = [None] * len(original)
    present = [i for i, key in enumerate(keys) if key]
    tokens = [comparable(t[0]) for t in timed]
    matcher = difflib.SequenceMatcher(None, [keys[i] for i in present], tokens, autojunk=False)
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            for k in range(i2 - i1):
                spans[present[i1 + k]] = (timed[j1 + k][1], timed[j1 + k][2])
        elif tag == "replace":
            group = [present[i] for i in range(i1, i2)]
            start, end = timed[j1][1], timed[j2 - 1][2]
            total = sum(len(keys[i]) for i in group)
            position = start
            for i in group:
                length = (end - start) * len(keys[i]) / total
                spans[i] = (position, position + length)
                position += length
    return spans


def timed_words(piece, spans_by_word, probability=None):
    """Word dicts for a piece's transcript from per-word spans relative to the piece."""
    words = []
    for word, span in zip(piece["Text"].split(), spans_by_word):
        words.append({"Text": word, "Start": None if span is None else piece["Start"] + span[0], "End": None if span is None else piece["Start"] + span[1],
                      "Probability": probability, "SegmentStart": piece["Start"], "SegmentEnd": piece["End"]})
    return words


class QwenAligner:
    """Qwen3-ForcedAligner Ultra with its Local Head, which refines the base model's
    80 ms timestamp grid to 10 ms. Supports 11 languages."""
    label = "Qwen3-ForcedAligner Ultra"
    BATCH = 8

    def __init__(self, settings, root):
        self.device = torch_device(settings)
        self.model = None

    @loads_model("Aligner", lambda self: self.label)
    def load(self):
        import torch
        from qwen_fa_ultra import LocalBoundaryAligner
        path = snapshot(*QWEN_ALIGNER)
        self.model = LocalBoundaryAligner.from_pretrained(path, device="cuda:0" if self.device == "cuda" else "cpu",
                                                          dtype=torch.bfloat16 if self.device == "cuda" else torch.float32, head_batch_size=512)

    @staticmethod
    def language(text):
        if any("\uac00" <= c <= "\ud7a3" for c in text):
            return "Korean"
        if any("\u3040" <= c <= "\u30ff" for c in text):
            return "Japanese"
        if any("\u4e00" <= c <= "\u9fff" for c in text):
            return "Chinese"
        return "English"

    def align(self, samples, pieces, label):
        pieces = [p for p in pieces if comparable(p["Text"])]
        groups = self.align_pieces(samples, pieces, label)
        plans = boundary_windows(pieces, groups)
        if plans:
            report("Refining alignment", completed=0, total=len(plans), detail=label)
            repaired = self.align_pieces(samples, [p["Piece"] for p in plans], label)
            groups = apply_boundaries(groups, plans, repaired)
            report("Refining alignment", completed=len(plans), total=len(plans), detail=label)
        return [word for group in groups for word in group]

    def align_pieces(self, samples, pieces, label):
        groups = []
        for first in range(0, len(pieces), self.BATCH):
            batch = pieces[first:first + self.BATCH]
            report("Aligning", completed=first, total=len(pieces), detail=label)
            audio = [(samples[int(round(p["Start"] * SAMPLE_RATE)):int(round(p["End"] * SAMPLE_RATE))], SAMPLE_RATE) for p in batch]
            results = self.model.align(audio=audio, text=[p["Text"] for p in batch], language=[self.language(p["Text"]) for p in batch])
            for piece, result in zip(batch, results):
                timed = [(item.text, item.start_time, item.end_time) for item in result]
                groups.append(interpolate(timed_words(piece, map_words(piece["Text"].split(), timed))))
            report("Aligning", completed=first + len(batch), total=len(pieces), detail=label)
        return groups

    def release(self):
        self.model = None
        release_gpu()


ALIGNERS = {"qwen3-ultra": QwenAligner}


LEVEL_HOP = 0.005


def speech_levels(samples):
    """dB level of each 20 ms window, starting every 5 ms."""
    import numpy as np
    hop = int(SAMPLE_RATE * LEVEL_HOP)
    count = len(samples) // hop
    energy = np.empty(count, np.float64)
    step = hop * 100000
    for begin in range(0, count * hop, step):
        part = samples[begin:min(begin + step, count * hop)].astype(np.float64)
        energy[begin // hop:begin // hop + len(part) // hop] = (part.reshape(-1, hop) ** 2).sum(axis=1)
    total = np.concatenate([[0.0], np.cumsum(energy)])
    index = np.arange(count)
    window = (total[np.minimum(index + 4, count)] - total[index]) / (4 * hop)
    return 20 * np.log10(np.sqrt(np.maximum(window, 0)) + 1e-9)


def trim_silence(words, samples, longest=0.3, rise=18.0):
    """A word never spans a pause: one that contains at least longest seconds of
    silence keeps only its loudest stretch of speech.

    WhisperX's CTC alignment credits silent frames to whichever character its path is
    on. On noisy gameplay audio the path can step onto the next word's first letter at
    the end of the previous phrase, so that word started before the whole pause (one
    "Yeah," lasted 24 s). Whisper can likewise stretch a word's end over a pause.
    """
    import numpy as np
    level = speech_levels(samples)
    n = len(level)
    out = []
    for word in words:
        word = dict(word)
        first, last = int(round(word["Start"] / LEVEL_HOP)), min(n - 1, int(round(word["End"] / LEVEL_HOP)))
        if word["End"] - word["Start"] >= longest + 0.1 and last > first:
            span = level[first:last + 1]
            floor, peak = np.percentile(span, 10), span.max()
            if peak - floor >= rise:
                loud = span >= max(floor + 12, peak - 30)
                # Runs of speech separated by at least longest seconds of silence.
                runs, begin, quiet = [], None, 0
                for i, value in enumerate(loud):
                    if value:
                        if begin is None:
                            begin = i
                        elif quiet * LEVEL_HOP >= longest:
                            runs.append((begin, i - quiet - 1))
                            begin = i
                        quiet = 0
                    elif begin is not None:
                        quiet += 1
                if begin is not None:
                    runs.append((begin, len(loud) - 1 - quiet))
                if runs:
                    # The run with the most speech; on a tie the later one, since the
                    # stray start belongs to the end of the previous phrase.
                    best = max(runs, key=lambda run: (int(loud[run[0]:run[1] + 1].sum()), run[0]))
                    start = word["Start"] + best[0] * LEVEL_HOP
                    end = min(word["End"], word["Start"] + best[1] * LEVEL_HOP + 0.02)
                    if (start - word["Start"] >= longest or word["End"] - end >= longest) and end > start:
                        word["Start"], word["End"] = start, end
        out.append(word)
    return out


def snap_to_speech(words, samples, pause=0.15, rise=18.0):
    """Moves word edges next to pauses onto the audio's speech onset and offset.

    Engines place words that follow silence early or late: measured on speech with
    known word times, Parakeet starts them ~130 ms early, Whisper and WhisperX miss by
    ~50-90 ms either way. Where the audio has a clear pause (18 dB between the word's
    peak and the silence before it) the edge is the first 5 ms step within 24 dB of
    the peak; a median error of ~10 ms instead. Noisy audio without a clear pause
    keeps the engine's times.
    """
    import numpy as np
    if not words:
        return words
    level = speech_levels(samples)
    n = len(level)
    if n == 0:
        return words
    end_of_audio = len(samples) / SAMPLE_RATE

    def at(t):
        return max(0, min(n - 1, int(round(t / LEVEL_HOP))))
    out = [dict(word) for word in words]
    for i, word in enumerate(out):
        before = out[i - 1]["End"] if i else 0.0
        after = out[i + 1]["Start"] if i + 1 < len(out) else end_of_audio
        if word["Start"] - before >= pause:
            lo, hi = at(max(before, word["Start"] - 0.1)), at(min(word["End"] - 0.02, word["Start"] + 0.4))
            floor = np.percentile(level[at(max(0.0, word["Start"] - 0.6)):at(word["Start"]) + 1], 10)
            peak = level[at(word["Start"]):at(min(word["End"], word["Start"] + 0.35)) + 1].max()
            if hi > lo and peak - floor >= rise:
                threshold = max(floor + 12, peak - 24)
                above = np.flatnonzero(level[lo:hi + 1] >= threshold)
                if len(above) and np.all(level[max(0, lo + above[0] - 10):lo + above[0]] < threshold):
                    word["Start"] = min((lo + above[0]) * LEVEL_HOP, word["End"] - 0.02)
        if after - word["End"] >= pause:
            lo, hi = at(max(word["Start"], word["End"] - 0.15)), at(min(after, word["End"] + 0.15))
            floor = np.percentile(level[at(word["End"]):at(min(end_of_audio, word["End"] + 0.6)) + 1], 10)
            peak = level[at(max(word["Start"], word["End"] - 0.3)):at(word["End"]) + 1].max()
            if hi > lo and peak - floor >= rise:
                threshold = max(floor + 12, peak - 30)
                above = np.flatnonzero(level[lo:hi + 1] >= threshold)
                if len(above):
                    word["End"] = max((lo + above[-1]) * LEVEL_HOP + 0.02, word["Start"] + 0.02)
    return out


def interpolate(words):
    """Times for words the aligner could not place (numbers, symbols): spread them
    between their aligned neighbours inside their segment."""
    index = 0
    while index < len(words):
        if words[index]["Start"] is not None and words[index]["End"] is not None:
            index += 1
            continue
        end = index
        while end < len(words) and (words[end]["Start"] is None or words[end]["End"] is None):
            end += 1
        before = words[index - 1]["End"] if index > 0 else words[index]["SegmentStart"]
        after = words[end]["Start"] if end < len(words) else words[end - 1]["SegmentEnd"]
        if before is None or after is None or after < before:
            before = before if before is not None else (after or 0.0)
            after = max(before, after if after is not None else before)
        step = (after - before) / (end - index)
        for k in range(index, end):
            words[k]["Start"] = before + step * (k - index)
            words[k]["End"] = before + step * (k - index + 1)
        index = end
    return [{"Text": w["Text"], "Start": float(w["Start"]), "End": float(w["End"]), "Probability": w["Probability"]} for w in words]


def create_engine(settings, root):
    name = settings.get("Model")
    if name not in TORCH_MODELS:
        raise ValueError("Unknown recognition model: %s" % name)
    kind, repo, revision = TORCH_MODELS[name]
    return {"parakeet": ParakeetEngine, "ark-asr": ArkAsrEngine, "qwen3-asr": Qwen3AsrEngine}[kind](name, repo, revision, settings)


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def finish(words, samples, snap):
    original = words
    words = trim_silence(words, samples)
    words = snap_to_speech(words, samples) if snap else words
    for before, after in zip(original, words):
        if before.get("_KeepAlignment"):
            after["Start"], after["End"] = before["Start"], before["End"]
        after.pop("_KeepAlignment", None)
    return words


def run(request):
    settings = request["Settings"]
    root = Path(settings["CacheDirectory"])
    root.mkdir(parents=True, exist_ok=True)
    # Every engine downloads into the project's cache, not the user profile.
    os.environ.setdefault("HF_HUB_CACHE", str(root / "models"))
    os.environ.setdefault("HF_MODULES_CACHE", str(root / "modules"))
    os.environ.setdefault("TORCH_HOME", str(root / "torch"))
    os.environ.setdefault("NLTK_DATA", str(root / "nltk"))
    # Native/console bars stay quiet. The adapters emit real byte progress over
    # the JSON protocol for the VEGAS panel instead.
    os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
    install_download_hooks()
    model_name = settings.get("Model")
    if model_name not in TORCH_MODELS:
        raise ValueError("Unknown recognition model: %s" % model_name)
    kind = TORCH_MODELS[model_name][0]
    aligner_name = settings.get("Aligner") or (DEFAULT_ALIGNERS.get(model_name) if kind in TEXT_ENGINES else None)
    if aligner_name is not None and aligner_name not in ALIGNERS:
        raise ValueError("Unknown aligner: " + aligner_name)
    snap = bool(settings.get("SnapToSpeech", True))
    # On by default. Engines that find speech themselves ignore it (boost_quiet_speech).
    boost = bool(settings.get("BoostQuietSpeech", True)) and kind in TEXT_ENGINES
    force = request.get("Force", False)
    ffmpeg = settings["FFmpegPath"]
    sources = request["Sources"]
    report("Preparing audio")
    delays, results, pending = {}, {}, []
    for source in sources:
        delay, measured = source.get("DecoderDelay"), False
        if delay is None and source.get("SyncReference"):
            reference = source["SyncReference"]
            if reference not in delays:
                delays[reference] = measure_delay(source, ffmpeg)
                found = delays[reference]
                report("Preparing audio", detail="VEGAS plays " + Path(source["FilePath"]).name +
                       (" " + format(abs(found) * 1000, ".0f") + " ms " + ("later" if found > 0 else "earlier") + " than FFmpeg decodes it; compensating."
                        if found else (" in sync with FFmpeg." if found == 0 else ": audio sync could not be measured; assuming none.")))
            delay = delays[reference]
            measured = delay is not None
        delay = float(delay or 0.0)
        aligned_model = model_name + ("+" + aligner_name if aligner_name else "")
        if aligner_name == "qwen3-ultra": aligned_model += "+" + BOUNDARY_VERSION
        job = {"Source": source, "Delay": delay, "Measured": measured,
               "Cache": root / (cache_key(source, aligned_model, delay, snap, boost) + ".json")}
        if job["Cache"].is_file() and not force:
            job["Words"] = json.loads(job["Cache"].read_text(encoding="utf-8"))
        else:
            pending.append(job)
        results[source["Id"]] = job
    device = "cache"
    if not pending:
        report("Using cached recognition")
    if pending and aligner_name is None:
        engine = create_engine(settings, root)
        for index, job in enumerate(pending):
            report("Preparing audio", detail=job["Source"]["Label"])
            samples = load_audio(job["Source"], ffmpeg, job["Delay"], root / "audio")
            if engine.model is None:
                engine.load()
            report("Transcribing", detail=job["Source"]["Label"])
            job["Words"] = finish(engine.transcribe(samples, job["Source"]["Label"]), samples, snap)
            report("Caching transcription", detail=job["Source"]["Label"])
            save(job["Cache"], job["Words"])
        device = getattr(engine, "device", None) or settings.get("Device", "auto")
        report("Unloading", model_type="Transcriber", model_name=model_label(model_name))
        del engine
        release_gpu()
    elif pending:
        # Two stages, so the recognizer and the aligner are never in GPU memory together:
        # transcribe every source (each transcript cached on its own), release the
        # recognizer, then load the aligner and align the cached transcripts.
        engine = None
        for index, job in enumerate(pending):
            source = job["Source"]
            report("Preparing audio", detail=source["Label"])
            job["Samples"] = load_audio(source, ffmpeg, job["Delay"], root / "audio")
            transcript = root / (cache_key(source, model_name, job["Delay"], "transcript", boost) + ".json")
            if transcript.is_file() and not force:
                job["Pieces"] = json.loads(transcript.read_text(encoding="utf-8"))
                report("Using cached transcription", detail=source["Label"])
                continue
            if engine is None:
                engine = create_engine(settings, root)
            report("Transcribing", detail=source["Label"])
            if kind in TEXT_ENGINES:
                pieces = speech_pieces(job["Samples"], engine.LONGEST)
                texts = engine.transcribe_pieces(job["Samples"], pieces, source["Label"]) if pieces else []
                if boost:
                    # Quiet speech the plain detection skipped, recognized from the boosted
                    # audio. The pieces it did find keep their output, and the aligner and
                    # finish() time every word on the original samples.
                    heard = boost_quiet_speech(job["Samples"], ffmpeg)
                    extra = gap_pieces(heard, pieces, engine.LONGEST)
                    found = same_script(engine.transcribe_pieces(heard, extra, source["Label"]) if extra else [], texts)
                    pieces = pieces + [piece for piece, text in zip(extra, found) if text]
                    texts = texts + [text for text in found if text]
                job["Pieces"] = sorted(({"Start": start / SAMPLE_RATE, "End": end / SAMPLE_RATE, "Text": text} for (start, end), text in zip(pieces, texts)),
                                       key=lambda piece: piece["Start"])
            else:
                # A timestamped recognizer can also use an explicitly selected
                # forced aligner. Preserve its words as the independent transcript.
                native_cache = root / (cache_key(source, model_name, job["Delay"], snap) + ".json")
                if native_cache.is_file() and not force:
                    timed = json.loads(native_cache.read_text(encoding="utf-8"))
                    report("Using cached transcription", detail=source["Label"])
                else:
                    if engine.model is None: engine.load()
                    timed = engine.transcribe(job["Samples"], source["Label"])
                job["Pieces"] = []
                for word in timed:
                    if not job["Pieces"] or word["End"] - job["Pieces"][-1]["Start"] > 25 or word["Start"] - job["Pieces"][-1]["End"] > .6:
                        job["Pieces"].append({"Start": max(0, word["Start"] - .1), "End": word["End"], "Text": word["Text"].strip()})
                    else:
                        piece = job["Pieces"][-1]
                        piece["End"] = word["End"]
                        piece["Text"] += " " + word["Text"].strip()
            report("Caching transcription", detail=source["Label"])
            save(transcript, job["Pieces"])
        if engine is not None:
            device = engine.device
            report("Unloading", model_type="Transcriber", model_name=model_label(model_name))
            if hasattr(engine, "release"): engine.release()
            engine = None
            release_gpu()
        aligner = ALIGNERS[aligner_name](settings, root)
        aligner.load()
        for index, job in enumerate(pending):
            report("Aligning", detail=job["Source"]["Label"])
            job["Words"] = finish(aligner.align(job["Samples"], job["Pieces"], job["Source"]["Label"]), job["Samples"], snap)
            report("Caching alignment", detail=job["Source"]["Label"])
            save(job["Cache"], job["Words"])
            job["Samples"] = None
        if device == "cache":
            device = aligner.device
        report("Unloading", model_type="Aligner", model_name=aligner.label)
        aligner.release()
    # The correction model loads only now, after the recognizer and aligner are released.
    notes = []
    if settings.get("Corrector"):
        import correction
        notes = correction.run(sources, results, request.get("Context") or [], settings, root)
    emit("Result", Sources=[{"Id": s["Id"], "Words": results[s["Id"]]["Words"], "Delay": results[s["Id"]]["Delay"],
                             "Measured": results[s["Id"]]["Measured"]} for s in sources],
         Device=device, Model=model_name, Aligner=aligner_name, Notes=notes)


def download(names, root):
    """Fetches recognizers and the aligner (qwen3-ultra) into root's models folder before
    their first use, for Setup.cmd. Progress goes out as the panel's JSON lines: Downloading
    with the model's label and percentage, then Downloaded once it is complete."""
    os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
    import huggingface_hub
    for name in names:
        repo, revision = QWEN_ALIGNER if name in ALIGNERS else TORCH_MODELS[name][1:]
        label = QwenAligner.label if name in ALIGNERS else model_label(name)
        with model_scope("Model", label):
            huggingface_hub.snapshot_download(repo_id=repo, revision=revision, max_workers=1, cache_dir=str(Path(root) / "models"))
        report("Downloaded", model_name=label)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--request")
    action.add_argument("--download", nargs="+", metavar="MODEL", choices=sorted(TORCH_MODELS) + sorted(ALIGNERS))
    parser.add_argument("--cache", help="the cache folder for --download")
    args = parser.parse_args()
    if args.download:
        download(args.download, args.cache)
        sys.exit(0)
    try:
        run(json.loads(Path(args.request).read_text(encoding="utf-8-sig")))
    except Exception as error:
        import traceback
        traceback.print_exc(file=sys.stderr)
        emit("Error", Message=str(error))
        sys.exit(1)
