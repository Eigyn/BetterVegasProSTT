using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Subtitler.VegasPlugin
{
    // A RichEdit control swallows every wheel message over it, even with nothing
    // to scroll, which stalled the list whenever the pointer crossed a caption.
    // Wheel input goes to the list unless this caption has focus and can scroll.
    public sealed class CaptionEditor : RichTextBox
    {
        private const int WM_MOUSEWHEEL = 0x020A, SB_VERT = 1, SIF_ALL = 0x17;
        public Control WheelTarget { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && WheelTarget != null && WheelTarget.IsHandleCreated && !CanScroll((short)((m.WParam.ToInt64() >> 16) & 0xFFFF))) {
                SendMessage(WheelTarget.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                return;
            }
            base.WndProc(ref m);
        }
        private bool CanScroll(int delta)
        {
            if (!Focused) return false;
            var info = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), Mask = SIF_ALL };
            if (!GetScrollInfo(Handle, SB_VERT, ref info) || info.Page == 0 || info.Max - info.Min + 1 <= info.Page) return false;
            return delta > 0 ? info.Position > info.Min : info.Position + info.Page <= info.Max;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollInfo { public int Size; public int Mask; public int Min; public int Max; public int Page; public int Position; public int TrackPosition; }
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // Column geometry shared by the list header and every row: check, Start, End,
    // Source, Text, Preset and the row's tool buttons. Narrower lists move the text
    // under the times and leave Preset to the row's tooltip.
    public sealed class SubtitleColumns
    {
        public const int Check = 22, Time = 84, Source = 120, Preset = 96, Actions = 44, WideFrom = 640;
        public bool Wide;
        public Rectangle CheckCell, StartCell, EndCell, SourceCell, TextCell, PresetCell, ActionCell;
        public static SubtitleColumns For(int width)
        {
            int line = PanelTheme.Row, after = Check + Time * 2;
            var columns = new SubtitleColumns { Wide = width >= WideFrom,
                CheckCell = new Rectangle(0, 0, Check, line), StartCell = new Rectangle(Check, 0, Time, line), EndCell = new Rectangle(Check + Time, 0, Time, line),
                ActionCell = new Rectangle(width - Actions, 0, Actions, line) };
            if (columns.Wide) {
                columns.SourceCell = new Rectangle(after, 0, Source, line);
                columns.PresetCell = new Rectangle(width - Actions - Preset, 0, Preset, line);
                columns.TextCell = new Rectangle(after + Source, 0, columns.PresetCell.X - after - Source, line);
            } else {
                columns.SourceCell = new Rectangle(after, 0, Math.Max(0, width - Actions - after), line);
                columns.TextCell = new Rectangle(Check, line, Math.Max(60, width - Check - 4), line);
            }
            return columns;
        }
    }

    // One subtitle, laid out like a row of a VEGAS details view and divided from the
    // next by a 1px seam. Times, source, preset and suggestion are drawn; the check
    // box, caption editor and tool buttons are native children. The caption sits on
    // the row until it is edited, when it becomes a framed well.
    public sealed class SubtitleRowView : Control
    {
        internal const int AcceptWidth = 70, KeepWidth = 56;
        internal string Id, Start, End, Source, Preset, Suggestion, LayoutText;
        internal bool SourceWarning;
        internal CheckBox Check;
        internal RichTextBox Editor;
        internal Button Locate, Revert, Accept, Keep;
        private Parts parts;
        private bool playing;

        internal struct Parts
        {
            public SubtitleColumns Columns;
            public Rectangle Editor, SuggestionText;
            public bool SuggestionWraps;
            public Point Accept;
            public int Height;
        }

        public SubtitleRowView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelTheme.Surface; ForeColor = PanelTheme.Ink;
        }
        internal void Attach()
        {
            foreach (var control in new Control[] { Check, Editor, Locate, Revert, Accept, Keep }.Where(c => c != null)) {
                control.Margin = Padding.Empty; control.AutoSize = false; Controls.Add(control);
            }
            Editor.Enter += (s, e) => { Editor.BackColor = PanelTheme.Input; Invalidate(); };
            Editor.Leave += (s, e) => { Editor.BackColor = BackColor; Invalidate(); };
        }
        // The subtitle under the playhead fills with the selection blue, as a VEGAS
        // list shows its selected row. A caption being edited keeps its well.
        internal bool Playing
        {
            get { return playing; }
            set {
                if (playing == value) return;
                playing = value; BackColor = value ? PanelTheme.Accent : PanelTheme.Surface;
                if (Editor != null && !Editor.ContainsFocus) Editor.BackColor = BackColor;
                foreach (Control child in Controls) child.Invalidate();
                Invalidate();
            }
        }

        private static int Measure(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text ?? "", font, new Size(Math.Max(40, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }
        // The editor is as tall as its text, so it shows no scrollbar of its own.
        private static int EditorHeight(string text, int width) { return Math.Max(20, Measure(text, PanelTheme.Caption, width - 8) + 4); }
        internal static Parts Arrange(int width, string text, string suggestion)
        {
            var columns = SubtitleColumns.For(width);
            var parts = new Parts { Columns = columns };
            int editorWidth = columns.TextCell.Width - (columns.Wide ? 8 : 6), editor = EditorHeight(text, editorWidth);
            parts.Editor = new Rectangle(columns.TextCell.X + 4, columns.Wide ? 2 : PanelTheme.Row, editorWidth, editor);
            int top = columns.Wide ? Math.Max(PanelTheme.Row, editor + 4) : PanelTheme.Row + editor + 3;
            if (suggestion != null) {
                int x = (columns.Wide ? columns.TextCell.X : SubtitleColumns.Check) + 6, available = Math.Max(60, width - x - 4);
                int single = TextRenderer.MeasureText(suggestion, PanelTheme.Small, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                if (single + 8 + AcceptWidth + 2 + KeepWidth <= available) {
                    parts.SuggestionText = new Rectangle(x, top, single + 2, PanelTheme.Row);
                    parts.Accept = new Point(x + single + 8, top);
                    top += PanelTheme.Row;
                } else {
                    int height = Measure(suggestion, PanelTheme.Small, available);
                    parts.SuggestionText = new Rectangle(x, top + 3, available, height); parts.SuggestionWraps = true;
                    parts.Accept = new Point(x - 3, top + 4 + height);
                    top += 4 + height + PanelTheme.Row;
                }
            }
            parts.Height = top + 2 + 1;
            return parts;
        }
        public static int Measure(SubtitleItem item, int width)
        {
            return Arrange(width, item.Text, item.Candidate == null ? null : SubtitleViewport.CandidateText(item)).Height;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            parts = Arrange(Width, LayoutText, Suggestion);
            var columns = parts.Columns;
            Check?.SetBounds(columns.CheckCell.X, columns.CheckCell.Y, columns.CheckCell.Width, columns.CheckCell.Height);
            Editor?.SetBounds(parts.Editor.X, parts.Editor.Y, parts.Editor.Width, parts.Editor.Height);
            Locate?.SetBounds(columns.ActionCell.X, 0, PanelTheme.Row, PanelTheme.Row);
            Revert?.SetBounds(columns.ActionCell.X + PanelTheme.Row, 0, PanelTheme.Row, PanelTheme.Row);
            Accept?.SetBounds(parts.Accept.X, parts.Accept.Y, AcceptWidth, PanelTheme.Row);
            Keep?.SetBounds(parts.Accept.X + AcceptWidth + 2, parts.Accept.Y, KeepWidth, PanelTheme.Row);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            if (parts.Columns == null) return;
            var columns = parts.Columns;
            Color time = playing ? PanelTheme.OnAccent : PanelTheme.InkBright, muted = playing ? PanelTheme.OnAccent : PanelTheme.Muted;
            Cell(g, Start, columns.StartCell, time);
            Cell(g, End, columns.EndCell, time);
            Cell(g, Source, columns.SourceCell, SourceWarning && !playing ? PanelTheme.Warning : muted);
            if (columns.Wide) Cell(g, Preset, columns.PresetCell, muted);
            if (Suggestion != null) TextRenderer.DrawText(g, Suggestion, PanelTheme.Small, parts.SuggestionText, muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                (parts.SuggestionWraps ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter));
            if (Editor != null && Editor.ContainsFocus)
                PanelTheme.Frame(g, new Rectangle(parts.Editor.X - 3, parts.Editor.Y - 1, parts.Editor.Width + 5, parts.Editor.Height + 2), PanelTheme.Input, PanelTheme.Accent);
            using (var seam = new SolidBrush(PanelTheme.Background)) g.FillRectangle(seam, 0, Height - 1, Width, 1);
        }
        private void Cell(Graphics g, string text, Rectangle cell, Color color)
        {
            TextRenderer.DrawText(g, text, Font, new Rectangle(cell.X + 6, cell.Y, Math.Max(0, cell.Width - 12), cell.Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    // Column names over the subtitle list, with its check-all box. It follows the
    // list's row width, which excludes the list's scrollbar.
    public sealed class SubtitleListHeader : Control
    {
        private readonly SubtitleViewport list;
        private readonly CheckBox all;
        public SubtitleListHeader(SubtitleViewport list, CheckBox all)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            this.list = list; this.all = all;
            BackColor = PanelTheme.Surface; ForeColor = PanelTheme.Muted; Height = PanelTheme.Row + 1;
            all.AutoSize = false; all.Margin = Padding.Empty; Controls.Add(all);
            list.ClientSizeChanged += (s, e) => { PerformLayout(); Invalidate(); };
            list.Layout += (s, e) => Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var cell = SubtitleColumns.For(list.RowWidth).CheckCell;
            all.SetBounds(cell.X, cell.Y, cell.Width, cell.Height);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            var columns = SubtitleColumns.For(list.RowWidth);
            var cells = new List<Tuple<Rectangle, string>> { Tuple.Create(columns.StartCell, "Start"), Tuple.Create(columns.EndCell, "End"), Tuple.Create(columns.SourceCell, "Source") };
            if (columns.Wide) { cells.Add(Tuple.Create(columns.TextCell, "Text")); cells.Add(Tuple.Create(columns.PresetCell, "Preset")); }
            using (var seam = new SolidBrush(PanelTheme.Background)) {
                foreach (var cell in cells) {
                    g.FillRectangle(seam, cell.Item1.X, 0, 1, PanelTheme.Row);
                    TextRenderer.DrawText(g, cell.Item2, Font, new Rectangle(cell.Item1.X + 6, 0, Math.Max(0, cell.Item1.Width - 12), PanelTheme.Row), PanelTheme.Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                g.FillRectangle(seam, 0, Height - 1, Width, 1);
            }
        }
    }

    // Only visible subtitle rows own native controls. Window resizing changes
    // their bounds once after a short pause, rather than relaying out every
    // RichEdit control in the project for every mouse movement. The list scrolls
    // itself under a VEGAS-style ScrollBar, which its container docks beside it.
    public sealed class SubtitleViewport : Panel
    {
        private IList<SubtitleItem> data = new List<SubtitleItem>();
        private Func<SubtitleItem, Control> createRow;
        private readonly Dictionary<int, Control> visible = new Dictionary<int, Control>();
        private readonly Timer resizeTimer = new Timer { Interval = 100 };
        private int[] offsets = new[] { 0 };
        private int scroll;
        private bool updating;
        internal StudioScrollBar ScrollBar { get; } = new StudioScrollBar { Dock = DockStyle.Right, Visible = false };
        public bool IsResizing { get { return resizeTimer.Enabled; } }
        public int MaterializedCount { get { return visible.Count; } }
        public int ItemCount { get { return data.Count; } }
        public int RowWidth { get { return Math.Max(240, ClientSize.Width); } }
        public int ScrollOffset { get { return scroll; } }
        public int ContentHeight { get { return offsets[data.Count]; } }
        public IEnumerable<Control> Rows { get { return visible.Values; } }

        public SubtitleViewport()
        {
            BackColor = PanelTheme.Surface;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            resizeTimer.Tick += (s, e) => FlushPendingLayout();
            ScrollBar.Scroll += (s, e) => ScrollTo(ScrollBar.Value);
        }
        public void SetItems(IList<SubtitleItem> items, Func<SubtitleItem, Control> factory, string emptyTitle = null, string emptyHint = null)
        {
            int previous = scroll;
            updating = true; SuspendLayout();
            try {
                foreach (Control control in visible.Values.ToArray()) { Controls.Remove(control); control.Dispose(); }
                visible.Clear();
                // Includes the empty-state label from the previous data set.
                while (Controls.Count > 0) Controls[0].Dispose();
                data = items; createRow = factory;
                BuildOffsets();
                if (data.Count == 0) Controls.Add(new EmptySubtitles(emptyTitle ?? "Ready for your first caption", emptyHint ?? "Select audio, then Generate.") {
                    Location = new Point(0, 0), Width = RowWidth, Height = 120 });
            } finally { ResumeLayout(true); updating = false; }
            ScrollTo(previous);
        }
        private void ScrollTo(int y)
        {
            scroll = Math.Max(0, Math.Min(ContentHeight - ClientSize.Height, y));
            ScrollBar.Value = scroll;
            UpdateVisibleRows();
        }
        // The bar shows only while the rows overflow; showing it narrows the list,
        // which the resize pass then refits.
        private void UpdateScrollBar()
        {
            bool needed = ContentHeight > ClientSize.Height;
            if (ScrollBar.Visible != needed) ScrollBar.Visible = needed;
            ScrollBar.SetRange(ContentHeight, ClientSize.Height);
        }
        protected override void OnSizeChanged(EventArgs args)
        {
            base.OnSizeChanged(args);
            if (resizeTimer == null || updating) return;
            UpdateScrollBar();
            resizeTimer.Stop(); resizeTimer.Start();
        }
        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            ScrollTo(scroll - args.Delta * Math.Max(1, SystemInformation.MouseWheelScrollLines) * PanelTheme.Row / 120);
            if (args is HandledMouseEventArgs) ((HandledMouseEventArgs)args).Handled = true;
        }
        public void FlushPendingLayout()
        {
            resizeTimer.Stop();
            if (IsDisposed || updating) return;
            updating = true; SuspendLayout();
            try {
                BuildOffsets();
                foreach (Control control in visible.Values) control.Width = RowWidth;
                if (data.Count == 0) foreach (Control control in Controls) control.Width = RowWidth;
            }
            finally { ResumeLayout(true); updating = false; }
            ScrollTo(scroll);
        }
        public void ScrollToItem(int index)
        {
            if (index < 0 || index >= data.Count) return;
            ScrollTo(offsets[index]);
        }
        // Brings a row into view a third of the way down, unless it is already shown.
        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= data.Count) return;
            if (offsets[index] >= scroll && offsets[index + 1] <= scroll + ClientSize.Height) return;
            ScrollTo(offsets[index] - ClientSize.Height / 3);
        }
        // Shared with SubtitlerPanel.CreateRow so the height estimate matches the row.
        public static string CandidateText(SubtitleItem item) { return "Suggested: " + item.Candidate.Text; }
        private void BuildOffsets()
        {
            offsets = new int[data.Count + 1];
            int width = RowWidth;
            for (int i = 0; i < data.Count; i++) offsets[i + 1] = offsets[i] + SubtitleRowView.Measure(data[i], width);
            UpdateScrollBar();
        }
        private int AtOffset(int y)
        {
            int found = Array.BinarySearch(offsets, Math.Max(0, y));
            if (found < 0) found = ~found - 1;
            return Math.Max(0, Math.Min(data.Count - 1, found));
        }
        private void UpdateVisibleRows()
        {
            if (updating || data.Count == 0 || createRow == null || IsDisposed) return;
            updating = true; SuspendLayout();
            bool grew = false;
            try {
                int first = AtOffset(scroll - 150), last = AtOffset(scroll + Math.Max(100, ClientSize.Height) + 150);
                foreach (int index in visible.Keys.Where(i => (i < first || i > last) && !visible[i].ContainsFocus).ToArray()) {
                    Control old = visible[index]; visible.Remove(index); Controls.Remove(old); old.Dispose();
                }
                for (int i = first; i <= last; i++) {
                    Control row;
                    if (!visible.TryGetValue(i, out row)) {
                        row = createRow(data[i]); row.AutoSize = false; visible[i] = row; Controls.Add(row);
                    }
                }
                foreach (var row in visible.OrderBy(pair => pair.Key)) {
                    int i = row.Key;
                    row.Value.SetBounds(0, offsets[i] - scroll, RowWidth, offsets[i + 1] - offsets[i]);
                    int needed = row.Value.Controls.Cast<Control>().Select(c => c.Bottom + c.Margin.Bottom + row.Value.Padding.Bottom).DefaultIfEmpty(0).Max();
                    if (needed > row.Value.Height) {
                        int extra = needed - row.Value.Height;
                        for (int j = i + 1; j < offsets.Length; j++) offsets[j] += extra;
                        grew = true;
                    }
                }
                if (grew) {
                    UpdateScrollBar();
                    foreach (var row in visible) row.Value.SetBounds(0, offsets[row.Key] - scroll, RowWidth, offsets[row.Key + 1] - offsets[row.Key]);
                }
            } finally { ResumeLayout(grew); updating = false; }
            Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { resizeTimer.Stop(); resizeTimer.Dispose(); }
            base.Dispose(disposing);
        }

        private sealed class EmptySubtitles : Control
        {
            private readonly string title, hint;
            internal EmptySubtitles(string title, string hint)
            {
                this.title = title; this.hint = hint;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                BackColor = PanelTheme.Surface;
                AccessibleName = title + ". " + hint;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                PanelTheme.Icon(e.Graphics, "subtitles", new Rectangle(Width / 2 - 8, 24, 16, 16), PanelTheme.Muted);
                TextRenderer.DrawText(e.Graphics, title, PanelTheme.Heading, new Rectangle(0, 50, Width, 20), PanelTheme.InkBright,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, hint, Font, new Rectangle(0, 72, Width, 20), PanelTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
