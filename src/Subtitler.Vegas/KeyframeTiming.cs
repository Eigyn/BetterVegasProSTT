using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ScriptPortal.Vegas;

namespace Subtitler.VegasPlugin
{
    // VEGAS 22 (build 250) OFX keyframe timing, measured against rendered frames
    // (docs/vegas22-keyframe-timing.md):
    //  * SetValueAtTime, GetValueAtTime, the Keyframe.Time setter and Bezier control
    //    points all use real seconds from the start of the generated media.
    //  * Keys written through the API stay uncommitted until VEGAS renders that event
    //    (preview, a timeline thumbnail, a snapshot). Meanwhile the Keyframe.Time getter
    //    reports real seconds; once committed it reports real x 1000 / project rate.
    //  * Saving the project or opening the Media Generator window while keys are
    //    uncommitted stores them in the wrong unit: they come back x project rate / 1000,
    //    so a 1 s animation plays in 60 ms at 60 fps.
    //  * Only rendering the event itself commits it. Muted tracks are not rendered, and
    //    rendering the same media through another event does not commit this one.
    public static class KeyframeTiming
    {
        // Converts a committed Keyframe.Time reading into real seconds.
        public static double CommittedScale(Media media)
        {
            return media.Project.Video.FrameRate / media.GetVideoStreamByIndex(0).FrameRate;
        }

        // Returns the factor that converts this parameter's Keyframe.Time readings
        // into real seconds, or null when neither known time base reproduces its keys.
        // GetValueAtTime always agrees with the rendered output, so each candidate is
        // checked by sampling the curve at the times it implies.
        public static double? ReadScale(OFXParameter parameter, double committedScale)
        {
            var keys = Keys(parameter).Select(k => new { Time = Time(k), Value = Property(k, "Value") }).ToList();
            if (keys.All(k => k.Time == 0)) return 1;
            MethodInfo sample = parameter.GetType().GetMethod("GetValueAtTime", new[] { typeof(Timecode) });
            double[] range = Range(keys.Select(k => k.Value));
            Func<double, bool> fits = scale => keys.All(k => Same(k.Value, sample.Invoke(parameter, new object[] { Timecode.FromSeconds(k.Time * scale) }), range));
            // Prefer the committed reading: it is the normal state after VEGAS idles,
            // and both candidates only fit together when the curve is flat between them.
            if (fits(committedScale)) return committedScale;
            if (fits(1)) return 1;
            return null;
        }

        // Animated parameters whose keys are still uncommitted.
        public static List<OFXParameter> PendingParameters(Media media)
        {
            var pending = new List<OFXParameter>();
            if (media == null || !media.IsGenerated() || !media.Generator.IsOFX) return pending;
            double committed = CommittedScale(media);
            if (Math.Abs(committed - 1) < 1e-9) return pending;
            foreach (OFXParameter parameter in media.Generator.OFXEffect.Parameters) {
                if (!parameter.CanAnimate || !parameter.IsAnimated || parameter.GetType().GetProperty("Keyframes") == null) continue;
                double? scale = ReadScale(parameter, committed);
                if (scale.HasValue && scale.Value != committed) pending.Add(parameter);
            }
            return pending;
        }
        public static bool IsUncommitted(Media media) { return PendingParameters(media).Count > 0; }

