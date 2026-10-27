"""Second short-lived worker: ASR process has exited before this process starts."""
import argparse
import json
import os
from pathlib import Path
import tempfile

from worker import decode, emit, save
from progress_reporting import install_download_hooks, report
from diarization import MODELS, Provider, SpeakerLimiter, recording_key, limited_key, assign_words


def run(request):
    settings = request["Settings"]
    model = settings.get("DiarizationModel", "nemotron-3")
    if model not in MODELS: raise ValueError("Unknown diarization model: " + model)
    settings["DiarizationModel"] = model
    maximum = int(settings.get("MaxSpeakers", 0))
    if maximum < 0 or maximum > 20: raise ValueError("Max speakers must be between 0 (automatic) and 20.")
    if maximum and int(settings.get("SpeakerCount", 0)) > maximum: raise ValueError("Expected speakers cannot exceed Max speakers.")
    if settings.get("Device") == "cpu": os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ.setdefault("PYANNOTE_METRICS_ENABLED", "0")
    root = Path(settings["CacheDirectory"])
    root.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HUB_CACHE"] = str(root / "models")
    os.environ["TORCH_HOME"] = str(root / "torch")
    os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
    install_download_hooks()
    results = {r["Id"]: r for r in request["Recognition"]}
    # Coherent sessions are full original recordings, with each audio stream/channel
    # selection kept separate. Cuts/repeats/rates share identities and cached turns.
    recordings = {}
    for source in request["Sources"]:
        scope, key = recording_key(source, model, int(settings.get("SpeakerCount", 0)), maximum)
        recordings.setdefault((scope, key), []).append(source)
    provider = None
    limiter = SpeakerLimiter()
    speakers = {}
    try:
        for index, ((scope, key), sources) in enumerate(recordings.items()):
            if not any(results[s["Id"]]["Words"] for s in sources): continue
            source = sources[0]
            cache = root / (key + ".json")
            capped_key = limited_key(key, maximum)
            capped_cache = root / (capped_key + ".json")
            samples = None
            if capped_cache.is_file():
                turns = json.loads(capped_cache.read_text(encoding="utf-8"))
                report("Using cached speakers", detail=source["FilePath"])
            elif cache.is_file():
                turns = json.loads(cache.read_text(encoding="utf-8"))
                report("Using cached speakers", detail=source["FilePath"])
            else:
                report("Preparing speaker audio", completed=index, total=len(recordings), detail=source["FilePath"])
                # Full recording context avoids re-clustering unrelated ASR chunks.
                samples = decode(settings["FFmpegPath"], source["FilePath"], 0, None,
                                 source["StreamIndex"], source.get("FirstChannel", 0), source.get("ChannelCount", 0), root / "audio")
                if len(samples) == 0: raise ValueError("The source stream contains no audio for diarization.")
                import soundfile as sf
                with tempfile.TemporaryDirectory(prefix="diarization-", dir=root) as temporary:
                    wav = Path(temporary) / "recording.wav"
                    sf.write(wav, samples, 16000, subtype="PCM_16")
                    if provider is None:
                        provider = Provider(settings)
                        provider.load()
                    report("Detecting speakers", completed=index, total=len(recordings), detail=source["FilePath"])
                    turns = provider.diarize(samples, wav)
                save(cache, turns)
                report("Caching speakers", completed=index + 1, total=len(recordings))
            if maximum and not capped_cache.is_file():
                if len({t["Speaker"] for t in turns["Turns"]}) > maximum:
                    if samples is None:
                        report("Preparing speaker audio", detail="Reusing cached speaker detection; matching excess voice labels.")
                        samples = decode(settings["FFmpegPath"], source["FilePath"], 0, None,
                                         source["StreamIndex"], source.get("FirstChannel", 0), source.get("ChannelCount", 0), root / "audio")
                    turns = limiter.apply(turns, samples, maximum)
                save(capped_cache, turns)
            del samples
            report("Assigning speakers", detail=source["FilePath"])
            ordered = list(dict.fromkeys(t["Speaker"] for t in turns["Turns"]))
            for s in sources:
                found = results[s["Id"]]
                found["Words"] = assign_words(found["Words"], turns["Turns"], turns.get("Exclusive"),
                                               float(s["Offset"]) - float(found["Delay"]), capped_key, model, turns.get("ReviewTurns"), maximum)
                for word in found["Words"]:
                    sid = word["SpeakerId"]
                    label = sid.rsplit(":", 1)[-1]
                    if label != "unknown" and label not in ordered: ordered.append(label)
                    name = "Unknown" if label == "unknown" else "Speaker " + str(ordered.index(label) + 1)
                    speakers[sid] = {"Id": sid, "Name": name, "Recording": Path(s["FilePath"]).name + " · stream " + str(s["StreamIndex"] + 1)}
    finally:
        if provider:
            report("Unloading", model_type="Diarizer", model_name=provider.label)
            provider.release()
    emit("Result", Sources=list(results.values()), Speakers=list(speakers.values()))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    args = parser.parse_args()
    try:
        run(json.loads(Path(args.request).read_text(encoding="utf-8-sig")))
    except Exception as error:
        import traceback
        traceback.print_exc()
        emit("Error", Message=str(error))
        raise SystemExit(1)
