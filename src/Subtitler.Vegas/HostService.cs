using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ScriptPortal.Vegas;
using Subtitler.Core;

namespace Subtitler.VegasPlugin
{
    public sealed class SubtitleItem
    {
        public VideoEvent Event { get; set; }
        public EventMetadata Metadata { get; set; }
        public string Text { get; set; }
        public Replacement Candidate { get; set; }
        // Captured on the host thread. List filters and their dropdown labels
        // must never read a track that a project switch has invalidated.
        public string TrackId { get; set; }
        public string TrackName { get; set; }
        public int TrackIndex { get; set; }
        public string RowId { get { return Metadata.Id + ":" + Event.EventID; } }
    }

    public sealed class AudioTrackItem
    {
        public Track Track { get; set; }
        public string Id { get; set; }
        // Captured when listed. Reading Track.Name later throws E_FAIL once VEGAS
        // has switched projects, and a ComboBox calls ToString() whenever it paints.
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    public static class TimelineEvents
    {
        // TrackEvent.IsValid() only checks cached IDs, so an event deleted in VEGAS or
        // removed by undo still passes it; reading its take fails with E_FAIL instead.
        public static bool Exists(TrackEvent ev)
        {
            if (ev == null || !ev.IsValid()) return false;
            try { return ev.ActiveTake != null; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
        }
    }

    public sealed class GenerationResult
    {
        public int Created { get; set; }
        public int Replaced { get; set; }
        public int Suggested { get; set; }
    }

    public sealed class HostService
    {
        public static readonly Guid TagKey = new Guid("4cd32151-9143-4b67-9f3e-70e128b223b9");
        private static readonly Guid StateKey = new Guid("19db2f29-1481-46df-90c2-f5b0b8d0d4b0");
        private readonly ScriptPortal.Vegas.Vegas vegas;
        private readonly int ownerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<Track, string> trackIds = new Dictionary<Track, string>();
        private readonly Dictionary<Track, string> subtitleTrackIds = new Dictionary<Track, string>();
        private readonly Dictionary<long, string> eventIds = new Dictionary<long, string>();
        private readonly Dictionary<string, AudioEvent> sources = new Dictionary<string, AudioEvent>();
        private SaveCompensation pendingSave;
        // Titles whose keys Subtitler wrote this session; saves also check tagged subtitles.
        private readonly List<VideoEvent> written = new List<VideoEvent>();
        private VideoTrack editorTrack;
        private VideoEvent editorEvent;
        private AppearancePreset editedPreset;
        private string editingId;
        public bool IsEditing { get { return editorEvent != null; } }
        public AppearancePreset EditedPreset { get { return editedPreset; } }
        public List<AppearancePreset> Presets { get; private set; }
        public string StorageDirectory { get; private set; }
        // VEGAS keeps one Project object for its whole session, so ReferenceEquals
        // cannot detect a project switch. The panel bumps this from VEGAS's events.
        public int Session { get; private set; }

        public HostService(ScriptPortal.Vegas.Vegas host, string storageDirectory = null)
        {
            vegas = host;
            StorageDirectory = storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subtitler");
            string path = Path.Combine(StorageDirectory, "presets.json");
            Presets = File.Exists(path) ? Json.Read<List<AppearancePreset>>(File.ReadAllText(path)) : new List<AppearancePreset>();
        }

        // Tracks and events from a closed project still report IsValid() but fail
        // with E_FAIL on use, and their numeric IDs repeat in the next project.
        public void ProjectChanged()
        {
            Session++;
            trackIds.Clear(); subtitleTrackIds.Clear(); eventIds.Clear(); sources.Clear(); written.Clear(); pendingSave = null;
            editorTrack = null; editorEvent = null; editedPreset = null; editingId = null;
        }

        public static EventMetadata Tag(CustomDataContainer data)
        {
            byte[] bytes = data.GetBytes(TagKey);
            return bytes == null ? null : Json.Read<EventMetadata>(Encoding.UTF8.GetString(bytes));
        }
        private static void SetTag(CustomDataContainer data, EventMetadata tag) { data.SetBytes(TagKey, Encoding.UTF8.GetBytes(Json.Write(tag))); }
        public ProjectState State()
        {
            VerifyAccess();
            byte[] bytes = vegas.Project.CustomData.GetBytes(StateKey);
            return bytes == null ? new ProjectState() : Json.Read<ProjectState>(Encoding.UTF8.GetString(bytes));
        }
        public void SaveState(ProjectState state) { vegas.Project.CustomData.SetBytes(StateKey, Encoding.UTF8.GetBytes(Json.Write(state))); }
        public void Mutate(string name, Action action)
        {
            VerifyAccess();
            using (var undo = new UndoBlock(vegas.Project, name)) {
                try { action(); } catch { undo.Cancel = true; throw; }
            }
            vegas.UpdateUI();
        }
        private string TrackId(Track track)
        {
            var tag = Tag(track.CustomData);
            if (tag != null) return tag.Id;
            string id;
            if (!trackIds.TryGetValue(track, out id)) trackIds[track] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        public List<AudioTrackItem> AudioTracks()
        {
            VerifyAccess();
            return vegas.Project.Tracks.Cast<Track>().Where(t => t.IsAudio()).Select(t => new AudioTrackItem { Track = t, Id = TrackId(t),
                Name = string.IsNullOrWhiteSpace(t.Name) ? "Audio track " + (t.Index + 1) : t.Name }).ToList();
        }
        private static string Words(VideoEvent ev)
        {
            var text = (OFXStringParameter)ev.ActiveTake.Media.Generator.OFXEffect.FindParameterByName("Text");
            return NativeText.Plain(text.GetValueAtTime(ev.ActiveTake.Offset));
        }
        // Plug-in names are localized; the OFX identifier is not.
        public const string TitlesAndTextId = "{Svfx:com.vegascreativesoftware:titlesandtext}";
        private static bool IsTitlesAndText(PlugInNode node)
        {
            return node != null && !node.IsContainer && (node.UniqueID == TitlesAndTextId || node.Name.IndexOf("Titles & Text", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        public static bool IsTitle(TrackEvent ev)
        {
            return ev is VideoEvent && ev.ActiveTake != null && ev.ActiveTake.IsGenerator &&
                ev.ActiveTake.Media.Generator.IsOFX && ev.ActiveTake.Media.Generator.OFXEffect.FindParameterByName("Text") is OFXStringParameter &&
                IsTitlesAndText(ev.ActiveTake.Media.Generator.PlugIn);
        }
        public List<SubtitleItem> Items()
        {
            VerifyAccess();
            var output = new List<SubtitleItem>();
            var candidates = State().Candidates;
            foreach (Track track in vegas.Project.Tracks)
                foreach (TrackEvent ev in track.Events) {
                    var tag = Tag(ev.CustomData);
                    if (tag == null || tag.Kind != "Subtitle" || !IsTitle(ev)) continue;
                    Replacement candidate; candidates.TryGetValue(tag.Id, out candidate);
                    output.Add(new SubtitleItem { Event = (VideoEvent)ev, Metadata = tag, Text = Words((VideoEvent)ev), Candidate = candidate,
                        TrackId = SubtitleTrackId(track), TrackName = string.IsNullOrWhiteSpace(track.Name) ? "Video track" : track.Name, TrackIndex = track.Index });
                }
            return output.OrderBy(item => item.Event.Start.Nanos).ToList();
        }
        private string SubtitleTrackId(Track track)
        {
            // Physical video tracks stay distinct even if copying a track also
            // copies its custom metadata. IDs last for this project session.
            string id;
            if (!subtitleTrackIds.TryGetValue(track, out id)) subtitleTrackIds[track] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        public void Locate(SubtitleItem item)
        {
            VerifyAccess();
            if (!TimelineEvents.Exists(item?.Event))
                throw new InvalidOperationException("This subtitle event is no longer on the timeline. Refresh the subtitle list.");
            // VEGAS requires an undo transaction for event selection in an application
            // extension. One that is not kept: selecting in VEGAS is not undoable either,
            // and a kept step made Ctrl+Z after Locate undo the selection, not the last edit.
            KeyframeTiming.Silently(vegas, () => item.Event.Selected = true);
            vegas.Transport.CursorPosition = item.Event.Start;
            vegas.Transport.ViewCursor(false);
            vegas.UpdateUI();
        }
        public List<SourceSnapshot> Snapshot(int scope, AudioTrackItem selectedTrack)
        {
            VerifyAccess();
            var selected = vegas.Project.Tracks.Cast<Track>().Where(t => t.IsAudio()).SelectMany(t => t.Events.Cast<TrackEvent>()).OfType<AudioEvent>().Where(e => e.Selected).ToList();
            if (scope == 1 && selected.Count != 1) throw new InvalidOperationException("Select exactly one audio event in VEGAS.");
            if (scope == 2) {
                if (selectedTrack == null) throw new InvalidOperationException("Choose an audio track.");
                selected = selectedTrack.Track.Events.Cast<TrackEvent>().OfType<AudioEvent>().ToList();
            }
            if (selected.Count == 0) throw new InvalidOperationException("Select one or more audio events, or choose an entire audio track.");
            var snapshots = new List<SourceSnapshot>();
            foreach (var ev in selected) {
                if (ev.ActiveTake == null || ev.ActiveTake.Media.IsSubclip() || ev.PlaybackRate <= 0)
                    throw new InvalidOperationException("This build requires forward-playing file-based audio events.");
                var take = ev.ActiveTake;
                if (!File.Exists(take.MediaPath)) throw new FileNotFoundException("Source audio is offline.", take.MediaPath);
                double duration = ev.Length.ToMilliseconds() / 1000;
                double offset = take.Offset.ToMilliseconds() / 1000;
                if (offset + duration * ev.PlaybackRate > take.MediaStream.Length.ToMilliseconds() / 1000 + .05)
                    throw new InvalidOperationException("A selected event loops beyond its source. Looped events are not supported in this build.");
                var tag = Tag(ev.CustomData);
                string id;
                if (tag != null) id = tag.Id;
                else if (!eventIds.TryGetValue(ev.EventID, out id)) eventIds[ev.EventID] = id = Guid.NewGuid().ToString("N");
                sources[id] = ev;
                var audioStreams = take.Media.Streams.Cast<MediaStream>().Where(s => s.MediaType == MediaType.Audio).ToList();
                // Take.StreamIndex is relative to the media type, whereas
                // MediaStream.Index also counts video streams. Compare actual
                // media streams before converting to FFmpeg's audio ordinal.
                int ordinal = audioStreams.FindIndex(s => s.Index == take.MediaStream.Index);
                if (ordinal < 0) throw new InvalidOperationException("The source audio stream could not be mapped.");
                string trackName = string.IsNullOrWhiteSpace(ev.Track.Name) ? "Audio " + (ev.Track.Index + 1) : ev.Track.Name;
                snapshots.Add(new SourceSnapshot {
                    Id = id, TrackId = TrackId(ev.Track), TrackName = trackName,
                    Label = trackName + " — " + (string.IsNullOrWhiteSpace(ev.Name) ? Path.GetFileName(take.MediaPath) : ev.Name) + " [event " + (ev.Index + 1) + "]",
                    FilePath = take.MediaPath, Offset = offset, Start = ev.Start.ToMilliseconds() / 1000, Duration = duration,
                    Rate = ev.PlaybackRate, StreamIndex = ordinal, FirstChannel = take.FirstAudioChannelIndex, ChannelCount = take.AudioChannelCount,
                    Signature = Signature(ev)
                });
            }
            return snapshots;
        }
        // VEGAS decodes some compressed audio with a different start than FFmpeg, which
        // the worker uses (measured: MP3 +51 ms, AAC in an MP4 video +43 ms, audio-only
        // M4A -44 ms; PCM WAV matches). For each new source file a few seconds are
        // rendered through VEGAS, and the worker measures the offset against its decode.
        public void PrepareSync(List<SourceSnapshot> snapshots, string folder)
        {
            VerifyAccess();
            var delays = SyncDelays();
            var rendered = new Dictionary<string, SourceSnapshot>();
            foreach (var snapshot in snapshots) {
                string key = SyncKey(snapshot);
                double delay;
                if (delays.TryGetValue(key, out delay)) { snapshot.DecoderDelay = delay; continue; }
                if (Pcm.Contains(Path.GetExtension(snapshot.FilePath).ToLowerInvariant())) { snapshot.DecoderDelay = 0; continue; }
                SourceSnapshot first;
                if (rendered.TryGetValue(key, out first)) { snapshot.SyncReference = first.SyncReference; snapshot.SyncOffset = first.SyncOffset; continue; }
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "sync-" + Guid.NewGuid().ToString("N") + ".wav");
                // Best effort: without a reference the worker assumes VEGAS matches FFmpeg.
                try { if (!RenderReference(snapshot, path)) continue; }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException || error is InvalidOperationException || error is ArgumentException || error is IOException) { continue; }
                snapshot.SyncReference = path; snapshot.SyncOffset = snapshot.Offset;
                rendered[key] = snapshot;
            }
        }
        public void RememberSync(SourceSnapshot snapshot, double delay)
        {
            SyncDelays()[SyncKey(snapshot)] = delay;
            Json.Save(Path.Combine(StorageDirectory, "decoder-sync.json"), syncDelays);
        }
        private static readonly HashSet<string> Pcm = new HashSet<string> { ".wav", ".wave", ".w64", ".bwf", ".rf64", ".aif", ".aiff", ".aifc" };
        private const double SyncSeconds = 20;
        private Dictionary<string, double> syncDelays;
        private Dictionary<string, double> SyncDelays()
        {
            if (syncDelays != null) return syncDelays;
            string path = Path.Combine(StorageDirectory, "decoder-sync.json");
            try { syncDelays = File.Exists(path) ? Json.Read<Dictionary<string, double>>(File.ReadAllText(path)) : null; }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException) { syncDelays = null; }
            return syncDelays = syncDelays ?? new Dictionary<string, double>();
        }
        // The offset belongs to the file and VEGAS's decoder for it.
        private string SyncKey(SourceSnapshot snapshot)
        {
            var file = new FileInfo(snapshot.FilePath);
            return string.Join("|", file.FullName.ToLowerInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, snapshot.StreamIndex, vegas.Version);
        }
        private bool RenderReference(SourceSnapshot snapshot, string path)
        {
            AudioEvent ev;
            if (!sources.TryGetValue(snapshot.Id, out ev) || !TimelineEvents.Exists(ev)) return false;
            RenderTemplate template = WaveTemplate();
            if (template == null) return false;
            double available = ev.ActiveTake.MediaStream.Length.ToMilliseconds() / 1000 - snapshot.Offset;
            double seconds = Math.Min(SyncSeconds, Math.Min(snapshot.Duration * snapshot.Rate, available));
            if (seconds < 1) return false;
            // Past the end of the project nothing else plays, so no track needs muting.
            Timecode at = vegas.Project.Length + Timecode.FromSeconds(5);
            AudioTrack track = null;
            try {
                KeyframeTiming.Silently(vegas, () => {
                    track = new AudioTrack(vegas.Project, vegas.Project.Tracks.Count, "Subtitler sync check");
                    vegas.Project.Tracks.Add(track);
                    var copy = track.AddAudioEvent(at, Timecode.FromSeconds(seconds));
                    copy.AddTake(ev.ActiveTake.MediaStream);
                    copy.ActiveTake.Offset = Timecode.FromSeconds(snapshot.Offset);
                });
                // The undo block must be closed first: the renderer does not see open changes.
                return vegas.Render(path, template, at, Timecode.FromSeconds(seconds)) == RenderStatus.Complete && File.Exists(path);
            } finally {
                if (track != null && track.IsValid()) KeyframeTiming.Silently(vegas, () => vegas.Project.Tracks.Remove(track));
            }
        }
        private RenderTemplate WaveTemplate()
        {
            var renderers = vegas.Renderers.Cast<Renderer>().Where(r => r.FileExtension.IndexOf(".wav", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var renderer = renderers.FirstOrDefault(r => r.FileTypeName.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) ?? renderers.FirstOrDefault();
            if (renderer == null) return null;
            var templates = renderer.Templates.Cast<RenderTemplate>().ToList();
            return templates.FirstOrDefault(t => t.AudioSampleRate == 48000 && t.Name.IndexOf("16 Bit", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? templates.FirstOrDefault(t => t.AudioSampleRate >= 16000) ?? templates.FirstOrDefault();
        }
        private static string Signature(AudioEvent ev)
        {
            return Json.Write(new { ev.EventID, Path = ev.ActiveTake.MediaPath, Start = ev.Start.Nanos, Length = ev.Length.Nanos, Offset = ev.ActiveTake.Offset.Nanos,
                Rate = ev.PlaybackRate, Stream = ev.ActiveTake.StreamIndex, First = ev.ActiveTake.FirstAudioChannelIndex, Channels = ev.ActiveTake.AudioChannelCount,
                Changed = File.GetLastWriteTimeUtc(ev.ActiveTake.MediaPath).Ticks });
        }
        public void ValidateSources(List<SourceSnapshot> snapshots, int session)
        {
            VerifyAccess();
            if (session != Session) throw new InvalidOperationException("The project changed during recognition. No subtitles were inserted.");
            foreach (var snapshot in snapshots) {
                AudioEvent ev;
                if (!sources.TryGetValue(snapshot.Id, out ev) || !ev.IsValid() || Signature(ev) != snapshot.Signature)
                    throw new InvalidOperationException("Source audio changed during recognition. Generate again; no subtitles were inserted.");
            }
        }
        private void VerifyAccess()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThreadId)
                throw new InvalidOperationException("VEGAS project access must run on the Subtitler UI thread.");
        }
        private void CommitSourceIds(List<SourceSnapshot> snapshots)
        {
            foreach (var source in snapshots) {
                AudioEvent ev = sources[source.Id];
                if (Tag(ev.Track.CustomData) == null) SetTag(ev.Track.CustomData, new EventMetadata { Id = source.TrackId, Kind = "AudioTrack" });
                if (Tag(ev.CustomData) == null) SetTag(ev.CustomData, new EventMetadata { Id = source.Id, Kind = "AudioEvent" });
            }
        }
        private static PlugInNode FindGenerator(PlugInNode node)
        {
            if (IsTitlesAndText(node)) return node;
            foreach (PlugInNode child in node) { var match = FindGenerator(child); if (match != null) return match; }
            return null;
        }
        public VideoEvent CreateTitle(VideoTrack track, double start, double duration, string words, AppearancePreset preset, bool compress)
        {
            var node = preset == null ? vegas.Generators.GetChildByUniqueID(TitlesAndTextId) ?? FindGenerator(vegas.Generators) : vegas.Generators.GetChildByUniqueID(preset.GeneratorId);
            if (node == null) throw new InvalidOperationException("VEGAS Titles & Text is unavailable.");
            var media = Media.CreateInstance(vegas.Project, node);
            media.Length = Timecode.FromSeconds(duration);
            var stream = media.GetVideoStreamByIndex(0);
            var ev = track.AddVideoEvent(Timecode.FromSeconds(start), Timecode.FromSeconds(duration));
            ev.AddTake(stream); ev.Name = words.Replace("\n", " "); ev.Loop = false;
            if (preset != null) { PresetCodec.Apply(media, preset, words, duration, compress); written.Add(ev); }
            else {
                var text = (OFXStringParameter)media.Generator.OFXEffect.FindParameterByName("Text");
                text.Value = NativeText.WithWords(text.Value, words);
                media.Generator.OFXEffect.AllParametersChanged();
            }
            return ev;
        }
        // Subtitles on other tracks around the selection: the correction model reads them so
        // a speaker on their own track is corrected within the whole conversation.
        public List<ContextLine> CorrectionContext(List<SourceSnapshot> selected)
        {
            var ids = new HashSet<string>(selected.Select(s => s.Id));
            double from = selected.Min(s => s.Start) - 60, to = selected.Max(s => s.Start + s.Duration) + 60;
            return Items().Where(i => !i.Metadata.SourceIds.Any(ids.Contains))
                .Select(i => new ContextLine { Label = i.Metadata.SpeakerName ?? Regex.Replace(i.TrackName ?? "", "^Subtitles — ", ""),
                    Start = i.Event.Start.ToMilliseconds() / 1000, End = i.Event.End.ToMilliseconds() / 1000, Text = i.Text })
                .Where(l => l.End > from && l.Start < to).OrderBy(l => l.Start).ToList();
        }
        // Subtitles a regeneration of these sources would replace, and how many carry corrected text.
        public Tuple<int, int> ExistingFor(List<SourceSnapshot> selected)
        {
            var ids = new HashSet<string>(selected.Select(s => s.Id));
            var old = Items().Where(i => i.Metadata.SourceIds.Any(ids.Contains)).ToList();
            return Tuple.Create(old.Count, old.Count(Corrected));
        }
        private static bool Corrected(SubtitleItem item)
        {
            return item.Metadata.RecognizedText != null && Collapse(item.Text) != Collapse(item.Metadata.RecognizedText);
        }
        internal static string Collapse(string text) { return string.Join(" ", (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); }

        // replace: delete the sources' current subtitles and build a new set (one undo step).
        // Otherwise current subtitles stay and differing new wording is staged for review.
        public List<SpeakerChoice> SpeakerChoices() { return State().Speakers?.Values.ToList() ?? new List<SpeakerChoice>(); }
        public void RestoreSpeakerEdits(List<Word> words)
        {
            // Manual assignments live in the project, never in the raw model cache.
            // Event-relative intervals survive timeline moves; changed wording/timing
            // or a different diarizer/session is reviewed anew.
            var edits = Items().SelectMany(i => i.Metadata.Words).Where(w => w.SpeakerEdited && w.SpeakerId != null)
                .GroupBy(w => w.SourceId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var word in words) {
                List<Word> candidates;
                if (!edits.TryGetValue(word.SourceId, out candidates) || word.SpeakerId == null) continue;
                string session = word.SpeakerId.Substring(0, word.SpeakerId.LastIndexOf(':') + 1);
                var old = candidates.FirstOrDefault(w => w.Text == word.Text && w.SpeakerId.StartsWith(session, StringComparison.Ordinal) &&
                    Math.Abs((w.Start - w.SourceStart) - (word.Start - word.SourceStart)) < .005 &&
                    Math.Abs((w.End - w.SourceStart) - (word.End - word.SourceStart)) < .005);
                if (old != null) { word.SpeakerId = old.SpeakerId; word.SpeakerEdited = true; word.SpeakerReview = false; }
            }
        }
        private static string OutputKey(string trackId, string speakerId, bool shared) { return trackId + (shared || string.IsNullOrEmpty(speakerId) ? "" : ":" + speakerId); }
        public GenerationResult CommitGeneration(List<SourceSnapshot> selected, List<Word> words, GroupingSettings grouping, bool compress, bool replace = false,
            List<SpeakerChoice> speakers = null, bool sharedSpeakers = false)
        {
            var existing = Items();
            var state = State();
            var result = new GenerationResult();
            var ids = new HashSet<string>(selected.Select(s => s.Id));
            var old = existing.Where(i => i.Metadata.SourceIds.Any(ids.Contains)).ToList();
            // A source that returned no words keeps its subtitles; an empty result is more
            // likely a recognition problem than speech the user wants removed.
            var heard = new HashSet<string>(words.Select(w => w.SourceId));
            var replaced = replace ? old.Where(i => i.Metadata.SourceIds.Any(heard.Contains)).ToList() : new List<SubtitleItem>();
            var reviewed = old.Except(replaced).ToList();
            var replacedTracks = replaced.Select(i => i.Event.Track).Distinct().ToList();
            var captionedSources = new HashSet<string>(reviewed.SelectMany(i => i.Metadata.SourceIds));
            var fresh = Grouping.Build(words.Where(w => !captionedSources.Contains(w.SourceId)), grouping, vegas.Project.Video.FrameRate);
            // New subtitles keep the look of the ones they replace.
            var keptPresets = replaced.Where(i => i.Metadata.SourceTrackId != null && Presets.Any(p => p.Id == i.Metadata.PresetId))
                .GroupBy(i => OutputKey(i.Metadata.SourceTrackId, i.Metadata.SpeakerId, sharedSpeakers))
                .ToDictionary(g => g.Key, g => g.GroupBy(i => i.Metadata.PresetId).OrderByDescending(p => p.Count()).First().Key);
            Mutate(replace ? "Regenerate subtitles" : "Generate subtitles", () => {
                CommitSourceIds(selected);
                if (state.Speakers == null) state.Speakers = new Dictionary<string, SpeakerChoice>();
                foreach (var speaker in speakers ?? new List<SpeakerChoice>()) state.Speakers[speaker.Id] = speaker;
                state.Grouping = grouping;
                var orphaned = replaced.Select(i => i.Event.ActiveTake.Media.KeyString).ToList();
                foreach (var item in replaced) {
                    state.Candidates.Remove(item.Metadata.Id);
                    item.Event.Track.Events.Remove(item.Event);
                }
                RemoveUnused(orphaned);
                result.Replaced = replaced.Count;
                foreach (var item in reviewed) {
                    state.Candidates.Remove(item.Metadata.Id);
                    double from = item.Event.Start.ToMilliseconds() / 1000, to = item.Event.End.ToMilliseconds() / 1000;
                    var replacementWords = words.Where(w => item.Metadata.SourceIds.Contains(w.SourceId) &&
                        (item.Metadata.SpeakerId == null || w.SpeakerId == item.Metadata.SpeakerId) &&
                        (w.Start + w.End) / 2 >= from && (w.Start + w.End) / 2 < to).OrderBy(w => w.Start).ToList();
                    Func<string, string> shown = text => grouping.CensorProfanity ? Profanity.Censor(text) : text;
                    string candidate = shown(string.Join(" ", replacementWords.Select(w => w.Text.Trim())));
                    if (candidate.Length == 0 || candidate == item.Text.Replace("\n", " ")) continue;
                    string uncorrected = replacementWords.Any(w => w.Original != null)
                        ? shown(string.Join(" ", replacementWords.Select(w => (w.Original ?? w.Text).Trim()).Where(t => t.Length > 0))) : null;
                    state.Candidates[item.Metadata.Id] = new Replacement { Text = candidate, UncorrectedText = uncorrected,
                        Start = replacementWords.Min(w => w.Start), End = replacementWords.Max(w => w.End), Words = replacementWords };
                    result.Suggested++;
                }
                result.Created = fresh.Count;
                foreach (var group in fresh.GroupBy(c => OutputKey(c.TrackId, c.SpeakerId, sharedSpeakers))) {
                    string sourceTrack = group.First().TrackId;
                    string speakerId = sharedSpeakers ? null : group.First().SpeakerId;
                    SpeakerChoice speaker = null;
                    if (speakerId != null) state.Speakers.TryGetValue(speakerId, out speaker);
                    var track = vegas.Project.Tracks.Cast<Track>().OfType<VideoTrack>().FirstOrDefault(t => {
                        var tag = Tag(t.CustomData); return tag != null && tag.Kind == "SubtitleTrack" && tag.SourceTrackId == sourceTrack && tag.SpeakerId == speakerId;
                    });
                    if (track == null) {
                        track = vegas.Project.AddVideoTrack();
                        var original = selected.First(s => s.TrackId == sourceTrack);
                        track.Name = "Subtitles — " + sources[original.Id].Track.Name + (speaker == null ? "" : " — " + speaker.Name);
                        SetTag(track.CustomData, new EventMetadata { Kind = "SubtitleTrack", SourceTrackId = sourceTrack, SpeakerId = speakerId, SpeakerName = speaker?.Name });
                    } else if (speaker != null) {
                        var tag = Tag(track.CustomData);
                        if (tag.SpeakerName != speaker.Name) {
                            string suffix = " — " + tag.SpeakerName;
                            if (!string.IsNullOrEmpty(tag.SpeakerName) && track.Name.EndsWith(suffix, StringComparison.Ordinal))
                                track.Name = track.Name.Substring(0, track.Name.Length - suffix.Length) + " — " + speaker.Name;
                            tag.SpeakerName = speaker.Name; SetTag(track.CustomData, tag);
                        }
                    }
                    string presetId;
                    if (!keptPresets.TryGetValue(group.Key, out presetId)) state.TrackPresets.TryGetValue(sourceTrack, out presetId);
                    AppearancePreset preset = Presets.FirstOrDefault(p => p.Id == presetId);
                    foreach (Caption caption in group) {
                        SpeakerChoice captionSpeaker = null;
                        if (caption.SpeakerId != null) state.Speakers.TryGetValue(caption.SpeakerId, out captionSpeaker);
                        var appearance = Presets.FirstOrDefault(p => p.Id == captionSpeaker?.PresetId) ?? preset;
                        var ev = CreateTitle(track, caption.Start, caption.End - caption.Start, caption.Text, appearance, compress);
                        SetTag(ev.CustomData, new EventMetadata { Id = caption.Id, Kind = "Subtitle", SourceTrackId = sourceTrack, SourceIds = caption.SourceIds,
                            SpeakerId = caption.SpeakerId, SpeakerName = captionSpeaker?.Name,
                            SourceLabel = string.Join(" + ", caption.SourceIds.Select(id => selected.First(s => s.Id == id).Label)), PresetId = appearance?.Id,
                            PresetRevision = appearance == null ? 0 : appearance.Revision, RecognizedText = caption.Text, UncorrectedText = caption.UncorrectedText,
                            Words = caption.Words });
                    }
                }
                // Replacing a speaker set can retire an old label/track. Remove only
                // emptied managed output tracks; tracks containing events stay intact.
                foreach (var track in replacedTracks) {
                    var tag = Tag(track.CustomData);
                    if (track.Events.Count == 0 && tag != null && tag.Kind == "SubtitleTrack") {
                        vegas.Project.Tracks.Remove(track); subtitleTrackIds.Remove(track);
                    }
                }
                SaveState(state);
            });
            return result;
        }
        // Restores the recognized wording a correction model changed. Like any text edit, it
        // clears the per-word data, so the corrected-word highlights go too.
        public void RevertCorrections(SubtitleItem item)
        {
            if (item.Metadata.UncorrectedText != null) SetText(item, item.Metadata.UncorrectedText, "Revert subtitle corrections");
        }
        public void SetText(SubtitleItem item, string words, string undoName = "Correct subtitle text")
        {
            if (item.Text == words) return;
            Mutate(undoName, () => {
                // Make generated media unique before changing text on an adopted/copied event.
                EnsureUnique(item.Event);
                var parameter = (OFXStringParameter)item.Event.ActiveTake.Media.Generator.OFXEffect.FindParameterByName("Text");
                parameter.Value = NativeText.WithWords(parameter.Value, words);
                foreach (var key in parameter.Keyframes) key.Value = NativeText.WithWords(key.Value, words);
                item.Event.Name = words.Replace("\n", " ");
                item.Metadata.Words.Clear();
                SetTag(item.Event.CustomData, item.Metadata);
                parameter.ParameterChanged();
            });
        }
        private void EnsureUnique(VideoEvent ev)
        {
            if (ev.ActiveTake.Media.UseCount <= 1) return;
            double duration = ev.Length.ToMilliseconds() / 1000;
            var snapshot = PresetCodec.Capture(ev.ActiveTake.Media, duration);
            var media = Media.CreateInstance(vegas.Project, ev.ActiveTake.Media.Generator.PlugIn);
            media.Length = Timecode.FromSeconds(Math.Max(duration, ev.ActiveTake.Offset.ToMilliseconds() / 1000 + duration));
            PresetCodec.Apply(media, snapshot, null, duration, false);
            var previous = ev.ActiveTake;
            var take = ev.AddTake(media.GetVideoStreamByIndex(0), true, previous.Name);
            take.Offset = previous.Offset;
            ev.Takes.Remove(previous);
        }
        public void AdoptSelected()
        {
            var selection = vegas.Project.Tracks.Cast<Track>().SelectMany(t => t.Events.Cast<TrackEvent>().Where(e => e.Selected || t.Selected)).Where(IsTitle).ToList();
            if (selection.Count == 0) throw new InvalidOperationException("Select the existing Titles & Text events or their track in VEGAS.");
            Mutate("Add existing subtitles", () => {
                foreach (var ev in selection) {
                    var tag = Tag(ev.CustomData);
                    if (tag != null && tag.Kind == "Editor") continue;
                    if (tag != null && tag.Kind == "Subtitle") continue;
                    SetTag(ev.CustomData, new EventMetadata { Kind = "Subtitle", SourceLabel = "Existing — " + ev.Track.Name + " [event " + (ev.Index + 1) + "]" });
                }
            });
        }
        // preset null clears the track's default.
        public void Assign(AudioTrackItem track, AppearancePreset preset)
        {
            if (track == null) throw new InvalidOperationException("Choose an audio track.");
            Mutate(preset == null ? "Clear default subtitle preset" : "Assign subtitle preset", () => {
                if (Tag(track.Track.CustomData) == null) SetTag(track.Track.CustomData, new EventMetadata { Id = track.Id, Kind = "AudioTrack" });
                var state = State();
                if (preset == null) state.TrackPresets.Remove(track.Id); else state.TrackPresets[track.Id] = preset.Id;
                SaveState(state);
            });
        }
        public AppearancePreset AssignedPreset(AudioTrackItem track)
        {
            string id;
            return State().TrackPresets.TryGetValue(track.Id, out id) ? Presets.FirstOrDefault(p => p.Id == id) : null;
        }
        public AppearancePreset FindPreset(string id) { return id == null ? null : Presets.FirstOrDefault(p => p.Id == id); }

        private void SavePresets(List<AppearancePreset> presets)
        {
            Json.Save(Path.Combine(StorageDirectory, "presets.json"), presets);
            Presets = presets;
        }
        // Existing subtitles keep their appearance: each title owns its generator media.
        public void DeletePreset(AppearancePreset preset)
        {
            if (IsEditing && editingId == preset.Id) throw new InvalidOperationException("Save or cancel the edit of this preset first.");
            SavePresets(Presets.Where(p => p.Id != preset.Id).ToList());
            var state = State();
            var assigned = state.TrackPresets.Where(p => p.Value == preset.Id).Select(p => p.Key).ToList();
            if (assigned.Count > 0) Mutate("Clear deleted subtitle preset", () => { foreach (var key in assigned) state.TrackPresets.Remove(key); SaveState(state); });
        }
        public void RenamePreset(AppearancePreset preset, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a preset name.");
            preset.Name = name.Trim();
            SavePresets(Presets.ToList());
        }
        // Earlier builds could shrink a preset's keys by fps / 1000 (KeyframeTiming): a
        // bounce over frames 0-8 at 60 fps was saved as 0-8 ms. Such an animation ends
        // within one frame of its authoring rate, which a real animation cannot.
        public static bool HasCollapsedTiming(AppearancePreset preset, double projectFrameRate)
        {
            double rate = preset.AuthoringFrameRate > 0 ? preset.AuthoringFrameRate : projectFrameRate;
            double last = preset.Parameters.SelectMany(p => p.Keys).Select(k => k.Seconds).DefaultIfEmpty(0).Max();
            return last > 0 && last < 1 / rate;
        }
        public void RepairPresetTiming(AppearancePreset preset)
        {
            double rate = preset.AuthoringFrameRate > 0 ? preset.AuthoringFrameRate : vegas.Project.Video.FrameRate;
            double factor = 1000 / rate;
            foreach (var key in preset.Parameters.SelectMany(p => p.Keys)) {
                key.Seconds = Math.Round(key.Seconds * factor, 7);
                foreach (var handle in key.Extras.Keys.Where(k => k.EndsWith("ControlPoint", StringComparison.Ordinal)).ToList()) {
                    var point = Json.Read<Dictionary<string, object>>(key.Extras[handle]);
                    point["Seconds"] = Math.Round(Convert.ToDouble(point["Seconds"]) * factor, 7);
                    key.Extras[handle] = Json.Write(point);
                }
            }
            preset.Revision++;
            preset.TimingVersion = PresetCodec.CurrentTimingVersion;
            SavePresets(Presets.ToList());
        }
        public AppearancePreset DuplicatePreset(AppearancePreset preset)
        {
            var copy = Json.Read<AppearancePreset>(Json.Write(preset));
            copy.Id = Guid.NewGuid().ToString("N"); copy.Name = preset.Name + " copy"; copy.Revision = 1;
            SavePresets(Presets.Concat(new[] { copy }).ToList());
            return copy;
        }
        public void OpenPreset(AppearancePreset preset)
        {
            if (IsEditing) throw new InvalidOperationException("Save or cancel the current preset edit first.");
            ShortestSubtitle = preset == null ? 0 : Items().Where(i => i.Metadata.PresetId == preset.Id).Select(i => i.Event.Length.ToMilliseconds() / 1000).DefaultIfEmpty(0).Min();
            double duration = PresetRules.EditingDuration(preset, ShortestSubtitle);
            editedPreset = preset;
            editingId = preset == null ? Guid.NewGuid().ToString("N") : preset.Id;
            double rate = vegas.Project.Video.FrameRate;
            double at = Math.Floor(vegas.Transport.CursorPosition.ToMilliseconds() / 1000 * rate + 1e-6) / rate;
            Mutate("Open subtitle preset reference", () => {
                // On top at the cursor, unmuted: visible in the video preview while editing.
                editorTrack = new VideoTrack(vegas.Project, 0, "Subtitler preset reference");
                vegas.Project.Tracks.Add(editorTrack);
                SetTag(editorTrack.CustomData, new EventMetadata { Kind = "Editor" });
                editorEvent = CreateTitle(editorTrack, at, duration, "Sample subtitle", preset, false);
                SetTag(editorEvent.CustomData, new EventMetadata { Kind = "Editor" });
            });
            try {
                // Opening the window on uncommitted keys shrinks the animation by
                // project rate / 1000 (60 fps: 83 ms becomes 5 ms) on every edit cycle.
                // The reference starts at the cursor, so this render needs no seek.
                KeyframeTiming.Commit(vegas, new[] { editorEvent });
                editorEvent.OpenMediaGeneratorUI();
            }
            catch { CancelEditor(); throw; }
        }
        public double EditingDuration { get { return editorEvent == null ? 0 : editorEvent.Length.ToMilliseconds() / 1000; } }
        // Length of the shortest subtitle using the edited preset (0 when none).
        public double ShortestSubtitle { get; private set; }
        public bool HasDraft { get { return File.Exists(Path.Combine(StorageDirectory, "preset-draft.json")); } }
        public AppearancePreset ReadDraft() { return Json.Read<AppearancePreset>(File.ReadAllText(Path.Combine(StorageDirectory, "preset-draft.json"))); }
        public void StoreEditorDraft(string name)
        {
            if (!IsEditing) return;
            // Best effort while VEGAS saves or closes; Capture also reads uncommitted keys correctly.
            try { KeyframeTiming.Commit(vegas, new[] { editorEvent }); } catch (Exception) { }
            var draft = PresetCodec.Capture(editorEvent.ActiveTake.Media, EditingDuration);
            draft.Id = editingId;
            draft.Name = string.IsNullOrWhiteSpace(name) ? "Recovered preset" : name.Trim();
            if (editedPreset != null) { draft.Id = editedPreset.Id; draft.Revision = editedPreset.Revision; }
            Json.Save(Path.Combine(StorageDirectory, "preset-draft.json"), draft);
        }
        public AppearancePreset SaveEditor(string name)
        {
            if (!IsEditing) throw new InvalidOperationException("No preset reference is being edited in this project.");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a preset name.");
            KeyframeTiming.Commit(vegas, new[] { editorEvent });
            var preset = PresetCodec.Capture(editorEvent.ActiveTake.Media, EditingDuration);
            preset.Id = editingId;
            preset.Name = name.Trim();
            if (editedPreset != null) { preset.Id = editedPreset.Id; preset.Revision = editedPreset.Revision + 1; }
            SavePresets(Presets.Where(p => p.Id != preset.Id).Concat(new[] { preset }).ToList());
            if (HasDraft && ReadDraft().Id == preset.Id) File.Delete(Path.Combine(StorageDirectory, "preset-draft.json"));
            CancelEditor();
            return preset;
        }
        public void CancelEditor()
        {
            // ProjectChanged clears these, so a reference from a closed project is never touched.
            if (editorTrack != null && editorTrack.IsValid()) {
                string media = TimelineEvents.Exists(editorEvent) ? editorEvent.ActiveTake.Media.KeyString : null;
                Mutate("Close subtitle preset reference", () => {
                    vegas.Project.Tracks.Remove(editorTrack);
                    if (media != null) RemoveUnused(new[] { media });
                });
            }
            editorTrack = null; editorEvent = null; editedPreset = null; editingId = null;
        }
        // VEGAS keeps generated media in the project media list after their events are
        // deleted, so every regeneration or preset edit left titles behind. Removing them
        // in the same undo step is safe: undo restores the events and their media.
        private void RemoveUnused(IEnumerable<string> keys)
        {
            foreach (string key in keys.Where(k => k != null).Distinct()) {
                var media = vegas.Project.MediaPool[key] as Media;
                if (media != null && media.IsGenerated() && media.UseCount == 0) vegas.Project.MediaPool.Remove(key);
            }
        }
        public void Apply(AppearancePreset preset, IEnumerable<SubtitleItem> targets, bool compress)
        {
            var list = targets.ToList();
            if (list.Count == 0) throw new InvalidOperationException("Select at least one subtitle.");
            Mutate("Apply subtitle appearance", () => {
                foreach (var item in list) {
                    EnsureUnique(item.Event);
                    item.Event.ActiveTake.Offset = Timecode.FromSeconds(0);
                    item.Event.ActiveTake.Media.Length = item.Event.Length;
                    PresetCodec.Apply(item.Event.ActiveTake.Media, preset, item.Text, item.Event.Length.ToMilliseconds() / 1000, compress);
                    item.Metadata.PresetId = preset.Id; item.Metadata.PresetRevision = preset.Revision;
                    SetTag(item.Event.CustomData, item.Metadata);
                }
            });
        }

        // From VEGAS's ProjectSaving event: keys VEGAS has not committed yet would be
        // saved in the wrong unit, so they are compensated for this save (KeyframeTiming).
        // Checking every managed title also covers undo/redo of earlier writes.
        public SaveCompensation PrepareSave()
        {
            VerifyAccess();
            FinishSave();
            written.RemoveAll(e => !TimelineEvents.Exists(e));
            var events = Items().Select(i => i.Event).Concat(written).ToList();
            if (editorEvent != null) events.Add(editorEvent);
            pendingSave = KeyframeTiming.CompensateForSave(vegas, events);
            return pendingSave;
        }
        // From ProjectSaved, or later if that event never arrives (a failed save).
        public void FinishSave()
        {
            var save = pendingSave;
            pendingSave = null;
            if (save != null) save.Restore();
        }
        public DateTime? SaveStarted { get { return pendingSave == null ? (DateTime?)null : pendingSave.Started; } }
        public void Accept(IEnumerable<SubtitleItem> targets, bool retime, bool keep)
        {
            var list = targets.Where(i => i.Candidate != null).ToList();
            var state = State();
            Mutate(keep ? "Keep current subtitles" : "Accept subtitle replacements", () => {
                foreach (var item in list) {
                    if (!keep) {
                        EnsureUnique(item.Event);
                        var text = (OFXStringParameter)item.Event.ActiveTake.Media.Generator.OFXEffect.FindParameterByName("Text");
                        text.Value = NativeText.WithWords(text.Value, item.Candidate.Text);
                        foreach (var key in text.Keyframes) key.Value = NativeText.WithWords(key.Value, item.Candidate.Text);
                        item.Event.Name = item.Candidate.Text.Replace("\n", " ");
                        if (retime) {
                            double frameRate = vegas.Project.Video.FrameRate;
                            double start = Math.Round(item.Candidate.Start * frameRate) / frameRate;
                            double end = Math.Max(start + 1 / frameRate, Math.Round(item.Candidate.End * frameRate) / frameRate);
                            item.Event.AdjustStartLength(Timecode.FromSeconds(start), Timecode.FromSeconds(end - start), false);
                            item.Event.ActiveTake.Media.Length = Timecode.FromSeconds(item.Event.ActiveTake.Offset.ToMilliseconds() / 1000 + end - start);
                        }
                        item.Metadata.Words = item.Candidate.Words;
                        item.Metadata.RecognizedText = item.Candidate.Text;
                        item.Metadata.UncorrectedText = item.Candidate.UncorrectedText;
                        SetTag(item.Event.CustomData, item.Metadata);
                        text.ParameterChanged();
                    }
                    state.Candidates.Remove(item.Metadata.Id);
                }
                SaveState(state);
            });
        }
    }
}
