using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Subtitler.VegasPlugin
{
    // VEGAS Pro 22's own look, as recorded in the "Subtitler" design system: two flat
    // greys with 1px seams between regions, square corners, 9pt Segoe UI, 22 px
    // controls and one blue for selection and the primary action.
    internal static class PanelTheme
    {
        // Sampled from VEGAS's docked windows: Background shows through as seams and
        // splitters and fills inputs; every region is Surface.
        internal static readonly Color Background = Color.FromArgb(17, 17, 17), Surface = Color.FromArgb(34, 34, 34), Input = Color.FromArgb(17, 17, 17),
            Hover = Color.FromArgb(45, 45, 45), Thumb = Color.FromArgb(68, 68, 68), ThumbHover = Color.FromArgb(85, 85, 85), ControlLine = Color.FromArgb(119, 119, 119),
            Ink = Color.FromArgb(220, 220, 220), InkBright = Color.FromArgb(238, 238, 238), Muted = Color.FromArgb(176, 176, 176),
            Faint = Color.FromArgb(102, 102, 102), Disabled = Color.FromArgb(83, 83, 83),
            Accent = Color.FromArgb(0, 120, 212), AccentDeep = Color.FromArgb(0, 90, 158), OnAccent = Color.White,
            Warning = Color.FromArgb(234, 185, 103), Error = Color.FromArgb(255, 123, 95);
        internal static readonly Font Small = new Font("Segoe UI", 8.25f), Heading = new Font("Segoe UI Semibold", 9f),
            Caption = new Font("Segoe UI", 9.75f), CaptionUnderline = new Font("Segoe UI", 9.75f, FontStyle.Underline);
        internal const int Row = 22, SidebarWidth = 176, RailWidth = 30, RailBelow = 520;

        // A square fill with a 1px frame drawn inside it.
        internal static void Frame(Graphics g, Rectangle rect, Color fill, Color border)
        {
            if (rect.Width < 1 || rect.Height < 1) return;
            using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, rect);
            if (border != fill) using (var pen = new Pen(border)) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
        // Native scrollbars stay light in a dark panel unless their window uses the
        // dark Explorer theme (Windows 10 1809 and later; ignored elsewhere).
        internal static void DarkScrollbars(Control control)
        {
            control.HandleCreated += (s, e) => SetWindowTheme(((Control)s).Handle, "DarkMode_Explorer", null);
            if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        // 16 px glyphs on a pixel grid: 1px square-ended strokes, solid triangles for
        // play and dropdowns, like VEGAS's transport and toolbar icons. Straight lines
        // are drawn without antialiasing so they stay crisp.
        internal static void Icon(Graphics g, string name, Rectangle rect, Color color)
        {
            var saved = g.Save();
            try {
                g.TranslateTransform(rect.X + (rect.Width - 16) / 2, rect.Y + (rect.Height - 16) / 2);
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(color, 1f))
                using (var thick = new Pen(color, 1.5f))
                using (var brush = new SolidBrush(color)) {
                    Action smooth = () => g.SmoothingMode = SmoothingMode.AntiAlias;
                    switch (name) {
                        case "subtitles": g.DrawRectangle(pen, 1, 2, 13, 11); g.DrawLine(pen, 4, 8, 6, 8); g.DrawLine(pen, 9, 8, 11, 8); g.DrawLine(pen, 4, 10, 11, 10); break;
                        case "presets": g.DrawRectangle(pen, 1, 1, 13, 13); g.DrawLine(pen, 4, 4, 12, 4); g.DrawLine(pen, 8, 4, 8, 12); break;
                        case "options":
                            for (int y = 4; y <= 12; y += 4) g.DrawLine(pen, 2, y, 13, y);
                            g.FillRectangle(brush, 5, 2, 2, 5); g.FillRectangle(brush, 9, 6, 2, 5); g.FillRectangle(brush, 4, 10, 2, 5); break;
                        case "play": smooth(); g.FillPolygon(brush, new[] { new PointF(4, 2), new PointF(13.5f, 8), new PointF(4, 14) }); break;
                        case "close": smooth(); g.DrawLine(pen, 4, 4, 12, 12); g.DrawLine(pen, 12, 4, 4, 12); break;
                        case "plus": g.DrawLine(pen, 8, 3, 8, 13); g.DrawLine(pen, 3, 8, 13, 8); break;
                        case "refresh":
                            smooth(); g.DrawArc(pen, 3, 3, 10, 10, -10, 290);
                            g.DrawLines(pen, new[] { new PointF(6.5f, 1), new PointF(9, 3.2f), new PointF(6.5f, 5.4f) }); break;
                        case "locate":
                            g.DrawLine(pen, 8, 0, 8, 3); g.DrawLine(pen, 8, 13, 8, 15); g.DrawLine(pen, 0, 8, 3, 8); g.DrawLine(pen, 13, 8, 15, 8);
                            smooth(); g.DrawEllipse(pen, 4, 4, 8, 8); break;
                        case "check": smooth(); g.DrawLines(pen, new[] { new PointF(2.5f, 8.5f), new PointF(6, 12), new PointF(13.5f, 4) }); break;
                        case "edit":
                            smooth(); g.DrawPolygon(pen, new[] { new PointF(2.5f, 13.5f), new PointF(3.5f, 10), new PointF(11, 2.5f), new PointF(13.5f, 5), new PointF(6, 12.5f) });
                            g.DrawLine(pen, 9.5f, 4, 12, 6.5f); break;
                        case "copy": g.DrawRectangle(pen, 5, 5, 9, 9); g.DrawLines(pen, new[] { new Point(2, 11), new Point(2, 2), new Point(11, 2) }); break;
                        case "rename": g.DrawRectangle(pen, 1, 5, 13, 6); g.DrawLine(pen, 4, 7, 4, 9); break;
                        case "trash":
                            g.DrawLine(pen, 2, 3, 13, 3); g.DrawLines(pen, new[] { new Point(6, 3), new Point(6, 1), new Point(10, 1), new Point(10, 3) });
                            g.DrawLines(pen, new[] { new Point(3, 3), new Point(4, 14), new Point(12, 14), new Point(13, 3) }); break;
                        case "revert":
                            g.DrawLine(pen, 2, 6, 10, 6); g.DrawLine(pen, 10, 13, 6, 13);
                            smooth(); g.DrawLines(pen, new[] { new PointF(5, 3), new PointF(2, 6), new PointF(5, 9) }); g.DrawArc(pen, 6.5f, 6, 7, 7, -90, 180); break;
                        case "search": smooth(); g.DrawEllipse(thick, 2, 2, 8.5f, 8.5f); g.DrawLine(thick, 9.5f, 9.5f, 14, 14); break;
                        case "drop": g.FillPolygon(brush, new[] { new Point(4, 6), new Point(12, 6), new Point(8, 10) }); break;
                        case "up": g.FillPolygon(brush, new[] { new Point(4, 10), new Point(12, 10), new Point(8, 6) }); break;
                        case "expand": g.DrawRectangle(pen, 4, 4, 8, 8); g.DrawLine(pen, 6, 8, 10, 8); g.DrawLine(pen, 8, 6, 8, 10); break;
                        case "collapse": g.DrawRectangle(pen, 4, 4, 8, 8); g.DrawLine(pen, 6, 8, 10, 8); break;
                    }
                }
            } finally { g.Restore(saved); }
        }
    }

    internal enum ButtonTone { Standard, Primary, Quiet }

    // Status-bar progress: a 10 px square bar centred in the control, accent on the
    // seam colour, with a moving block while the length is unknown.
    internal sealed class StudioProgressBar : ProgressBar
    {
        private readonly Timer pulse = new Timer { Interval = 40 };
        private int offset;
        internal StudioProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            MarqueeAnimationSpeed = 0; Minimum = 0; Maximum = 100;
            pulse.Tick += (s, e) => { offset = (offset + 8) % Math.Max(1, Width + Math.Max(24, Width / 4)); Invalidate(); };
        }
        internal void Report(int? percent)
        {
            var style = percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (Style != style) Style = style;
            if (percent.HasValue) Value = Math.Max(0, Math.Min(100, percent.Value));
            UpdatePulse(); Invalidate();
        }
        private void UpdatePulse() { if (pulse != null) pulse.Enabled = Visible && Style == ProgressBarStyle.Marquee; }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdatePulse(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelTheme.Surface);
            var track = new Rectangle(0, (Height - 10) / 2, Width, 10);
            using (var well = new SolidBrush(PanelTheme.Background)) e.Graphics.FillRectangle(well, track);
            int width = Style == ProgressBarStyle.Marquee ? Math.Max(24, Width / 4) : Width * Value / 100;
            int x = Style == ProgressBarStyle.Marquee ? offset - width : 0;
            e.Graphics.SetClip(track);
            using (var fill = new SolidBrush(PanelTheme.Accent)) e.Graphics.FillRectangle(fill, x, track.Y, width, track.Height);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { pulse.Stop(); pulse.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Primary: the view's one accent action. Standard: a framed button for forms.
    // Quiet: a VEGAS tool button, frameless until hovered, optionally glyph-only
    // (its Text remains its accessible name and tooltip subject).
    internal sealed class StudioButton : Button
    {
        private bool hovered, pressed;
        internal string Glyph { get; set; }
        internal ButtonTone Tone { get; set; }
        internal bool IconOnly { get; set; }
        internal StudioButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            BackColor = PanelTheme.Surface; ForeColor = PanelTheme.Ink;
            // A button grows but never shrinks by default, keeping its 75 px design size.
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Margin = Padding.Empty; Padding = new Padding(10, 0, 10, 0);
            MinimumSize = new Size(0, PanelTheme.Row);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if (IconOnly) return new Size(PanelTheme.Row, PanelTheme.Row);
            var text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + (string.IsNullOrEmpty(Glyph) ? 0 : 20)), Math.Max(MinimumSize.Height, text.Height + 6));
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var back = Parent?.BackColor ?? PanelTheme.Surface;
            g.Clear(back);
            bool primary = Tone == ButtonTone.Primary, quiet = Tone == ButtonTone.Quiet, active = Enabled && (hovered || pressed);
            Color fill, border, ink, glyph;
            if (!Enabled) {
                fill = back; border = quiet ? back : PanelTheme.Hover; ink = glyph = PanelTheme.Disabled;
            } else if (primary) {
                fill = border = active ? PanelTheme.AccentDeep : PanelTheme.Accent; ink = glyph = PanelTheme.OnAccent;
            } else if (quiet) {
                // On a playing (accent) row the hover deepens the accent instead.
                bool onAccent = back.ToArgb() == PanelTheme.Accent.ToArgb();
                fill = border = active ? (onAccent ? PanelTheme.AccentDeep : PanelTheme.Hover) : back;
                ink = onAccent ? PanelTheme.OnAccent : PanelTheme.Ink; glyph = onAccent ? PanelTheme.OnAccent : PanelTheme.InkBright;
            } else {
                fill = active ? PanelTheme.Thumb : PanelTheme.Hover; border = PanelTheme.ControlLine; ink = PanelTheme.Ink; glyph = PanelTheme.InkBright;
            }
            PanelTheme.Frame(g, ClientRectangle, fill, border);
            if (Focused && ShowFocusCues)
                using (var focus = new Pen(primary ? PanelTheme.OnAccent : PanelTheme.Accent)) {
                    int inset = primary ? 2 : 0;
                    g.DrawRectangle(focus, inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
                }
            if (IconOnly) { PanelTheme.Icon(g, Glyph, new Rectangle((Width - 16) / 2, (Height - 16) / 2, 16, 16), glyph); return; }
            int start = Padding.Left;
            if (!string.IsNullOrEmpty(Glyph)) { PanelTheme.Icon(g, Glyph, new Rectangle(start, (Height - 16) / 2, 16, 16), glyph); start += 20; }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(start, 0, Math.Max(0, Width - start - Padding.Right + 2), Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // A 13 px square box, accent when checked. Without text the box is centred,
    // as in the subtitle list's check column.
    internal sealed class StudioCheckBox : CheckBox
    {
        private bool hovered;
        internal StudioCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = true; UseMnemonic = false; Margin = Padding.Empty;
            MinimumSize = new Size(0, PanelTheme.Row);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if (string.IsNullOrEmpty(Text)) return new Size(PanelTheme.Row, PanelTheme.Row);
            return new Size(TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + 22, PanelTheme.Row);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnCheckStateChanged(EventArgs e) { Invalidate(); base.OnCheckStateChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? PanelTheme.Surface);
            bool bare = string.IsNullOrEmpty(Text);
            var box = new Rectangle(bare ? (Width - 13) / 2 : 1, (Height - 13) / 2, 13, 13);
            bool marked = CheckState != CheckState.Unchecked;
            // A marked box on a playing (accent) row keeps a white frame to stay visible.
            bool onAccent = (Parent?.BackColor ?? PanelTheme.Surface).ToArgb() == PanelTheme.Accent.ToArgb();
            if (!Enabled) PanelTheme.Frame(g, box, PanelTheme.Surface, PanelTheme.Hover);
            else PanelTheme.Frame(g, box, marked ? PanelTheme.Accent : PanelTheme.Input,
                marked ? (onAccent ? PanelTheme.OnAccent : PanelTheme.Accent) : hovered ? PanelTheme.Muted : PanelTheme.ControlLine);
            if (Focused && ShowFocusCues) using (var focus = new Pen(PanelTheme.Accent)) g.DrawRectangle(focus, box.X - 2, box.Y - 2, box.Width + 3, box.Height + 3);
            if (CheckState == CheckState.Checked) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var tick = new Pen(Enabled ? PanelTheme.OnAccent : PanelTheme.Disabled, 1.5f))
                    g.DrawLines(tick, new[] { new PointF(box.X + 3, box.Y + 6.5f), new PointF(box.X + 5.5f, box.Y + 9), new PointF(box.X + 10, box.Y + 3.5f) });
            } else if (CheckState == CheckState.Indeterminate)
                using (var bar = new SolidBrush(PanelTheme.OnAccent)) g.FillRectangle(bar, box.X + 3, box.Y + 6, 7, 1);
            if (!bare) TextRenderer.DrawText(g, Text, Font, new Rectangle(21, 0, Math.Max(0, Width - 21), Height), Enabled ? ForeColor : PanelTheme.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    // A square well with a small solid triangle; framed in accent only while focused.
    // The open list is a Surface menu with an accent selection.
    internal sealed class StudioComboBox : ComboBox
    {
        internal StudioComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = PanelTheme.Input; ForeColor = PanelTheme.Ink; ItemHeight = 16; IntegralHeight = false; DropDownHeight = 240;
            Margin = Padding.Empty;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, selected = !edit && (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(edit ? PanelTheme.Input : selected ? PanelTheme.Accent : PanelTheme.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
            // SingleLine centres like the labels, buttons and check boxes: without it
            // WinForms centres the text itself, a pixel lower.
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, new Rectangle(e.Bounds.X + 5, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 7), e.Bounds.Height),
                !Enabled ? PanelTheme.Disabled : selected ? PanelTheme.OnAccent : PanelTheme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F /* WM_PAINT */ || !IsHandleCreated || Width < 24) return;
            // WinForms' flat style paints a light square frame and button. Everything
            // except the owner-drawn item is painted over.
            var info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) };
            var item = GetComboBoxInfo(Handle, ref info) ? Rectangle.FromLTRB(info.Item.Left, info.Item.Top, info.Item.Right, info.Item.Bottom) : new Rectangle(3, 3, Width - 25, Height - 6);
            using (var g = Graphics.FromHwnd(Handle)) {
                g.SetClip(item, CombineMode.Exclude);
                PanelTheme.Frame(g, new Rectangle(0, 0, Width, Height), PanelTheme.Input, Enabled && Focused ? PanelTheme.Accent : PanelTheme.Input);
                PanelTheme.Icon(g, "drop", new Rectangle(Width - 19, (Height - 16) / 2, 16, 16), Enabled ? PanelTheme.InkBright : PanelTheme.Disabled);
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ComboInfo { public int Size; public NativeRect Item, Button; public int ButtonState; public IntPtr Combo, Edit, List; }
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboInfo info);
    }

    // The Video FX search field: a well with an italic placeholder and a magnifier at
    // the right, which becomes a clear button once something is typed. Esc clears it.
    internal sealed class StudioSearchBox : Panel
    {
        private readonly SearchInput input = new SearchInput();
        private bool clearHovered;
        internal TextBox Input { get { return input; } }
        internal string Placeholder { get { return input.Placeholder; } set { input.Placeholder = value; input.Invalidate(); } }
        public override string Text { get { return input.Text; } set { input.Text = value; } }
        internal StudioSearchBox()
        {
            BackColor = PanelTheme.Input; Height = PanelTheme.Row; Padding = new Padding(6, 0, 24, 0); Cursor = Cursors.IBeam; Margin = Padding.Empty;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            input.TextChanged += (s, e) => Invalidate();
            input.Enter += (s, e) => Invalidate(); input.Leave += (s, e) => Invalidate();
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && input.TextLength > 0) { input.Clear(); e.Handled = e.SuppressKeyPress = true; } };
            Controls.Add(input);
        }
        private Rectangle GlyphBounds { get { return new Rectangle(Width - 21, (Height - 18) / 2, 18, 18); } }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            input.SetBounds(Padding.Left, (Height - input.Height) / 2, Math.Max(10, Width - Padding.Horizontal), input.Height);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PanelTheme.Frame(e.Graphics, ClientRectangle, BackColor, input.Focused ? PanelTheme.Accent : BackColor);
            if (input.TextLength == 0) { PanelTheme.Icon(e.Graphics, "search", GlyphBounds, PanelTheme.InkBright); return; }
            if (clearHovered) using (var hover = new SolidBrush(PanelTheme.Hover)) e.Graphics.FillRectangle(hover, GlyphBounds);
            PanelTheme.Icon(e.Graphics, "close", GlyphBounds, PanelTheme.InkBright);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = input.TextLength > 0 && GlyphBounds.Contains(e.Location);
            if (over != clearHovered) { clearHovered = over; Cursor = over ? Cursors.Default : Cursors.IBeam; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { if (clearHovered) { clearHovered = false; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && input.TextLength > 0 && GlyphBounds.Contains(e.Location)) input.Clear();
            input.Focus();
            base.OnMouseDown(e);
        }

        private sealed class SearchInput : TextBox
        {
            private static readonly Font Italic = new Font("Segoe UI", 9f, FontStyle.Italic);
            internal string Placeholder;
            internal SearchInput() { BorderStyle = BorderStyle.None; BackColor = PanelTheme.Input; ForeColor = PanelTheme.Ink; }
            protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
            // The native edit control paints itself; the placeholder is drawn over an empty field.
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F /* WM_PAINT */ || TextLength > 0 || string.IsNullOrEmpty(Placeholder)) return;
                HideCaret(Handle);
                try {
                    using (var g = Graphics.FromHwnd(Handle))
                        TextRenderer.DrawText(g, Placeholder, Italic, new Rectangle(0, 0, Width, Height), PanelTheme.Faint,
                            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                } finally { ShowCaret(Handle); }
            }
            [DllImport("user32.dll")] private static extern bool HideCaret(IntPtr hwnd);
            [DllImport("user32.dll")] private static extern bool ShowCaret(IntPtr hwnd);
        }
    }

    // Binds sections and wrapping rows to the available dock width. The caption
    // viewport remains responsible for coalescing expensive editor resize work.
    internal class StudioStack : FlowLayoutPanel
    {
        private bool fitting;
        // For a docked stack: its height follows its content after each layout. A
        // docking parent asks an auto-sized stack for its height before the children
        // re-wrap at a new width, leaving it one width change behind.
        internal bool FitHeight { get; set; }
        internal StudioStack()
        {
            FlowDirection = FlowDirection.TopDown; WrapContents = false; Margin = Padding.Empty;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        // Every child reports Visible = false while the page is hidden.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (FitHeight && Visible) PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            // ClientSize already excludes a visible native scrollbar.
            if (ClientSize.Width > 0) Fit(ClientSize.Width);
            base.OnLayout(e);
            if (!FitHeight || !Visible) return;
            int height = Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max() + Padding.Bottom;
            if (Height != height) Height = height;
        }
        private void Fit(int clientWidth)
        {
            if (fitting) return;
            fitting = true;
            try {
                int width = Math.Max(80, clientWidth - Padding.Horizontal);
                foreach (Control child in Controls) {
                    int available = Math.Max(60, width - child.Margin.Horizontal);
                    if (child is ListBox || child is StudioHeading) {
                        // Setting a width-only MaximumSize on ListBox also
                        // resets its native height. It already has fixed height.
                        if (child.Width != available) child.Width = available;
                    } else if (child is FlowLayoutPanel || child is TableLayoutPanel) {
                        if (child.MinimumSize.Width != available || child.MaximumSize.Width != available) {
                            child.SuspendLayout();
                            try {
                                child.MaximumSize = new Size(available, 0);
                                child.MinimumSize = new Size(available, child.MinimumSize.Height);
                                child.Width = available;
                            } finally { child.ResumeLayout(true); }
                        }
                        // A labelled field inside a row wraps its input below its label
                        // rather than overflowing a narrow dock.
                        foreach (var nested in child.Controls.OfType<StudioLine>()) {
                            int limit = Math.Max(60, available - child.Padding.Horizontal - nested.Margin.Horizontal);
                            if (nested.MaximumSize.Width != limit) nested.MaximumSize = new Size(limit, 0);
                        }
                    } else if (child is Label && child.MaximumSize.Width != available) child.MaximumSize = new Size(available, 0);
                }
            } finally { fitting = false; }
        }
    }

    // A section heading followed by a 1px seam to the right edge.
    internal sealed class StudioHeading : Control
    {
        internal StudioHeading()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Height = PanelTheme.Row; Margin = new Padding(0, 0, 0, 4); Font = PanelTheme.Heading;
        }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelTheme.Surface);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int text = TextRenderer.MeasureText(Text, Font, Size.Empty, flags).Width;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height), PanelTheme.InkBright, flags);
            using (var seam = new SolidBrush(PanelTheme.Background)) e.Graphics.FillRectangle(seam, text + 8, Height / 2, Math.Max(0, Width - text - 8), 1);
        }
    }

    // Toolbar rows whose controls share one centre line, so captions, check
    // boxes, inputs and buttons of different heights align. A row has a left
    // group, an optional fill control taking the remaining width, and a right
    // group, which wraps onto its own line when the row does not fit.
    internal sealed class StudioToolbar : Panel
    {
        private sealed class Row { internal Control[] Left, Right; internal Control Fill; internal int FillMinimum; }
        private const int Gap = 4, LineGap = 4;
        private readonly List<Row> rows = new List<Row>();
        internal StudioToolbar()
        {
            BackColor = PanelTheme.Surface; Padding = new Padding(4);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        // A fill narrower than its minimum moves below the left group. A label fill
        // shares its line with the right group only while it fits unwrapped.
        internal void AddRow(Control[] left, Control fill, int fillMinimum, params Control[] right)
        {
            rows.Add(new Row { Left = left, Fill = fill, FillMinimum = fillMinimum, Right = right });
            foreach (var control in left.Concat(right)) Controls.Add(control);
            if (fill != null) Controls.Add(fill);
        }
        // Every child reports Visible = false while the page is hidden.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!Visible) return;
            int width = Math.Max(120, ClientSize.Width - Padding.Horizontal), top = Padding.Top;
            var line = new List<Control>();
            foreach (var row in rows) {
                var right = row.Right.Where(c => c.Visible).ToList();
                var fill = row.Fill != null && row.Fill.Visible ? row.Fill : null;
                int x = 0;
                foreach (var control in row.Left.Where(c => c.Visible)) {
                    if (line.Count > 0 && x + control.Width > width) { top = Finish(line, top); x = 0; }
                    Put(line, control, x); x += control.Width + Gap;
                }
                int rightWidth = right.Sum(c => c.Width) + Gap * Math.Max(0, right.Count - 1);
                int beside = fill == null ? 0 : fill is Label ? Math.Max(row.FillMinimum, Natural(fill)) : row.FillMinimum;
                bool shared = right.Count > 0 && x + (fill != null ? beside + Gap : 0) + rightWidth <= width;
                if (fill != null) {
                    if (line.Count > 0 && x + row.FillMinimum > width) { top = Finish(line, top); x = 0; }
                    Fit(fill, width - x - (shared ? rightWidth + Gap : 0));
                    Put(line, fill, x); x += fill.Width + Gap;
                }
                if (shared) {
                    int rx = width - rightWidth;
                    foreach (var control in right) { Put(line, control, rx); rx += control.Width + Gap; }
                } else if (right.Count > 0) {
                    top = Finish(line, top); x = 0;
                    foreach (var control in right) {
                        if (line.Count > 0 && x + control.Width > width) { top = Finish(line, top); x = 0; }
                        Put(line, control, x); x += control.Width + Gap;
                    }
                }
                top = Finish(line, top);
            }
            int height = Math.Max(Padding.Vertical, top - LineGap + Padding.Bottom);
            if (Height != height) Height = height;
        }
        private void Put(List<Control> line, Control control, int x) { control.Left = Padding.Left + x; line.Add(control); }
        private static int Finish(List<Control> line, int top)
        {
            if (line.Count == 0) return top;
            int height = Math.Max(PanelTheme.Row, line.Max(c => c.Height));
            foreach (var control in line) control.Top = top + (height - control.Height) / 2;
            line.Clear();
            return top + height + LineGap;
        }
        private static int Natural(Control label)
        {
            return TextRenderer.MeasureText(label.Text, label.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Width + label.Padding.Horizontal + 2;
        }
        // An auto-sized label wraps within the width; other controls stretch to it.
        private static void Fit(Control control, int width)
        {
            width = Math.Max(1, width);
            if (control is Label && control.AutoSize) { control.MaximumSize = new Size(width, 0); control.Size = control.GetPreferredSize(Size.Empty); }
            else control.Width = width;
        }
    }

    // A toolbar group divider: 1px, 18 px tall.
    internal sealed class StudioSeparator : Control
    {
        internal StudioSeparator()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Size = new Size(9, PanelTheme.Row); Margin = Padding.Empty;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelTheme.Surface);
            using (var line = new SolidBrush(PanelTheme.Thumb)) e.Graphics.FillRectangle(line, Width / 2, (Height - 18) / 2, 1, 18);
        }
    }

    internal sealed class StudioLine : FlowLayoutPanel
    {
        internal StudioLine()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; WrapContents = true; Margin = Padding.Empty; Padding = Padding.Empty;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    // A square well for a borderless single-line input, as tall as a dropdown.
    internal sealed class StudioInputFrame : Panel
    {
        private readonly Control input;
        internal StudioInputFrame(Control input)
        {
            this.input = input;
            BackColor = PanelTheme.Input; Width = input.Width; Height = PanelTheme.Row; Margin = Padding.Empty; Padding = new Padding(6, 0, 1, 0); Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            input.Margin = Padding.Empty;
            Controls.Add(input);
            input.Enter += (s, e) => Invalidate(); input.Leave += (s, e) => Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int line = input is StudioNumber ? ((StudioNumber)input).TextHeight : input.Height;
            input.SetBounds(Padding.Left, (Height - line) / 2, Math.Max(10, Width - Padding.Horizontal), input.Height);
        }
        protected override void OnMouseDown(MouseEventArgs e) { input.Focus(); base.OnMouseDown(e); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PanelTheme.Frame(e.Graphics, ClientRectangle, BackColor, input.ContainsFocus ? PanelTheme.Accent : BackColor);
        }
    }

    // A borderless number box for StudioInputFrame. WinForms draws its spin
    // buttons with the light system theme; they are painted over in its Paint
    // event, which follows that drawing on the same buffer.
    internal sealed class StudioNumber : NumericUpDown
    {
        private readonly TextBox edit;
        internal StudioNumber()
        {
            BorderStyle = BorderStyle.None; BackColor = PanelTheme.Input; ForeColor = PanelTheme.Ink;
            edit = Controls.OfType<TextBox>().First();
            Controls.Cast<Control>().First(c => c != edit).Paint += PaintButtons;
        }
        // The text box is pinned to the top of the control and fills its height, so
        // the line of digits, not the control, is what a frame centres.
        internal int TextHeight { get { return edit.PreferredHeight; } }
        private void PaintButtons(object sender, PaintEventArgs e)
        {
            var buttons = (Control)sender;
            int half = buttons.Height / 2, centre = TextHeight / 2;
            var pointer = buttons.PointToClient(MousePosition);
            bool over = buttons.ClientRectangle.Contains(pointer) && Enabled;
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillRectangle(fill, buttons.ClientRectangle);
            if (over) using (var hover = new SolidBrush(MouseButtons == MouseButtons.Left ? PanelTheme.Thumb : PanelTheme.Hover))
                e.Graphics.FillRectangle(hover, 0, pointer.Y < half ? 0 : half, buttons.Width, half);
            // Two 4 px triangles centred 8 px apart on the line of digits.
            var ink = Enabled ? PanelTheme.Ink : PanelTheme.Disabled;
            PanelTheme.Icon(e.Graphics, "up", new Rectangle((buttons.Width - 16) / 2, centre - 4 - 8, 16, 16), ink);
            PanelTheme.Icon(e.Graphics, "drop", new Rectangle((buttons.Width - 16) / 2, centre + 4 - 8, 16, 16), ink);
        }
    }

    // VEGAS's own scrollbar, as in the Video FX plug-in list: a flat thumb with a grip
    // on the panel grey and a small triangle at each end. Native scrollbars cannot take
    // these colours. Value runs from 0 to the content height less the view height.
    internal sealed class StudioScrollBar : Control
    {
        internal const int Thickness = 16;
        private const int MinimumThumb = 20;
        private readonly Timer repeat = new Timer();
        private int value, content, view = 1, dragOffset = -1, pressed, hot;
        private Action repeating;
        internal int SmallChange { get; set; } = PanelTheme.Row * 3;
        internal event EventHandler Scroll;
        internal StudioScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Width = Thickness; BackColor = PanelTheme.Surface;
            repeat.Tick += (s, e) => { repeat.Interval = 50; repeating?.Invoke(); };
        }
        internal int Range { get { return Math.Max(0, content - view); } }
        internal int Value { get { return value; } set { ScrollTo(value, false); } }
        internal void SetRange(int contentHeight, int viewHeight)
        {
            content = Math.Max(0, contentHeight); view = Math.Max(1, viewHeight);
            value = Math.Max(0, Math.Min(Range, value)); Invalidate();
        }
        internal void Wheel(int delta) { ScrollTo(value - delta * Math.Max(1, SystemInformation.MouseWheelScrollLines) * PanelTheme.Row / 120, true); }
        private void ScrollTo(int target, bool notify)
        {
            target = Math.Max(0, Math.Min(Range, target));
            if (target == value) return;
            value = target; Invalidate();
            if (notify) Scroll?.Invoke(this, EventArgs.Empty);
        }
        private Rectangle Up { get { return new Rectangle(0, 0, Width, Thickness); } }
        private Rectangle Down { get { return new Rectangle(0, Height - Thickness, Width, Thickness); } }
        private Rectangle Track { get { return new Rectangle(0, Thickness, Width, Math.Max(0, Height - Thickness * 2)); } }
        private Rectangle Thumb
        {
            get {
                var track = Track;
                if (Range == 0 || track.Height < MinimumThumb) return Rectangle.Empty;
                int length = Math.Min(track.Height, Math.Max(MinimumThumb, (int)((long)track.Height * view / content)));
                return new Rectangle(0, track.Y + (int)((long)(track.Height - length) * value / Range), Width, length);
            }
        }
        // 1 up arrow, 2 down arrow, 3 thumb, 4 track above, 5 track below.
        private int At(Point point)
        {
            if (Up.Contains(point)) return 1;
            if (Down.Contains(point)) return 2;
            var thumb = Thumb;
            return thumb.Contains(point) ? 3 : thumb.IsEmpty ? 0 : point.Y < thumb.Y ? 4 : 5;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            bool enabled = Range > 0;
            foreach (var arrow in new[] { 1, 2 }) {
                var bounds = arrow == 1 ? Up : Down;
                if (enabled && (pressed == arrow || hot == arrow)) using (var fill = new SolidBrush(pressed == arrow ? PanelTheme.Thumb : PanelTheme.Hover)) g.FillRectangle(fill, bounds);
                PanelTheme.Icon(g, arrow == 1 ? "up" : "drop", bounds, enabled ? PanelTheme.Ink : PanelTheme.Disabled);
            }
            var thumb = Thumb;
            if (thumb.IsEmpty) return;
            using (var fill = new SolidBrush(dragOffset >= 0 || hot == 3 ? PanelTheme.ThumbHover : PanelTheme.Thumb)) g.FillRectangle(fill, thumb);
            if (thumb.Height >= MinimumThumb)
                using (var grip = new SolidBrush(PanelTheme.ControlLine)) {
                    int x = thumb.X + (thumb.Width - 6) / 2, y = thumb.Y + thumb.Height / 2;
                    g.FillRectangle(grip, x, y - 2, 6, 1); g.FillRectangle(grip, x, y + 1, 6, 1);
                }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Range == 0) return;
            pressed = At(e.Location);
            if (pressed == 3) { dragOffset = e.Y - Thumb.Y; Capture = true; }
            else if (pressed == 1) Start(() => ScrollTo(value - SmallChange, true));
            else if (pressed == 2) Start(() => ScrollTo(value + SmallChange, true));
            // Paging stops once the thumb reaches the pointer.
            else if (pressed == 4) Start(() => { if (Thumb.Y > PointToClient(MousePosition).Y) ScrollTo(value - view, true); });
            else if (pressed == 5) Start(() => { if (Thumb.Bottom < PointToClient(MousePosition).Y) ScrollTo(value + view, true); });
            Invalidate();
        }
        private void Start(Action action) { repeating = action; action(); repeat.Interval = 350; repeat.Start(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragOffset >= 0) {
                var track = Track; int travel = track.Height - Thumb.Height;
                if (travel > 0) ScrollTo((int)((long)(e.Y - dragOffset - track.Y) * Range / travel), true);
                return;
            }
            int at = At(e.Location);
            if (at != hot) { hot = at; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            repeat.Stop(); repeating = null; dragOffset = -1; pressed = 0; Capture = false; Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != 0) { hot = 0; Invalidate(); } }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Wheel(e.Delta);
            if (e is HandledMouseEventArgs) ((HandledMouseEventArgs)e).Handled = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { repeat.Stop(); repeat.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Scrolls a stack that fits its own height, using a StudioScrollBar in place of
    // the light native scrollbar an AutoScroll panel shows.
    internal sealed class StudioScroller : Panel
    {
        private readonly StudioStack content;
        private readonly StudioScrollBar bar = new StudioScrollBar { Dock = DockStyle.Right, Visible = false };
        private bool arranging;
        internal StudioScroller(StudioStack content)
        {
            this.content = content;
            content.AutoScroll = false; content.FitHeight = true; content.Dock = DockStyle.None; content.Location = Point.Empty;
            BackColor = content.BackColor;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Controls.Add(content); Controls.Add(bar);
            bar.Scroll += (s, e) => content.Top = -bar.Value;
            content.SizeChanged += (s, e) => Arrange();
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }
        // A stack taller than the view is refitted beside the bar; one that then fits
        // at full width loses it again.
        private void Arrange()
        {
            if (arranging || ClientSize.Width <= 0) return;
            arranging = true;
            try {
                for (int pass = 0; pass < 2; pass++) {
                    int width = Math.Max(1, ClientSize.Width - (bar.Visible ? bar.Width : 0));
                    if (content.Width != width) { content.Width = width; content.PerformLayout(); }
                    bool needed = content.Height > ClientSize.Height;
                    if (needed == bar.Visible) break;
                    bar.Visible = needed; PerformLayout();
                }
                bar.SetRange(content.Height, ClientSize.Height);
                content.Top = -bar.Value;
            } finally { arranging = false; }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            bar.Wheel(e.Delta);
            if (e is HandledMouseEventArgs) ((HandledMouseEventArgs)e).Handled = true;
        }
    }

    internal sealed class SidebarEntry
    {
        internal string Text, Count, Glyph, Hint;
        internal int Level;
        internal bool Expandable;
        internal Action Select;
    }

    // The panel's navigation, drawn like VEGAS's Project Media tree: Subtitles,
    // which expands into one entry per subtitle track, Presets and Options. Below
    // the rail width it shows only the sections' glyphs.
    internal sealed class StudioSidebar : Control
    {
        private List<SidebarEntry> entries = new List<SidebarEntry>();
        private int selected = -1, hovered = -1;
        private bool collapsed, rail;
        internal event Action<SidebarEntry> HoverChanged;
        internal StudioSidebar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = PanelTheme.Surface; ForeColor = PanelTheme.Ink; TabStop = true;
            AccessibleRole = AccessibleRole.Outline; AccessibleName = "Subtitler sections";
        }
        internal bool Rail { get { return rail; } set { if (rail == value) return; rail = value; hovered = -1; Invalidate(); } }
        internal int Selected { get { return selected; } set { if (selected == value) return; selected = value; Invalidate(); } }
        internal IReadOnlyList<SidebarEntry> Entries { get { return entries; } }
        internal void SetEntries(List<SidebarEntry> list, int select) { entries = list; selected = select; hovered = -1; Invalidate(); }
        private int RowHeight { get { return rail ? PanelTheme.RailWidth : PanelTheme.Row; } }
        private int FirstRow { get { return rail ? 0 : 4; } }
        private List<int> Shown()
        {
            return Enumerable.Range(0, entries.Count).Where(i => entries[i].Level == 0 || (!rail && !collapsed)).ToList();
        }
        // The entry that shows the selection: a hidden track's Subtitles section.
        private int Marked()
        {
            if (selected < 0 || selected >= entries.Count) return -1;
            if (entries[selected].Level == 0 || (!rail && !collapsed)) return selected;
            for (int i = selected; i >= 0; i--) if (entries[i].Level == 0) return i;
            return -1;
        }
        private int At(Point point)
        {
            var shown = Shown(); int row = (point.Y - FirstRow) / RowHeight;
            return point.Y >= FirstRow && row < shown.Count ? shown[row] : -1;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            var shown = Shown(); int marked = Marked();
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            for (int row = 0; row < shown.Count; row++) {
                int i = shown[row]; var entry = entries[i];
                var bounds = new Rectangle(0, FirstRow + row * RowHeight, Width, RowHeight);
                bool on = i == marked;
                if (on || i == hovered) using (var fill = new SolidBrush(on ? PanelTheme.Accent : PanelTheme.Hover)) g.FillRectangle(fill, bounds);
                Color ink = on ? PanelTheme.OnAccent : PanelTheme.Ink, glyph = on ? PanelTheme.OnAccent : PanelTheme.InkBright;
                if (on && Focused && ShowFocusCues) using (var focus = new Pen(PanelTheme.OnAccent)) g.DrawRectangle(focus, 1, bounds.Y + 1, Width - 3, bounds.Height - 3);
                if (rail) { if (entry.Glyph != null) PanelTheme.Icon(g, entry.Glyph, bounds, glyph); continue; }
                int x = 4;
                if (entry.Level == 0) {
                    if (entry.Expandable) PanelTheme.Icon(g, collapsed ? "expand" : "collapse", new Rectangle(x, bounds.Y + 3, 16, 16), ink);
                    x += 20;
                    if (entry.Glyph != null) PanelTheme.Icon(g, entry.Glyph, new Rectangle(x, bounds.Y + 3, 16, 16), glyph);
                    x += 20;
                } else x += 20;
                int count = entry.Count == null ? 0 : TextRenderer.MeasureText(entry.Count, Font, Size.Empty, flags).Width;
                if (count > 0) TextRenderer.DrawText(g, entry.Count, Font, new Rectangle(Width - 8 - count, bounds.Y, count, bounds.Height), on ? PanelTheme.OnAccent : PanelTheme.Muted, flags);
                TextRenderer.DrawText(g, entry.Text, Font, new Rectangle(x, bounds.Y, Math.Max(0, Width - 8 - x - (count > 0 ? count + 6 : 0)), bounds.Height), ink,
                    flags | TextFormatFlags.EndEllipsis);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int at = At(e.Location);
            if (at != hovered) { hovered = at; Invalidate(); HoverChanged?.Invoke(at >= 0 ? entries[at] : null); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { if (hovered >= 0) { hovered = -1; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            int at = At(e.Location);
            if (at < 0) return;
            if (!rail && entries[at].Expandable && e.X < 24) { collapsed = !collapsed; hovered = -1; Invalidate(); return; }
            entries[at].Select?.Invoke();
        }
        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData);
        }
        // Up and Down move between entries that can be shown; an expanded Subtitles
        // section is skipped, as its tracks are what selecting it shows.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!rail && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)) { collapsed = e.KeyCode == Keys.Left; Invalidate(); e.Handled = true; return; }
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
            var stops = Shown().Where(i => !(entries[i].Expandable && !rail && !collapsed)).ToList();
            int position = stops.IndexOf(Marked());
            int next = position + (e.KeyCode == Keys.Up ? -1 : 1);
            if (position >= 0 && next >= 0 && next < stops.Count) entries[stops[next]].Select?.Invoke();
            e.Handled = true;
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
