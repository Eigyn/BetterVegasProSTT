using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Subtitler.Core
{
    public static class Grouping
    {
        public static List<Caption> Build(IEnumerable<Word> input, GroupingSettings settings, double frameRate)
        {
            if (settings.MaxWords < 1 || settings.MaxLines < 1 || settings.MaxDuration <= 0 || settings.PauseSeconds < 0 || frameRate <= 0)
                throw new ArgumentException("Grouping limits must be positive.");
            var output = new List<Caption>();
            foreach (var track in input.GroupBy(w => w.TrackId)) {
                var words = track.OrderBy(w => w.Start).ToList();
                var group = new List<Word>();
                foreach (Word word in words) {
                    if (word.End <= word.Start || string.IsNullOrWhiteSpace(word.Text)) continue;
                    if (group.Count > 0) {
                        Word previous = group[group.Count - 1];
                        bool boundary = word.SourceId != previous.SourceId;
                        bool adjacent = Math.Abs(previous.SourceEnd - word.SourceStart) <= 1.0 / frameRate;
                        bool phrase = settings.PreferPhrases && EndsSentence(previous.Text);
                        if ((boundary && (!settings.CrossEvents || !adjacent)) || group.Count >= settings.MaxWords ||
                            word.End - group[0].Start > settings.MaxDuration || word.Start - previous.End >= settings.PauseSeconds ||
                            phrase || word.SpeakerId != previous.SpeakerId || word.Start < previous.End - 1.0 / frameRate) {
                            output.Add(Create(group, settings, frameRate)); group.Clear();
                        }
                    }
                    group.Add(word);
                }
                if (group.Count > 0) output.Add(Create(group, settings, frameRate));
            }
            output = output.OrderBy(c => c.Start).ThenBy(c => c.TrackId).ToList();
            if (settings.FillGaps) {
                foreach (var track in output.GroupBy(c => c.TrackId)) {
                    var captions = track.OrderBy(c => c.Start).ToList();
                    for (int i = 0; i + 1 < captions.Count; i++)
                        if (captions[i + 1].Start >= captions[i].End) captions[i].End = captions[i + 1].Start;
                }
            }
            return output;
        }

        private static Caption Create(List<Word> words, GroupingSettings settings, double fps)
        {
            double start = Math.Round(words[0].Start * fps) / fps;
            double end = Math.Round(words[words.Count - 1].End * fps) / fps;
            if (end <= start) end = start + 1.0 / fps;
            Func<string, string> shown = text => settings.CensorProfanity ? Profanity.Censor(text) : text;
            return new Caption {
                TrackId = words[0].TrackId, SpeakerId = words[0].SpeakerId, SourceIds = words.Select(w => w.SourceId).Distinct().ToList(),
                Start = start, End = end, Words = words.ToList(), Text = shown(Wrap(words.Select(w => w.Text), settings)),
                UncorrectedText = words.Any(w => w.Original != null) ? shown(Wrap(words.Select(w => w.Original ?? w.Text), settings)) : null
            };
        }

        private static bool EndsSentence(string value)
        {
            value = value.TrimEnd().TrimEnd('"', '\'', ')', ']');
            return value.EndsWith(".") || value.EndsWith("!") || value.EndsWith("?");
        }

        private static string Wrap(IEnumerable<string> input, GroupingSettings settings)
        {
            var words = input.Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
            int lineLimit = words.Count <= 4 ? words.Count : Math.Max(1, (int)Math.Ceiling((double)words.Count / settings.MaxLines));
            var result = new StringBuilder();
            for (int i = 0; i < words.Count; i++) {
                if (i > 0) result.Append(i % lineLimit == 0 && i / lineLimit < settings.MaxLines ? "\n" : " ");
                result.Append(words[i]);
            }
            return result.ToString();
        }
    }

    public static class PresetRules
    {
        public static double ReferenceDuration(string presetId, IEnumerable<Tuple<string, double>> assignments, double fallback)
        {
            if (string.IsNullOrEmpty(presetId)) return fallback;
            var values = assignments.Where(a => a.Item1 == presetId && a.Item2 > 0).Select(a => a.Item2).ToList();
            return values.Count == 0 ? fallback : values.Min();
        }

        // Reference length for editing a preset in VEGAS: the shortest subtitle using it,
        // but always long enough to show and edit every keyframe (a one-word subtitle can
        // be a single frame, which would leave the keys past the end of the reference).
        public static double EditingDuration(AppearancePreset preset, double shortestSubtitle)
        {
            if (preset == null) return 5;
            double animation = preset.Parameters.SelectMany(p => p.Keys).Select(k => k.Seconds).DefaultIfEmpty(0).Max();
            double duration = shortestSubtitle > 0 ? shortestSubtitle : preset.ReferenceDuration > 0 ? preset.ReferenceDuration : 5;
            return Math.Max(Math.Max(duration, animation + .5), 1);
        }
    }
}
