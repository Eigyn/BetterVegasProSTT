using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;
using Subtitler.Core;

namespace Subtitler.VegasPlugin
{
    // Titles & Text renders its Text parameter with its own small RTF reader
    // (ScriptPortal.MediaSoftware.TextGen.OFXInterop.Rtf), not with RichEdit. It knows
    // \par, \'xx in the font's code page, \uN with \ucN, the four quote words, \f, \b, \i,
    // \fs and alignment. Other symbol words are dropped from the picture (\emdash,
    // \endash, \bullet, \tab, \line), \~ and \_ render as "~" and "_", and \- or a
    // negative \uN stops the title rendering at all.
    public static class NativeText
    {
        // The text VEGAS renders. About 100 times faster than loading a RichTextBox,
        // which made rebuilding the subtitle list take ~2.5 ms per subtitle.
        public static string Plain(string rtf)
        {
            if (rtf == null || !rtf.StartsWith(@"{\rtf", StringComparison.Ordinal)) return rtf ?? "";
            try { return RtfText.Rendered(rtf); }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException ||
                error is KeyNotFoundException || error is IndexOutOfRangeException || error is InvalidOperationException || error is NotSupportedException) {
                // Titles & Text cannot read this RTF either; show what RichEdit sees.
                using (var box = new RichTextBox()) { box.Rtf = rtf; return box.Text; }
            }
        }

        public static string WithWords(string rtf, string words)
        {
            // The renderer draws each UTF-16 unit as its own glyph, so decomposed
            // accents would not combine with their letters.
            words = (words ?? "").Normalize(NormalizationForm.FormC);
            using (var box = new RichTextBox()) {
                if (rtf.StartsWith(@"{\rtf", StringComparison.Ordinal)) box.Rtf = rtf; else box.Text = rtf;
                box.SelectAll();
                Font font = box.SelectionFont;
                if (font == null && box.TextLength > 0) { box.Select(0, 1); font = box.SelectionFont; }
                var alignment = box.SelectionAlignment;
                Color color = box.SelectionColor;
                box.Text = words; box.SelectAll();
                if (font != null) box.SelectionFont = font;
                box.SelectionAlignment = alignment;
                if (!color.IsEmpty) box.SelectionColor = color;
                return Renderable(box.Rtf);
            }
        }

        // Rewrites RichEdit's symbol words as \uN, which Titles & Text understands.
        public static string Renderable(string rtf)
        {
            var output = new StringBuilder(rtf.Length + 16);
            int skip = 1;
            for (int i = 0; i < rtf.Length;) {
                char c = rtf[i];
                if (c != '\\' || i + 1 >= rtf.Length) { output.Append(c); i++; continue; }
                char next = rtf[i + 1];
                if (next == '\\' || next == '{' || next == '}') { output.Append(c).Append(next); i += 2; continue; }
                if (next == '\'') { output.Append(rtf, i, Math.Min(4, rtf.Length - i)); i += 4; continue; }
                if (!char.IsLetter(next)) {
                    // Control symbols: \~ no-break space, \_ no-break hyphen, \- optional hyphen.
                    output.Append(next == '~' ? Unicode(160, skip) : next == '_' ? Unicode(8209, skip) : next == '-' ? "" : rtf.Substring(i, 2));
                    i += 2; continue;
                }
                int start = i++;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                string word = rtf.Substring(start + 1, i - start - 1);
                int numberStart = i;
                if (i < rtf.Length && rtf[i] == '-') i++;
                while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                string number = rtf.Substring(numberStart, i - numberStart);
                if (i < rtf.Length && rtf[i] == ' ') i++;
                int value;
                bool numeric = int.TryParse(number, out value);
                if (word == "uc" && numeric) skip = value;
                string replacement;
                if (word == "u" && numeric && value < 0) replacement = @"\u" + (value + 65536) + (skip == 0 ? " " : "");
                else if (!Symbols.TryGetValue(word, out replacement)) { output.Append(rtf, start, i - start); continue; }
                else if (replacement.StartsWith("U+", StringComparison.Ordinal)) replacement = Unicode(int.Parse(replacement.Substring(2)), skip);
                output.Append(replacement);
            }
            return output.ToString();
        }
        private static string Unicode(int code, int skip) { return @"\u" + code + (skip == 0 ? " " : new string('?', skip)); }
        private static readonly Dictionary<string, string> Symbols = new Dictionary<string, string> {
            { "emdash", "U+8212" }, { "endash", "U+8211" }, { "bullet", "U+8226" }, { "emspace", "U+8195" }, { "enspace", "U+8194" },
            { "qmspace", "U+8197" }, { "zwj", "U+8205" }, { "zwnj", "U+8204" }, { "tab", "U+32" }, { "line", @"\par " }
        };

