using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BigPictureManager
{
    /// <summary>
    /// A self-drawn, borderless popup that replaces the native tray <see cref="ContextMenuStrip"/>.
    /// Gives full control over padding, icons (Segoe MDL2 Assets) and colour for a modern look.
    /// Build it fresh on each open with the Add* methods, then call <see cref="ShowAtCursor"/>.
    /// </summary>
    internal sealed class TrayMenu : Form
    {
        // Segoe MDL2 Assets glyph code points (present on Windows 10/11) used as crisp, DPI-independent icons.
        internal static readonly string GlyphAudio = Glyph(0xE767);       // Volume
        internal static readonly string GlyphNightLight = Glyph(0xE708);  // QuietHours (moon)
        internal static readonly string GlyphPause = Glyph(0xE769);       // Pause
        internal static readonly string GlyphController = Glyph(0xE7FC);  // Game
        internal static readonly string GlyphBluetooth = Glyph(0xE702);   // Bluetooth
        internal static readonly string GlyphStartup = Glyph(0xE7E8);     // PowerButton
        internal static readonly string GlyphAbout = Glyph(0xE946);       // Info
        internal static readonly string GlyphExit = Glyph(0xE711);        // Cancel
        private static readonly string GlyphCheck = Glyph(0xE73E);        // CheckMark

        private static string Glyph(int codePoint) => ((char)codePoint).ToString();

        private enum RowKind { Title, Header, Separator, Action, Toggle, Radio }

        private sealed class Row
        {
            public RowKind Kind;
            public string Glyph;
            public string Text;
            public bool Enabled = true;
            public bool On;
            public bool ClosesOnClick;
            public Action OnClick;
            public int Top;
            public int Height;

            public bool Interactive => Enabled && (Kind == RowKind.Action || Kind == RowKind.Toggle || Kind == RowKind.Radio);
        }

        private readonly List<Row> _rows = new List<Row>();
        private int _hoverIndex = -1;

        private float _scale = 1f;
        private Font _textFont;
        private Font _headerFont;
        private Font _titleFont;
        private Font _iconFont;
        private Font _checkFont;

        public TrayMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = TrayMenuTheme.Background;
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — keep it out of Alt+Tab.
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW — a subtle system shadow.
                return cp;
            }
        }

        public void ClearRows()
        {
            _rows.Clear();
            _hoverIndex = -1;
        }

        public void AddTitle(string text) => _rows.Add(new Row { Kind = RowKind.Title, Text = text });

        public void AddHeader(string text) => _rows.Add(new Row { Kind = RowKind.Header, Text = text });

        public void AddSeparator() => _rows.Add(new Row { Kind = RowKind.Separator });

        public void AddAction(string glyph, string text, Action onClick) =>
            _rows.Add(new Row { Kind = RowKind.Action, Glyph = glyph, Text = text, OnClick = onClick, ClosesOnClick = true });

        public void AddToggle(string glyph, string text, bool isOn, bool enabled, Action onClick) =>
            _rows.Add(new Row { Kind = RowKind.Toggle, Glyph = glyph, Text = text, On = isOn, Enabled = enabled, OnClick = onClick });

        public void AddRadio(string text, bool selected, Action onClick) =>
            _rows.Add(new Row { Kind = RowKind.Radio, Text = text, On = selected, OnClick = onClick });

        /// <summary>A non-interactive, disabled text row (e.g. an empty-state message).</summary>
        public void AddInfo(string text) =>
            _rows.Add(new Row { Kind = RowKind.Action, Text = text, Enabled = false });

        /// <summary>Lays the menu out, positions it near the cursor and shows it focused.</summary>
        public void ShowAtCursor()
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
            }

            BuildFonts();
            LayoutRows();

            var cursor = Cursor.Position;
            var area = Screen.FromPoint(cursor).WorkingArea;
            var x = cursor.X - Width;
            var y = cursor.Y - Height;
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + Width > area.Right) x = area.Right - Width;
            if (y + Height > area.Bottom) y = area.Bottom - Height;
            Location = new Point(x, y);

            Show();
            // Take foreground so clicking elsewhere fires Deactivate and dismisses the menu.
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            Invalidate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Hide();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void BuildFonts()
        {
            var dpi = DeviceDpi <= 0 ? 96 : DeviceDpi;
            var scale = dpi / 96f;
            if (_textFont != null && Math.Abs(scale - _scale) < 0.01f)
            {
                return;
            }

            _scale = scale;
            DisposeFonts();
            _textFont = new Font("Segoe UI", 10.5f);
            _headerFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            _titleFont = new Font("Segoe UI Semibold", 11.5f);
            _iconFont = new Font("Segoe MDL2 Assets", 11f);
            _checkFont = new Font("Segoe MDL2 Assets", 10f);
        }

        private int Px(float logical) => (int)Math.Round(logical * _scale);

        private void LayoutRows()
        {
            var width = Px(280);
            var pad = Px(6);
            var y = pad;

            foreach (var row in _rows)
            {
                row.Top = y;
                switch (row.Kind)
                {
                    case RowKind.Title:
                        row.Height = Px(40);
                        break;
                    case RowKind.Header:
                        row.Height = Px(26);
                        break;
                    case RowKind.Separator:
                        row.Height = Px(9);
                        break;
                    default:
                        row.Height = Px(38);
                        break;
                }

                y += row.Height;
            }

            Width = width;
            Height = y + pad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(TrayMenuTheme.Background))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            for (var i = 0; i < _rows.Count; i++)
            {
                DrawRow(g, _rows[i], i == _hoverIndex);
            }

            using (var border = new Pen(TrayMenuTheme.Border))
            {
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        }

        private void DrawRow(Graphics g, Row row, bool hovered)
        {
            var pad = Px(6);
            var rowRect = new Rectangle(pad, row.Top, Width - 2 * pad, row.Height);

            switch (row.Kind)
            {
                case RowKind.Separator:
                {
                    var sy = row.Top + row.Height / 2;
                    using (var pen = new Pen(TrayMenuTheme.Separator))
                    {
                        g.DrawLine(pen, rowRect.Left + Px(8), sy, rowRect.Right - Px(8), sy);
                    }

                    return;
                }

                case RowKind.Title:
                {
                    var tr = new Rectangle(rowRect.Left + Px(12), rowRect.Top, rowRect.Width - Px(12), rowRect.Height);
                    TextRenderer.DrawText(g, row.Text, _titleFont, tr, TrayMenuTheme.TitleText, LabelFlags);
                    return;
                }

                case RowKind.Header:
                {
                    var tr = new Rectangle(rowRect.Left + Px(14), rowRect.Top, rowRect.Width - Px(14), rowRect.Height);
                    TextRenderer.DrawText(g, row.Text, _headerFont, tr, TrayMenuTheme.HeaderText, LabelFlags);
                    return;
                }
            }

            if (hovered)
            {
                using (var fill = new SolidBrush(TrayMenuTheme.HoverFill))
                using (var path = Rounded(new Rectangle(rowRect.Left, rowRect.Top + Px(1), rowRect.Width, rowRect.Height - Px(2)), Px(6)))
                {
                    g.FillPath(fill, path);
                }
            }

            var textColor = row.Enabled ? TrayMenuTheme.ItemText : TrayMenuTheme.DisabledText;
            var iconColor = row.Enabled ? TrayMenuTheme.Accent : TrayMenuTheme.DisabledText;

            var textLeft = rowRect.Left + Px(14);

            if (!string.IsNullOrEmpty(row.Glyph))
            {
                var iconRect = new Rectangle(rowRect.Left + Px(10), rowRect.Top, Px(24), rowRect.Height);
                TextRenderer.DrawText(g, row.Glyph, _iconFont, iconRect, iconColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                textLeft = rowRect.Left + Px(42);
            }

            var rightReserve = row.Kind == RowKind.Toggle ? Px(46) : Px(28);
            var labelRect = new Rectangle(textLeft, rowRect.Top, rowRect.Right - textLeft - rightReserve, rowRect.Height);
            TextRenderer.DrawText(g, row.Text, _textFont, labelRect, textColor, LabelFlags);

            if (row.Kind == RowKind.Toggle)
            {
                DrawSwitch(g, rowRect, row.On, row.Enabled);
            }
            else if (row.Kind == RowKind.Radio && row.On)
            {
                var checkRect = new Rectangle(rowRect.Right - Px(28), rowRect.Top, Px(24), rowRect.Height);
                TextRenderer.DrawText(g, GlyphCheck, _checkFont, checkRect, TrayMenuTheme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private void DrawSwitch(Graphics g, Rectangle rowRect, bool on, bool enabled)
        {
            var w = Px(36);
            var h = Px(18);
            var x = rowRect.Right - w - Px(6);
            var y = rowRect.Top + (rowRect.Height - h) / 2;
            var track = new Rectangle(x, y, w, h);

            var trackColor = !enabled
                ? TrayMenuTheme.SwitchOffDisabled
                : (on ? TrayMenuTheme.Accent : TrayMenuTheme.SwitchOff);

            using (var brush = new SolidBrush(trackColor))
            using (var path = Rounded(track, h / 2))
            {
                g.FillPath(brush, path);
            }

            var knobSize = h - Px(4);
            var knobX = on ? track.Right - knobSize - Px(2) : track.Left + Px(2);
            var knob = new Rectangle(knobX, y + Px(2), knobSize, knobSize);
            using (var knobBrush = new SolidBrush(Color.White))
            {
                g.FillEllipse(knobBrush, knob);
            }
        }

        private const TextFormatFlags LabelFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = RowIndexAt(e.Y);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        // Handle MouseUp (not Click): rapid clicks are merged into a DoubleClick, for which Click never
        // fires, so every other fast toggle would otherwise be dropped.
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var index = RowIndexAt(e.Y);
            if (index < 0)
            {
                return;
            }

            var row = _rows[index];
            var action = row.OnClick;

            if (row.ClosesOnClick)
            {
                Hide();
                if (action != null)
                {
                    BeginInvoke(action);
                }

                return;
            }

            // Toggle/radio: flip the displayed state instantly for snappy feedback, then apply the
            // (now debounced) backend change without blocking. The menu stays open.
            ApplyInstantVisual(row);
            Invalidate();
            Update();
            if (action != null)
            {
                BeginInvoke(action);
            }
        }

        private void ApplyInstantVisual(Row row)
        {
            if (row.Kind == RowKind.Toggle)
            {
                row.On = !row.On;
            }
            else if (row.Kind == RowKind.Radio)
            {
                foreach (var r in _rows)
                {
                    if (r.Kind == RowKind.Radio)
                    {
                        r.On = false;
                    }
                }

                row.On = true;
            }
        }

        private int RowIndexAt(int y)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Interactive && y >= row.Top && y < row.Top + row.Height)
                {
                    return i;
                }
            }

            return -1;
        }

        private GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            if (diameter <= 0 || diameter > bounds.Width || diameter > bounds.Height)
            {
                path.AddRectangle(bounds);
                return path;
            }

            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DisposeFonts()
        {
            _textFont?.Dispose();
            _headerFont?.Dispose();
            _titleFont?.Dispose();
            _iconFont?.Dispose();
            _checkFont?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeFonts();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Colour palette for the custom tray menu. Tweak here to retheme.</summary>
    internal static class TrayMenuTheme
    {
        internal static readonly Color Background = Color.FromArgb(0xFF, 0xFF, 0xFF);
        internal static readonly Color Border = Color.FromArgb(0xDF, 0xE3, 0xEA);
        internal static readonly Color Separator = Color.FromArgb(0xEC, 0xEE, 0xF2);

        internal static readonly Color ItemText = Color.FromArgb(0x22, 0x26, 0x2D);
        internal static readonly Color DisabledText = Color.FromArgb(0xB2, 0xB7, 0xC0);
        internal static readonly Color HeaderText = Color.FromArgb(0x8A, 0x90, 0x9B);
        internal static readonly Color TitleText = Color.FromArgb(0x1B, 0x26, 0x3E);

        internal static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xE4);
        internal static readonly Color HoverFill = Color.FromArgb(0xF1, 0xF5, 0xFD);

        internal static readonly Color SwitchOff = Color.FromArgb(0xC9, 0xCE, 0xD8);
        internal static readonly Color SwitchOffDisabled = Color.FromArgb(0xE4, 0xE7, 0xEC);
    }
}
