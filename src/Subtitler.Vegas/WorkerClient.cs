using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Subtitler.Core;

namespace Subtitler.VegasPlugin
{
    // The recognizers of the pipeline tiers. Files lists each model's downloads, relative
    // to the cache directory, for the model manager.
    public sealed class RecognitionModel
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Description { get; private set; }
        public string[] Files { get; private set; }
        public override string ToString() { return Label; }
        public static readonly RecognitionModel[] All = {
            new RecognitionModel { Id = "ark-asr-3b", Label = "ARK-ASR 3B", Files = new[] { "models/models--Edge0--ARK-ASR-3B", "modules" },
                Description = "ARK-ASR 3B (Apache-2.0), 19 languages, top of the Open ASR leaderboard for English. Text only, so the aligner times its words. " +
                    "Needs about 9.5 GB of GPU memory; with less, part of it runs from system memory, which is slower." },
            new RecognitionModel { Id = "qwen3-asr-1.7b", Label = "Qwen3-ASR 1.7B", Files = new[] { "models/models--Qwen--Qwen3-ASR-1.7B" },
                Description = "Qwen3-ASR 1.7B (Apache-2.0), multilingual, with its own punctuation and capitals. Text only, so the aligner times its words. About 4.4 GB of GPU memory." },
            new RecognitionModel { Id = "parakeet-tdt-0.6b-v2", Label = "Parakeet TDT 0.6B v2", Files = new[] { "models/models--nvidia--parakeet-tdt-0.6b-v2" },
                Description = "NVIDIA Parakeet TDT v2 (CC-BY-4.0), English, with its own punctuation and capitals. About 3.6 GB of GPU memory, or the CPU." },
        };
        public static RecognitionModel Find(string id) { return All.FirstOrDefault(m => m.Id == id); }
    }

    // What the panel offers: a recognizer and aligner benchmarked together, by the GPU
    // memory they need (tests/PipelineBench.py, PIPELINE_PLAN.md). Every tier is timed by
    // Qwen3-ForcedAligner Ultra; diarization is Nemotron 3.
    public sealed class RecognitionPipeline
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Model { get; private set; }
        public string Aligner { get; private set; }
        // GPU memory the tier is made for, shown beside it.
        public string Requirement { get; private set; }
        // Whether Options → Text Correction applies. On the gold set it improved High's sentence
        // ends (82% → 86%) and changed almost nothing in Medium's already punctuated text.
        public bool Correction { get; private set; }
        public string Description { get; private set; }
        public RecognitionModel Recognizer { get { return RecognitionModel.Find(Model); } }
        public override string ToString() { return Label + " · " + Recognizer.Label; }
        public static readonly RecognitionPipeline[] All = {
            new RecognitionPipeline { Id = "high", Label = "High", Model = "ark-asr-3b", Aligner = "qwen3-ultra", Requirement = "16 GB GPU", Correction = true,
                Description = "ARK-ASR 3B, timed by Qwen3-ForcedAligner Ultra. For 16 GB graphics cards: ARK-ASR needs about 9.5 GB of GPU memory, " +
                    "so with VEGAS open part of it runs from system memory, which is slower." },
            new RecognitionPipeline { Id = "medium", Label = "Medium", Model = "qwen3-asr-1.7b", Aligner = "qwen3-ultra", Requirement = "8–12 GB GPU",
                Description = "Qwen3-ASR 1.7B, timed by Qwen3-ForcedAligner Ultra. For 8–12 GB graphics cards: recognition and alignment need at most about 5.5 GB " +
                    "of GPU memory. Slightly less accurate than High on most recordings. It writes its own punctuation and capitals, so text correction is not used." },
            new RecognitionPipeline { Id = "low", Label = "Low", Model = "parakeet-tdt-0.6b-v2", Aligner = "qwen3-ultra", Requirement = "6 GB GPU",
                Description = "Parakeet TDT 0.6B v2, timed by Qwen3-ForcedAligner Ultra. For 6 GB graphics cards, or the CPU: recognition needs about 3.6 GB of GPU memory " +
                    "and alignment about 2 GB. English only, and less accurate than Medium. It writes its own punctuation and capitals, so text correction is not used." },
        };
        public static RecognitionPipeline Find(string id) { return All.FirstOrDefault(p => p.Id == id); }
    }

    // The aligner every tier uses. The recognizer is released before the aligner loads, so
    // they never share GPU memory.
    public sealed class RecognitionAligner
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Description { get; private set; }
        public string[] Files { get; private set; }
        public override string ToString() { return Label; }
        public static readonly RecognitionAligner[] All = {
            new RecognitionAligner { Id = "qwen3-ultra", Label = "Qwen3-ForcedAligner Ultra",
                Files = new[] { "models/models--17slever17--Qwen3-ForcedAligner-Ultra" },
                Description = "A community fine-tune of Qwen3-ForcedAligner-0.6B with a 10 ms boundary head. 11 languages, about 2 GB of GPU memory." },
        };
        public static RecognitionAligner Find(string id) { return All.FirstOrDefault(a => a.Id == id); }
    }

    public sealed class DiarizationModel
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Description { get; private set; }
        public string[] Files { get; private set; }
        public override string ToString() { return Label; }
        public static readonly DiarizationModel[] All = {
            new DiarizationModel { Id = "nemotron-3", Label = "Nemotron 3 Diarization",
                Description = "NVIDIA Sortformer, up to eight speakers. Uses the offline 30.4-second context and a persistent speaker cache across the recording. Count is automatic; Max speakers can merge excess labels using voice similarity.",
                Files = new[] { "models/models--nvidia--Nemotron-3-Diarization" } },
        };
        public static DiarizationModel Find(string id) { return All.FirstOrDefault(m => m.Id == id); }
    }

    // The language model for word and punctuation correction, Qwen3.5 9B Q6_K, which
    // corrected best on the gold set (PIPELINE_PLAN.md). The worker downloads it into the
    // recognition cache on first use and runs it on Subtitler's own llama.cpp engine
    // (correction_runtime.py, installed by Setup.cmd).
    public static class CorrectionModel
    {
        // The worker's Corrector setting: the Hugging Face repository and file.
        public const string Key = "lmstudio-community/Qwen3.5-9B-GGUF/Qwen3.5-9B-Q6_K.gguf";
        public const string Name = "Qwen3.5 9B";
        public const long Size = 7359259040;
        public static readonly string[] Files = { "models/models--lmstudio-community--Qwen3.5-9B-GGUF" };
        // A download in progress is not in the snapshot folder until it completes.
        public static bool Downloaded(string cacheDirectory)
        {
            string folder = Path.Combine(cacheDirectory, Files[0], "snapshots");
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, Key.Substring(Key.LastIndexOf('/') + 1), SearchOption.AllDirectories).Any();
        }
    }

    // Downloaded models in the recognition cache, for listing and deleting them.
    public static class ModelStore
    {
        public sealed class Entry
        {
            public string Label { get; set; }
            public string[] Files { get; set; }
            public long Size { get; set; }
        }
        public static List<Entry> Downloaded(string cacheDirectory)
        {
            return RecognitionModel.All.Select(m => new Entry { Label = m.Label, Files = m.Files })
                .Concat(RecognitionAligner.All.Select(a => new Entry { Label = a.Label + " (aligner)", Files = a.Files }))
                .Concat(DiarizationModel.All.Select(d => new Entry { Label = d.Label + " (diarizer)", Files = d.Files }))
                .Concat(new[] { new Entry { Label = CorrectionModel.Name + " (correction)", Files = CorrectionModel.Files } })
                .Concat(new[] { new Entry { Label = "WeSpeaker (voice matching)", Files = new[] { "models/models--pyannote--wespeaker-voxceleb-resnet34-LM" } } })
                .Select(e => { e.Size = e.Files.Sum(f => Size(Path.Combine(cacheDirectory, f))); return e; })
                .Where(e => e.Size > 0).ToList();
        }
        public static void Delete(string cacheDirectory, Entry entry)
        {
            foreach (string file in entry.Files) {
                string path = Path.Combine(cacheDirectory, file);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                // Hugging Face keeps a lock folder per repository beside the downloads.
                string name = Path.GetFileName(path);
                string locks = Path.Combine(Path.GetDirectoryName(path), ".locks", name);
                if (name.StartsWith("models--", StringComparison.Ordinal) && Directory.Exists(locks)) Directory.Delete(locks, true);
            }
        }
        // Cached recognition results and transcripts are named by a SHA-256 key; decoded audio
        // kept for regenerating (worker.decode) is in the audio folder.
        public static List<string> Results(string cacheDirectory)
        {
            if (!Directory.Exists(cacheDirectory)) return new List<string>();
            var results = Directory.GetFiles(cacheDirectory, "*.json").Where(f => Path.GetFileNameWithoutExtension(f).Length == 64 &&
                Path.GetFileNameWithoutExtension(f).All(Uri.IsHexDigit)).ToList();
            string audio = Path.Combine(cacheDirectory, "audio");
            if (Directory.Exists(audio)) results.AddRange(Directory.GetFiles(audio, "*.s16"));
            return results;
        }
        public static long Size(string path)
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        public static string Describe(long bytes)
        {
            return bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : Math.Max(1, bytes >> 20) + " MB";
        }
    }

    // Where Setup.cmd installed the runtime, and what one generation asks of the worker.
    public sealed class WorkerSettings
    {
        // Setup.cmd's install folder: the runtime, the worker and the panel's own settings.
        public static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subtitler");
        // The recognition environment, which also aligns and corrects.
        public string PythonPath { get; set; }
        public string DiarizationPythonPath { get; set; }
        // The bundled llama.cpp server that runs the correction model.
        public string LlamaServerPath { get; set; }
        public string WorkerPath { get; set; }
        public string FFmpegPath { get; set; }
        public string Model { get; set; }
        public string Device { get; set; } = "auto";
        public string Aligner { get; set; }
        public bool EnableDiarization { get; set; }
        public string DiarizationModel { get; set; } = "nemotron-3";
        public int SpeakerCount { get; set; }
        public int MaxSpeakers { get; set; }
        // Moves word edges next to pauses onto the speech onset in the audio (worker.snap_to_speech).
        public bool SnapToSpeech { get; set; } = true;
        // Text-only models also recognize quiet speech that plain detection skipped (worker.boost_quiet_speech).
        public bool BoostQuietSpeech { get; set; } = true;
        // CorrectionModel.Key when correcting; null when off.
        public string Corrector { get; set; }
        public string CorrectorLabel { get; set; }
        public string CacheDirectory { get; set; }
        // The copy Setup.cmd installs beside the extension, or the runtime's own when the
        // assembly runs from elsewhere, as in the tests.
        public static WorkerSettings Load()
        {
            string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var locations = new[] { Path.Combine(assembly, "Subtitler.Worker.settings.json"), Path.Combine(InstallDirectory, "runtime", "worker-settings.json") };
            string path = locations.FirstOrDefault(File.Exists);
            if (path == null) throw new InvalidOperationException("Subtitler is not set up. Close VEGAS and run Setup.cmd.");
            var settings = Json.Read<WorkerSettings>(File.ReadAllText(path));
            if (new[] { settings.PythonPath, settings.DiarizationPythonPath, settings.WorkerPath, settings.FFmpegPath }.Any(file => !File.Exists(file)))
                throw new InvalidOperationException("Part of Subtitler's runtime is missing. Close VEGAS and run Setup.cmd again.");
            return settings;
        }
        public bool HasCorrectionEngine { get { return !string.IsNullOrEmpty(LlamaServerPath) && File.Exists(LlamaServerPath); } }
    }
    public sealed class WorkerMessage
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string Stage { get; set; }
        public string ModelType { get; set; }
        public string ModelName { get; set; }
        public string Detail { get; set; }
        public int? Percent { get; set; }
        public int? Completed { get; set; }
        public int? Total { get; set; }
        public List<WorkerSource> Sources { get; set; }
        public string Device { get; set; }
        public string Model { get; set; }
        public string Aligner { get; set; }
        public List<SpeakerChoice> Speakers { get; set; }
        // Problems the worker worked around, such as skipped correction, for the status bar.
        public List<string> Notes { get; set; }
    }
    public sealed class WorkerSource
    {
        public string Id { get; set; }
        public List<Word> Words { get; set; }
        public double Delay { get; set; }
        public bool Measured { get; set; }
    }
    public sealed class Recognition
    {
        public List<Word> Words { get; } = new List<Word>();
        // Decoder offsets the worker measured this time, by source id (HostService.PrepareSync).
        public Dictionary<string, double> MeasuredDelays { get; } = new Dictionary<string, double>();
        public List<SpeakerChoice> Speakers { get; } = new List<SpeakerChoice>();
        public List<string> Notes { get; } = new List<string>();
    }
    public static class WorkerClient
    {
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        public static Task<Recognition> Recognize(List<SourceSnapshot> sources, WorkerSettings settings, bool force, Action<string> progress, CancellationToken token)
        {
            return RecognizeWithProgress(sources, settings, force, message => progress(message.Message), token);
        }
        // context: other tracks' subtitles, read by the correction model only.
        public static Task<Recognition> RecognizeWithProgress(List<SourceSnapshot> sources, WorkerSettings settings, bool force, Action<WorkerMessage> progress, CancellationToken token,
            List<ContextLine> context = null)
        {
            return Task.Run(() => {
                Directory.CreateDirectory(settings.CacheDirectory);
                string request = Path.Combine(settings.CacheDirectory, "request-" + Guid.NewGuid().ToString("N") + ".json");
                try {
                    Json.Save(request, new { Settings = settings, Sources = sources, Force = force, Context = context ?? new List<ContextLine>() });
                    WorkerMessage result = Run(settings.PythonPath, settings.WorkerPath, request, progress, token);
                    var notes = result.Notes ?? new List<string>();
                    if (settings.EnableDiarization && result.Sources.Any(s => s.Words.Count > 0)) {
                        Json.Save(request, new { Settings = settings, Sources = sources, Recognition = result.Sources });
                        result = Run(settings.DiarizationPythonPath, Path.Combine(Path.GetDirectoryName(settings.WorkerPath), "diarization_worker.py"), request, progress, token);
                    }
                    if (result.Sources.Count != sources.Count || result.Sources.Select(s => s.Id).Distinct().Count() != sources.Count)
                        throw new InvalidOperationException("Recognition returned an incomplete source list.");
                    var recognition = new Recognition();
                    recognition.Notes.AddRange(notes);
                    if (result.Speakers != null) recognition.Speakers.AddRange(result.Speakers);
                    foreach (var source in sources) {
                        var found = result.Sources.Single(s => s.Id == source.Id);
                        recognition.Words.AddRange(Timing.Map(source, found.Words));
                        if (found.Measured) recognition.MeasuredDelays[source.Id] = found.Delay;
                    }
                    return recognition;
                } finally { if (File.Exists(request)) File.Delete(request); }
            }, token);
        }
        private static WorkerMessage Run(string python, string worker, string request, Action<WorkerMessage> progress, CancellationToken token)
        {
                    using (var process = new Process()) {
                        process.StartInfo = new ProcessStartInfo(python, "-u " + Quote(worker) + " --request " + Quote(request)) {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(worker)
                        };
                        process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                        token.ThrowIfCancellationRequested();
                        process.Start();
                        using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } })) {
                            Task<string> diagnostics = process.StandardError.ReadToEndAsync();
                            WorkerMessage result = null;
                            string failure = null, line;
                            while ((line = process.StandardOutput.ReadLine()) != null) {
                                // Protocol lines are JSON objects; anything else is a library's console output.
                                if (!line.StartsWith("{", StringComparison.Ordinal)) continue;
                                var message = Json.Read<WorkerMessage>(line);
                                if (message.Type == "Progress") progress(message);
                                else if (message.Type == "Result") result = message;
                                else if (message.Type == "Error") failure = message.Message;
                            }
                            process.WaitForExit();
                            string errors = diagnostics.GetAwaiter().GetResult();
                            token.ThrowIfCancellationRequested();
                            if (failure != null) throw new InvalidOperationException(failure);
                            if (process.ExitCode != 0 || result == null) throw new InvalidOperationException("Recognition stopped before completing. " + errors.Trim());
                            return result;
                        }
                    }
        }
    }
}