        // Mirrors TextGen's RtfLibrary.Parse and TextFormat.Parse, text only.
        private static class RtfText
        {
            private sealed class Group { public readonly List<object> Items = new List<object>(); }
            private sealed class Word { public string Name; public int? Parameter; }
            private sealed class Run
            {
                public readonly StringBuilder Chars = new StringBuilder();
                public bool Wide;
                public string Value(int codepage)
                {
                    if (Wide) return Chars.ToString();
                    var bytes = new byte[Chars.Length];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Chars[i];
                    return bytes.Length == 0 ? "" : Encoding.GetEncoding(codepage).GetString(bytes);
                }
            }

            public static string Rendered(string rtf)
            {
                var text = new StringBuilder();
                var state = new Reader();
                state.Walk(Parse(rtf), 1252, text);
                if (text.Length > 0 && text[text.Length - 1] == '\n') text.Length--;
                return text.ToString();
            }

            private static Group Parse(string rtf)
            {
                var top = new Group();
                var stack = new Stack<Group>();
                Group current = top;
                Run run = null;
                Action flush = () => { if (run != null) { current.Items.Add(run); run = null; } };
                Action<char> add = c => { if (run == null) run = new Run(); run.Chars.Append(c); if ((byte)c != c) run.Wide = true; };
                for (int i = 0; i < rtf.Length;) {
                    char c = rtf[i];
                    if (c == '\\') {
                        i++;
                        if (rtf[i] == '\'') { add((char)((Hex(rtf[i + 1]) << 4) | Hex(rtf[i + 2]))); i += 3; continue; }
                        if (rtf[i] == '{' || rtf[i] == '}' || rtf[i] == '\\') { add(rtf[i]); i++; continue; }
                        flush();
                        int start = i;
                        while (char.IsLetter(rtf[i]) || rtf[i] == '*') i++;
                        var word = new Word { Name = rtf.Substring(start, i - start) };
                        if (rtf[i] == '-' || char.IsDigit(rtf[i])) {
                            start = i;
                            while (rtf[i] == '-' || char.IsDigit(rtf[i])) i++;
                            word.Parameter = int.Parse(rtf.Substring(start, i - start));
                        }
                        if (rtf[i] == ' ') i++;
                        current.Items.Add(word);
                    } else if (c == '{') {
                        flush();
                        var group = new Group();
                        current.Items.Add(group); stack.Push(current); current = group; i++;
                    } else if (c == '}') {
                        flush(); current = stack.Pop(); i++;
                    } else {
                        if (!char.IsControl(c)) add(c);
                        i++;
                    }
                }
                flush();
                return top;
            }
            private static int Hex(char c) { return char.IsDigit(c) ? c - '0' : char.IsLetter(c) ? char.ToLower(c) - 'a' + 10 : 0; }