        // Renders one frame inside each event whose keys are still uncommitted
        // (~60 ms per frame). Window painting is frozen and the cursor restored, so
        // the playhead never visibly moves; an event under the cursor needs no seek.
        public static int Commit(ScriptPortal.Vegas.Vegas vegas, IEnumerable<VideoEvent> events)
        {
            var pending = events.Where(e => TimelineEvents.Exists(e) && IsUncommitted(e.ActiveTake.Media)).ToList();
            if (pending.Count == 0) return 0;
            Timecode cursor = vegas.Transport.CursorPosition;
            // VEGAS does not render muted tracks, so their titles would stay pending.
            var muted = pending.Select(e => e.Track).Distinct().Where(t => t.Mute).ToList();
            string file = Path.Combine(Path.GetTempPath(), "Subtitler-commit-" + Process.GetCurrentProcess().Id + ".jpg");
            IntPtr window = vegas.MainWindow == null ? IntPtr.Zero : vegas.MainWindow.Handle;
            bool locked = window != IntPtr.Zero && LockWindowUpdate(window);
            try {
                if (muted.Count > 0) Silently(vegas, () => muted.ForEach(t => t.Mute = false));
                // A snapshot renders every event under that frame, so stacked titles share
                // one. It renders inside each event's first frame: at an exact start time
                // that is not frame-aligned, the frame drawn can begin before the event.
                double half = .5 / vegas.Project.Video.FrameRate;
                foreach (var group in pending.GroupBy(e => e.Start.Nanos)) {
                    double inside = Math.Min(half, group.Min(e => e.Length.ToMilliseconds() / 1000) / 2);
                    RenderStatus status = vegas.SaveSnapshot(file, ImageFileFormat.JPEG, group.First().Start + Timecode.FromSeconds(inside));
                    if (status != RenderStatus.Complete)
                        throw new InvalidOperationException("VEGAS could not render a frame to commit title animation (" + status + ").");
                }
            } finally {
                if (muted.Count > 0) Silently(vegas, () => muted.ForEach(t => t.Mute = true));
                if (vegas.Transport.CursorPosition.Nanos != cursor.Nanos) vegas.Transport.CursorPosition = cursor;
                if (locked) LockWindowUpdate(IntPtr.Zero);
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return pending.Count;
        }

        // Called from ProjectSaving. A save stores an uncommitted key time T as T x fps
        // and reloads it as milliseconds, so pending keys are set to T x 1000 / fps for
        // the save and restored from ProjectSaved: exact and nothing is rendered. VEGAS
        // reloads Bezier handles saved this way slightly differently, so events with
        // Manual or Split keys are committed by rendering instead.
        public static SaveCompensation CompensateForSave(ScriptPortal.Vegas.Vegas vegas, IEnumerable<VideoEvent> events)
        {
            var compensation = new SaveCompensation(vegas.Project);
            var curved = new List<VideoEvent>();
            Silently(vegas, () => {
                foreach (var ev in events.Where(TimelineEvents.Exists).Distinct()) {
                    var media = ev.ActiveTake.Media;
                    var pending = PendingParameters(media);
                    if (pending.Count == 0) continue;
                    if (pending.SelectMany(Keys).Cast<OFXKeyframe>().Any(k => k.Interpolation == OFXInterpolationType.Manual || k.Interpolation == OFXInterpolationType.Split)) { curved.Add(ev); continue; }
                    double factor = 1 / CommittedScale(media);
                    compensation.Titles++;
                    foreach (var parameter in pending)
                        // Latest first, so scaled keys never pass a later one.
                        foreach (OFXKeyframe key in Keys(parameter).Cast<OFXKeyframe>().OrderByDescending(k => k.Time.Nanos).ToList()) {
                            if (key.Time.Nanos == 0) continue;
                            compensation.Remember(key);
                            key.Time = Timecode.FromNanos((long)Math.Round(key.Time.Nanos * factor));
                        }
                }
            });
            compensation.Rendered = Commit(vegas, curved);
            return compensation;
        }

        // Project edits that VEGAS applies without recording an undo step.
        public static void Silently(ScriptPortal.Vegas.Vegas vegas, Action action)
        {
            using (new UndoBlockEx(vegas.Project, "Subtitler") { Keep = false }) action();
        }

        public static IEnumerable<object> Keys(OFXParameter parameter)
        {
            PropertyInfo keys = parameter.GetType().GetProperty("Keyframes");
            return keys == null ? Enumerable.Empty<object>() : ((IEnumerable)keys.GetValue(parameter, null)).Cast<object>();
        }
        public static double Time(object key) { return ((Timecode)Property(key, "Time")).ToMilliseconds() / 1000; }
        private static object Property(object owner, string name) { return owner.GetType().GetProperty(name).GetValue(owner, null); }

        // Tolerance scales with each component's range across the keys, so a steep
        // curve sampled at a 100 ns-rounded time still matches its key.
        private static bool Same(object expected, object actual, double[] range)
        {
            double[] a = Components(expected), b = Components(actual);
            if (a == null || b == null) return Equals(expected, actual) || (expected is OFXChoice && actual is OFXChoice && ((OFXChoice)expected).Index == ((OFXChoice)actual).Index);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (double.IsNaN(b[i]) || Math.Abs(a[i] - b[i]) > 1e-6 + 1e-3 * range[i]) return false;
            return true;
        }
        private static double[] Range(IEnumerable<object> values)
        {
            var vectors = values.Select(Components).Where(v => v != null).ToList();
            if (vectors.Count == 0) return new double[0];
            return Enumerable.Range(0, vectors[0].Length).Select(i => vectors.Max(v => v[i]) - vectors.Min(v => v[i])).ToArray();
        }
        private static double[] Components(object value)
        {
            if (value is double) return new[] { (double)value };
            if (value is int) return new[] { (double)(int)value };
            if (value is bool) return new[] { (bool)value ? 1.0 : 0 };
            if (value == null || value is string || value is OFXChoice) return null;
            var fields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(double) || f.FieldType == typeof(int)).ToList();
            return fields.Count == 0 ? null : fields.Select(f => Convert.ToDouble(f.GetValue(value))).ToArray();
        }
        [DllImport("user32.dll")] private static extern bool LockWindowUpdate(IntPtr window);
    }

    // Pending keys rewritten for one save; Restore puts the session's keys back.
    public sealed class SaveCompensation
    {
        private readonly Project project;
        private readonly List<Tuple<OFXKeyframe, Timecode>> original = new List<Tuple<OFXKeyframe, Timecode>>();
        public int Titles { get; set; }
        public int Rendered { get; set; }
        public int Keys { get { return original.Count; } }
        public DateTime Started { get; } = DateTime.UtcNow;
        public SaveCompensation(Project project) { this.project = project; }
        public void Remember(OFXKeyframe key) { original.Add(Tuple.Create(key, key.Time)); }
        public void Restore()
        {
            if (original.Count == 0) return;
            using (new UndoBlockEx(project, "Subtitler") { Keep = false })
                // Earliest first, mirroring the compensation order.
                foreach (var entry in original.OrderBy(e => e.Item2.Nanos)) entry.Item1.Time = entry.Item2;
            original.Clear();
        }
    }
}
