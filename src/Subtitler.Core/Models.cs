using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Subtitler.Core
{
    public static class Json
    {
        private static JavaScriptSerializer Serializer { get { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 128 }; } }
        public static string Write(object value) { return Serializer.Serialize(value); }
        public static T Read<T>(string value) { return Serializer.Deserialize<T>(value); }
        public static object Read(string value, Type type) { return Serializer.Deserialize(value, type); }
        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, Write(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class SourceSnapshot
    {
        public string Id { get; set; }
        public string TrackId { get; set; }
        public string Label { get; set; }
        public string TrackName { get; set; }
        public string FilePath { get; set; }
        public int StreamIndex { get; set; }
        public int FirstChannel { get; set; }
        public int ChannelCount { get; set; }
        public double Offset { get; set; }
        public double Start { get; set; }
        public double Duration { get; set; }
        public double Rate { get; set; }
        public string Signature { get; set; }
        // Seconds VEGAS's decode of this file lags FFmpeg's, when already known.
        public double? DecoderDelay { get; set; }
        // A VEGAS render of the file from SyncOffset (media seconds) for measuring it.
        public string SyncReference { get; set; }
        public double SyncOffset { get; set; }
    }

    public sealed class Word
    {
        public string Text { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public double? Probability { get; set; }
        public string SourceId { get; set; }
        public string TrackId { get; set; }
        public double SourceStart { get; set; }
        public double SourceEnd { get; set; }
        // Stable within a diarization model + source recording/stream, never global.
        public string SpeakerId { get; set; }
        public bool SpeakerReview { get; set; }
        public bool SpeakerOverlap { get; set; }
        public bool SpeakerEdited { get; set; }
        // The recognized text before the correction model changed it; "" for the
        // second part of a word it split. Null when unchanged.
        public string Original { get; set; }
    }

    public sealed class Caption
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TrackId { get; set; }
        public List<string> SourceIds { get; set; } = new List<string>();
        public string SpeakerId { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; }
        // The recognized wording when the correction model changed it, for reverting.
        public string UncorrectedText { get; set; }
        public List<Word> Words { get; set; } = new List<Word>();
    }

    // A subtitle on another track, shown to the correction model as context.
    public sealed class ContextLine
    {
        public string Label { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; }
    }

    public sealed class GroupingSettings
    {
        public int MaxWords { get; set; } = 7;
        public int MaxLines { get; set; } = 2;
        public double MaxDuration { get; set; } = 4;
        public double PauseSeconds { get; set; } = .4;
        public bool PreferPhrases { get; set; } = true;
        public bool CrossEvents { get; set; }
        public bool FillGaps { get; set; }
        // Caption text shows profane words as asterisks (Profanity.Censor); words keep them.
        public bool CensorProfanity { get; set; }
    }

    // Panel choices remembered across projects and VEGAS sessions
    // (%LOCALAPPDATA%\Subtitler\settings.json).
    public sealed class PanelSettings
    {
        public int Scope { get; set; }
        public bool ReplaceExisting { get; set; } = true;
        // A RecognitionPipeline tier (high, medium, low). Settings saved before tiers keep the
        // default; they chose single models, of which only ARK-ASR is in a tier.
        public string Pipeline { get; set; } = "high";
        public string Device { get; set; }
        public bool Retranscribe { get; set; }
        public bool SnapToSpeech { get; set; } = true;
        public bool EnableDiarization { get; set; }
        public bool SharedSpeakerTrack { get; set; }
        public int MaxSpeakers { get; set; }
        public bool EnableCorrection { get; set; }
        public GroupingSettings Grouping { get; set; } = new GroupingSettings();
        public bool CompressAnimation { get; set; }
        public bool UseNewTiming { get; set; }
        public string PresetId { get; set; }
        public int Tab { get; set; }
    }

    public sealed class ParameterValue
    {
        public string Json { get; set; }
        public int? ChoiceIndex { get; set; }
        public string ChoiceName { get; set; }
    }

    public sealed class ParameterKey
    {
        public double Seconds { get; set; }
        public string Interpolation { get; set; }
        public ParameterValue Value { get; set; }
        public Dictionary<string, string> Extras { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ParameterState
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Animated { get; set; }
        public ParameterValue Value { get; set; }
        public List<ParameterKey> Keys { get; set; } = new List<ParameterKey>();
    }

    public sealed class AppearancePreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public int Revision { get; set; } = 1;
        public string GeneratorId { get; set; }
        public double ReferenceDuration { get; set; }
        public int TimingVersion { get; set; }
        public double AuthoringFrameRate { get; set; }
        public List<ParameterState> Parameters { get; set; } = new List<ParameterState>();
    }

    public sealed class EventMetadata
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; }
        public string SourceTrackId { get; set; }
        public List<string> SourceIds { get; set; } = new List<string>();
        public string SourceLabel { get; set; }
        public string SpeakerId { get; set; }
        public string SpeakerName { get; set; }
        public string PresetId { get; set; }
        public int PresetRevision { get; set; }
        public List<Word> Words { get; set; } = new List<Word>();
        public string RecognizedText { get; set; }
        public string UncorrectedText { get; set; }
    }

    public sealed class ProjectState
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, string> TrackPresets { get; set; } = new Dictionary<string, string>();
        public GroupingSettings Grouping { get; set; } = new GroupingSettings();
        public Dictionary<string, Replacement> Candidates { get; set; } = new Dictionary<string, Replacement>();
        public Dictionary<string, SpeakerChoice> Speakers { get; set; } = new Dictionary<string, SpeakerChoice>();
    }

    public sealed class SpeakerChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PresetId { get; set; }
        public string Recording { get; set; }
    }

    public sealed class Replacement
    {
        public string Text { get; set; }
        public string UncorrectedText { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public List<Word> Words { get; set; } = new List<Word>();
    }

    public static class Timing
    {
        public static List<Word> Map(SourceSnapshot source, IEnumerable<Word> words)
        {
            if (source.Rate <= 0 || source.Duration <= 0) throw new ArgumentException("Unsupported source playback mapping.");
            var result = new List<Word>();
            foreach (Word word in words) {
                double start = source.Start + word.Start / source.Rate;
                double end = Math.Min(source.Start + source.Duration, source.Start + word.End / source.Rate);
                start = Math.Max(source.Start, start);
                // Whisper can assign a spoken word a zero-length alignment. Keep
                // its text and give it a minimal interval inside the source.
                if (end == start && start < source.Start + source.Duration) end = Math.Min(source.Start + source.Duration, start + .001);
                if (end <= start) continue;
                result.Add(new Word { Text = word.Text, Original = word.Original, Start = start, End = end, Probability = word.Probability,
                    SpeakerId = word.SpeakerId, SpeakerReview = word.SpeakerReview, SpeakerOverlap = word.SpeakerOverlap, SpeakerEdited = word.SpeakerEdited,
                    SourceId = source.Id, TrackId = source.TrackId, SourceStart = source.Start, SourceEnd = source.Start + source.Duration });
            }
            return result;
        }
    }
}