            private sealed class Reader
            {
                private Dictionary<int, int> fonts;
                private int unicodeSkip = 1, pendingSkip;
                public void Walk(Group group, int codepage, StringBuilder text)
                {
                    foreach (object item in group.Items) {
                        var word = item as Word;
                        var child = item as Group;
                        var run = item as Run;
                        if (word != null) {
                            switch (word.Name) {
                                case "par": text.Append('\n'); break;
                                case "f": if (fonts != null) codepage = fonts[word.Parameter ?? 0]; break;
                                case "lquote": text.Append('‘'); break;
                                case "rquote": text.Append('’'); break;
                                case "ldblquote": text.Append('“'); break;
                                case "rdblquote": text.Append('”'); break;
                                case "uc": unicodeSkip = word.Parameter ?? 0; break;
                                case "u": pendingSkip = unicodeSkip; text.Append(Convert.ToChar(word.Parameter ?? 0)); break;
                            }
                        } else if (child != null) {
                            var first = child.Items.Count > 0 ? child.Items[0] as Word : null;
                            if (first != null && first.Name == "fonttbl") {
                                fonts = Fonts(child);
                                if (fonts.ContainsKey(0)) codepage = fonts[0];
                            } else if (first == null || !Destinations.Contains(first.Name)) Walk(child, codepage, text);
                        } else if (run != null) {
                            text.Append(run.Value(codepage).Substring(pendingSkip));
                            pendingSkip = 0;
                        }
                    }
                }
                private static Dictionary<int, int> Fonts(Group table)
                {
                    var fonts = new Dictionary<int, int>();
                    foreach (var entry in table.Items.Skip(1).OfType<Group>()) {
                        int index = 0, charset = 0;
                        foreach (var word in entry.Items.OfType<Word>()) {
                            if (word.Name == "f") index = word.Parameter ?? 0;
                            else if (word.Name == "fcharset") charset = word.Parameter ?? 0;
                        }
                        fonts.Add(index, Codepage(charset));
                    }
                    return fonts;
                }
                private static readonly HashSet<string> Destinations = new HashSet<string> { "colortbl", "footnote", "footer", "header", "info", "pict", "*", "fonttbl", "stylesheet" };
                private static int Codepage(int charset)
                {
                    switch (charset) {
                        case 1: case 82: case 179: case 180: case 181: return 0;
                        case 77: return 10000; case 78: return 10001; case 79: return 10003; case 80: return 10008; case 81: return 10002;
                        case 83: return 10005; case 84: return 10004; case 85: return 10006; case 86: return 10081; case 87: return 10021;
                        case 88: return 10029; case 89: return 10007; case 128: return 932; case 129: return 949; case 130: return 1361;
                        case 134: return 936; case 136: return 950; case 161: return 1253; case 162: return 1254; case 163: return 1258;
                        case 177: return 1255; case 178: return 1256; case 186: return 1257; case 204: return 1251; case 222: return 874;
                        case 238: return 1250; case 254: return 437; case 255: return 850;
                        default: return 1252;
                    }
                }
            }
        }
    }

    public static class PresetCodec
    {
        // Bumped when capture switched to validated read scales; older presets may
        // carry keys shrunk by the VEGAS commit bug (KeyframeTiming).
        public const int CurrentTimingVersion = 2;

        public static AppearancePreset Capture(Media media, double duration)
        {
            media = (Media)media.Project.MediaPool[media.KeyString];
            double committed = KeyframeTiming.CommittedScale(media);
            Effect effect = media.Generator;
            if (!effect.IsOFX) throw new InvalidOperationException("This generator does not expose Titles & Text parameters.");
            var preset = new AppearancePreset { Name = "New preset", GeneratorId = effect.PlugIn.UniqueID, ReferenceDuration = duration,
                TimingVersion = CurrentTimingVersion, AuthoringFrameRate = media.Project.Video.FrameRate };
            foreach (OFXParameter parameter in effect.OFXEffect.Parameters) {
                PropertyInfo value = parameter.GetType().GetProperty("Value");
                if (value == null || !value.CanRead || !value.CanWrite || !parameter.Persisted) continue;
                var state = new ParameterState { Name = parameter.Name, Type = parameter.ParameterType.ToString(),
                    Animated = parameter.CanAnimate && parameter.IsAnimated, Value = Encode(value.GetValue(parameter, null)) };
                if (state.Animated) {
                    double? scale = KeyframeTiming.ReadScale(parameter, committed);
                    if (!scale.HasValue)
                        throw new InvalidOperationException("VEGAS reported keyframe times for " + parameter.Name + " that do not match its animation. Close the Video Media Generators window and save the preset again.");
                    foreach (object key in KeyframeTiming.Keys(parameter)) {
                        var keyType = key.GetType();
                        var frame = new ParameterKey {
                            Seconds = Math.Round(KeyframeTiming.Time(key) * scale.Value, 7),
                            Interpolation = keyType.GetProperty("Interpolation").GetValue(key, null).ToString(),
                            Value = Encode(keyType.GetProperty("Value").GetValue(key, null))
                        };
                        foreach (var extra in keyType.GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                            p.Name != "Time" && p.Name != "Value" && p.Name != "Interpolation")) {
                            if (extra.PropertyType == typeof(OFXControlPoint)) {
                                // The API throws for control points on any other interpolation.
                                if (frame.Interpolation != "Manual" && frame.Interpolation != "Split") continue;
                                OFXControlPoint point = (OFXControlPoint)extra.GetValue(key, null);
                                // Control points report real seconds whether or not keys are committed.
                                frame.Extras[extra.Name] = Json.Write(new CurvePoint { Seconds = point.Time.ToMilliseconds() / 1000, Value = point.Value });
                            } else frame.Extras[extra.Name] = Json.Write(extra.GetValue(key, null));
                        }
                        state.Keys.Add(frame);
                    }
                }
                preset.Parameters.Add(state);
            }
            if (!preset.Parameters.Any(p => p.Name == "Text")) throw new InvalidOperationException("The selected generator has no editable Titles & Text content.");
            return preset;
        }

        private static ParameterValue Encode(object value)
        {
            if (value is OFXChoice) {
                var choice = (OFXChoice)value;
                return new ParameterValue { ChoiceIndex = choice.Index, ChoiceName = choice.Name };
            }
            return new ParameterValue { Json = Json.Write(value) };
        }

        private static object Decode(ParameterValue data, OFXParameter parameter, Type type, string text)
        {
            if (data.ChoiceIndex.HasValue) {
                var choice = (OFXChoiceParameter)parameter;
                var match = choice.Choices.FirstOrDefault(c => c.Name == data.ChoiceName);
                if (match != null) return match;
                if (data.ChoiceIndex.Value < 0 || data.ChoiceIndex.Value >= choice.Choices.Length)
                    throw new InvalidOperationException("Preset choice is unavailable: " + parameter.Name);
                return choice.Choices[data.ChoiceIndex.Value];
            }
            object value = Json.Read(data.Json, type);
            if (parameter.Name == "Text" && text != null) value = NativeText.WithWords((string)value, text);
            return value;
        }

        public static void Apply(Media media, AppearancePreset preset, string words, double duration, bool compress)
        {
            Effect effect = media.Generator;
            if (effect.PlugIn.UniqueID != preset.GeneratorId) throw new InvalidOperationException("The preset uses a different media generator.");
            double frameRate = media.Project.Video.FrameRate;
            // Keys are real seconds from the media start. SetValueAtTime and control
            // points take real seconds; the caller must commit the event before the
            // project is saved or its generator window is opened (KeyframeTiming).
            double lastKey = preset.Parameters.SelectMany(p => p.Keys).Select(k => k.Seconds).DefaultIfEmpty(0).Max();
            double finalFrame = Math.Max(0, duration - 1 / frameRate);
            double compression = compress && lastKey > finalFrame ? finalFrame / lastKey : 1;
            foreach (ParameterState state in preset.Parameters) {
                OFXParameter parameter = effect.OFXEffect.FindParameterByName(state.Name);
                if (parameter == null) throw new InvalidOperationException("The generator is missing preset parameter " + state.Name);
                PropertyInfo property = parameter.GetType().GetProperty("Value");
                if (property == null || !property.CanWrite) throw new InvalidOperationException("Cannot restore preset parameter " + state.Name);
                if (parameter.CanAnimate) parameter.IsAnimated = false;
                property.SetValue(parameter, Decode(state.Value, parameter, property.PropertyType, words), null);
                if (!state.Animated || !parameter.CanAnimate) continue;
                parameter.IsAnimated = true;
                var keyProperty = parameter.GetType().GetProperty("Keyframes");
                object keys = keyProperty.GetValue(parameter, null);
                keys.GetType().GetMethod("Clear").Invoke(keys, null);
                MethodInfo setter = parameter.GetType().GetMethods().First(m => m.Name == "SetValueAtTime" && m.GetParameters().Length == 2);
                // Compression fits the animation's last key to the last visible
                // frame. A long reference with a short animation stays unchanged.
                var frames = state.Keys.Select(key => new { Key = key, Time = Timecode.FromSeconds(key.Seconds * compression) })
                    .GroupBy(frame => frame.Time.Nanos).Select(group => group.Last()).OrderBy(frame => frame.Time.Nanos).ToList();
                foreach (var frame in frames)
                    setter.Invoke(parameter, new[] { frame.Time, Decode(frame.Key.Value, parameter, property.PropertyType, words) });
                var actualKeys = ((IEnumerable)keys).Cast<object>().ToList();
                if (actualKeys.Count != frames.Count) throw new InvalidOperationException("VEGAS could not restore all animation keys for " + state.Name);
                // Inserting a neighbor can recalculate Bezier handles. Restore
                // interpolation and handles only after every key is in place.
                for (int i = 0; i < frames.Count; i++) {
                    object actual = actualKeys[i];
                    var key = frames[i].Key;
                    actual.GetType().GetProperty("Interpolation").SetValue(actual, Enum.Parse(typeof(OFXInterpolationType), key.Interpolation), null);
                }
                for (int i = 0; i < frames.Count; i++) {
                    object actual = actualKeys[i];
                    var key = frames[i].Key;
                    foreach (var extra in key.Extras) {
                        var target = actual.GetType().GetProperty(extra.Key);
                        if (target == null || !target.CanWrite) continue;
                        if (target.PropertyType == typeof(OFXControlPoint)) {
                            var saved = Json.Read<CurvePoint>(extra.Value);
                            object point = new OFXControlPoint(Timecode.FromSeconds(saved.Seconds * compression), saved.Value);
                            target.SetValue(actual, point, null);
                        } else target.SetValue(actual, Json.Read(extra.Value, target.PropertyType), null);
                    }
                }
            }
            effect.OFXEffect.AllParametersChanged();
        }
        private sealed class CurvePoint
        {
            public double Seconds { get; set; }
            public double Value { get; set; }
        }
    }
}
