using System.Drawing.Drawing2D;

namespace VrcTranslator.Desktop;

internal static class SettingsTheme
{
    public static readonly Color Canvas = Color.FromArgb(242, 247, 249);
    public static readonly Color Surface = Color.FromArgb(248, 250, 251);
    public static readonly Color Ink = Color.FromArgb(23, 32, 34);
    public static readonly Color Muted = Color.FromArgb(82, 98, 106);
    public static readonly Color Line = Color.FromArgb(214, 223, 229);
    public static readonly Color InputLine = Color.FromArgb(170, 182, 190);
    public static readonly Color Teal = Color.FromArgb(14, 104, 95);
    public static readonly Color DarkTeal = Color.FromArgb(9, 73, 66);
    public static readonly Color TealTint = Color.FromArgb(229, 243, 240);
    public static readonly Color Green = Color.FromArgb(6, 165, 112);

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Box(Graphics g, RectangleF bounds, float radius, Color fill, Color? outline = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var shape = Rounded(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, shape);
        if (outline is { } color)
        {
            using var pen = new Pen(color);
            g.DrawPath(pen, shape);
        }
    }
}

internal enum SettingsButtonStyle { Neutral, Outline, Primary, Plain, Tab }
internal enum SettingsGlyph { None, Refresh, Eye, EyeOff, Plus, Folder, Chevron, Down }

internal sealed class SettingsButton : Button
{
    private bool hovered;
    private bool pressed;
    private bool selected;
    private SettingsGlyph glyph;
    private readonly SettingsButtonStyle style;

    public SettingsButton(string text, SettingsButtonStyle style = SettingsButtonStyle.Neutral, SettingsGlyph glyph = SettingsGlyph.None)
    {
        Text = text;
        this.style = style;
        this.glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public void SetSelected(bool value) { selected = value; Invalidate(); }
    public void SetGlyph(SettingsGlyph value) { glyph = value; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        bool hot = hovered || pressed;
        Color ink = Enabled ? SettingsTheme.Ink : SettingsTheme.Muted;
        switch (style)
        {
            case SettingsButtonStyle.Primary:
                Color fill = !Enabled ? SettingsTheme.Line : hot ? SettingsTheme.DarkTeal : SettingsTheme.Teal;
                SettingsTheme.Box(g, bounds, 6 * scale, fill);
                if (Enabled) ink = Color.White;
                break;
            case SettingsButtonStyle.Neutral:
                SettingsTheme.Box(g, bounds, 5 * scale, hot ? SettingsTheme.TealTint : Color.FromArgb(244, 247, 249), SettingsTheme.InputLine);
                break;
            case SettingsButtonStyle.Outline:
                SettingsTheme.Box(g, bounds, 5 * scale, hot ? SettingsTheme.TealTint : SettingsTheme.Surface, SettingsTheme.Teal);
                ink = SettingsTheme.DarkTeal;
                break;
            case SettingsButtonStyle.Plain when hot:
                SettingsTheme.Box(g, bounds, 5 * scale, SettingsTheme.TealTint);
                break;
            case SettingsButtonStyle.Tab when selected:
                ink = SettingsTheme.DarkTeal;
                SettingsTheme.Box(g, new(4 * scale, Height - 4 * scale, Width - 8 * scale, 3.5f * scale), 2 * scale, SettingsTheme.Teal);
                break;
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        using var emphasis = (style == SettingsButtonStyle.Tab && selected) || style == SettingsButtonStyle.Primary ? new Font(Font, FontStyle.Bold) : null;
        Font buttonFont = emphasis ?? Font;
        int inset = (int)(5 * scale);
        if (glyph == SettingsGlyph.None)
        {
            var textArea = new Rectangle(inset, 0, Math.Max(0, Width - inset * 2), Height);
            TextRenderer.DrawText(g, Text, buttonFont, textArea, ink, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            int iconSize = (int)Math.Round(18 * scale);
            int gap = Text.Length > 0 ? (int)(7 * scale) : 0;
            int textWidth = Text.Length == 0 ? 0 : TextRenderer.MeasureText(g, Text, buttonFont, new Size(10000, Height), flags).Width;
            int start = Math.Max(inset, (Width - textWidth - iconSize - gap) / 2);
            DrawGlyph(g, glyph, start, (Height - iconSize) / 2f, iconSize, ink);
            if (Text.Length > 0)
            {
                int left = start + iconSize + gap;
                TextRenderer.DrawText(g, Text, buttonFont, new Rectangle(left, 0, Math.Max(0, Width - left - inset), Height), ink, flags | TextFormatFlags.EndEllipsis);
            }
        }

        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -4, -4);
            if (focus.Width > 0 && focus.Height > 0) ControlPaint.DrawFocusRectangle(g, focus, ink, BackColor);
        }
    }

    private static void DrawGlyph(Graphics g, SettingsGlyph icon, float x, float y, float size, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(size / 18, size / 18);
        using var pen = new Pen(color, 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case SettingsGlyph.Refresh:
                g.DrawArc(pen, 2.5f, 2.5f, 13, 13, -42, 309);
                g.DrawLines(pen, [new PointF(10.5f, 5), new(15.1f, 4.5f), new(14.5f, 0.9f)]);
                break;
            case SettingsGlyph.Eye:
            case SettingsGlyph.EyeOff:
                using (var eye = new GraphicsPath())
                {
                    eye.AddBezier(1, 9, 5, 2, 13, 2, 17, 9);
                    eye.AddBezier(17, 9, 13, 16, 5, 16, 1, 9);
                    g.DrawPath(pen, eye);
                }
                g.DrawEllipse(pen, 6.5f, 6.5f, 5, 5);
                if (icon == SettingsGlyph.EyeOff) g.DrawLine(pen, 2, 16, 16, 2);
                break;
            case SettingsGlyph.Plus:
                g.DrawLine(pen, 9, 3, 9, 15);
                g.DrawLine(pen, 3, 9, 15, 9);
                break;
            case SettingsGlyph.Folder:
                g.DrawPolygon(pen, [new(1, 4), new(7, 4), new(9, 6), new(17, 6), new(17, 15), new(1, 15)]);
                break;
            case SettingsGlyph.Chevron:
                g.DrawLines(pen, [new(6, 4), new(11, 9), new(6, 14)]);
                break;
            case SettingsGlyph.Down:
                g.DrawLines(pen, [new(4, 6), new(9, 11), new(14, 6)]);
                break;
        }
        g.Restore(state);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
}

internal sealed class SettingsCard : Panel
{
    public SettingsCard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        SettingsTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 9 * DeviceDpi / 96f, SettingsTheme.Surface, SettingsTheme.Line);
    }
}

// The pages share reference-image coordinates while font sizes and heights follow Windows DPI.
internal sealed class SettingsPage : Panel
{
    private readonly List<(Control Control, Rectangle Design)> placements = [];
    public SettingsPage()
    {
        BackColor = SettingsTheme.Surface;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public T Place<T>(T control, int x, int y, int width, int height) where T : Control
    {
        placements.Add((control, new(x, y, width, height)));
        Controls.Add(control);
        return control;
    }

    public void MoveControl(Control control, int y, int? height = null)
    {
        int index = placements.FindIndex(entry => entry.Control == control);
        var bounds = placements[index].Design;
        bounds.Y = y;
        if (height is { } value) bounds.Height = value;
        placements[index] = (control, bounds);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        float horizontal = ClientSize.Width / 682f;
        float vertical = DeviceDpi / 96f;
        foreach (var (control, design) in placements)
        {
            int x = (int)Math.Round(design.X * horizontal);
            int y = (int)Math.Round(design.Y * vertical);
            int right = (int)Math.Round(design.Right * horizontal);
            int bottom = (int)Math.Round(design.Bottom * vertical);
            control.SetBounds(x, y, right - x, bottom - y);
        }
    }
}

internal sealed class TextFrame : Panel
{
    private readonly TextBox input;
    private readonly SettingsButton? reveal;

    public TextFrame(TextBox input, bool password = false, bool preview = false)
    {
        this.input = input;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Fill = preview ? Color.FromArgb(235, 241, 245) : Color.FromArgb(253, 254, 254);
        input.BorderStyle = BorderStyle.None;
        input.BackColor = Fill;
        input.ForeColor = SettingsTheme.Ink;
        input.Enter += (_, _) => Invalidate();
        input.Leave += (_, _) => Invalidate();
        Controls.Add(input);
        if (password)
        {
            reveal = new("", SettingsButtonStyle.Plain, SettingsGlyph.Eye) { BackColor = Fill, AccessibleName = "显示 " + input.AccessibleName };
            Controls.Add(reveal);
            reveal.Click += (_, _) =>
            {
                input.UseSystemPasswordChar = !input.UseSystemPasswordChar;
                reveal.SetGlyph(input.UseSystemPasswordChar ? SettingsGlyph.Eye : SettingsGlyph.EyeOff);
                reveal.AccessibleName = (input.UseSystemPasswordChar ? "显示 " : "隐藏 ") + input.AccessibleName;
            };
        }
    }

    private Color Fill { get; }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (input is null) return;
        float scale = DeviceDpi / 96f;
        int pad = (int)(10 * scale);
        int trailing = reveal is null ? pad : (int)(40 * scale);
        int y = input.Multiline ? pad : Math.Max(0, (Height - input.PreferredHeight) / 2);
        input.SetBounds(pad, y, Math.Max(0, Width - pad - trailing), input.Multiline ? Math.Max(0, Height - pad * 2) : input.PreferredHeight);
        if (reveal is not null) reveal.SetBounds(Width - trailing, 3, trailing - 3, Math.Max(0, Height - 6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        SettingsTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 5 * DeviceDpi / 96f, Fill,
            input.Focused ? SettingsTheme.Teal : SettingsTheme.InputLine);
    }

    public void MaskPassword()
    {
        if (reveal is null) return;
        input.UseSystemPasswordChar = true;
        reveal.SetGlyph(SettingsGlyph.Eye);
        reveal.AccessibleName = "显示 " + input.AccessibleName;
    }
}

internal sealed class SettingsSwitch : CheckBox
{
    public SettingsSwitch(string name)
    {
        AccessibleName = name;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float width = Math.Min(48 * s, Width - 2);
        float height = 23 * s;
        float y = (Height - height) / 2;
        SettingsTheme.Box(g, new(1, y, width, height), height / 2, Checked ? SettingsTheme.Teal : Color.FromArgb(159, 169, 175));
        float d = height - 7 * s;
        using var dot = new SolidBrush(Color.White);
        g.FillEllipse(dot, Checked ? width - d - 3 * s : 1 + 3.5f * s, y + 3.5f * s, d, d);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}

internal sealed class SettingsNumber(string unit) : NumericUpDown
{
    protected override void UpdateEditText()
    {
        base.UpdateEditText();
        if (string.IsNullOrEmpty(unit) || Focused || Text.EndsWith(unit, StringComparison.Ordinal)) return;
        ChangingText = true;
        Text = (unit is " 秒" or "°" ? Value.ToString("0.##") : Text) + unit;
    }

    protected override void OnEnter(EventArgs e)
    {
        StripUnit();
        base.OnEnter(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        UpdateEditText();
    }

    protected override void ValidateEditText()
    {
        StripUnit();
        base.ValidateEditText();
    }

    private void StripUnit()
    {
        if (string.IsNullOrEmpty(unit) || !Text.EndsWith(unit, StringComparison.Ordinal)) return;
        ChangingText = true;
        Text = Text[..^unit.Length];
    }
}

internal sealed class NumberFrame : Panel
{
    private readonly NumericUpDown number;
    public NumberFrame(NumericUpDown number)
    {
        this.number = number;
        DoubleBuffered = true;
        ResizeRedraw = true;
        number.BorderStyle = BorderStyle.None;
        number.BackColor = Color.FromArgb(253, 254, 254);
        number.Enter += (_, _) => Invalidate();
        number.Leave += (_, _) => Invalidate();
        Controls.Add(number);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (number is null) return;
        int pad = (int)Math.Round(8 * DeviceDpi / 96f);
        number.SetBounds(pad, Math.Max(0, (Height - number.PreferredHeight) / 2), Math.Max(0, Width - pad - 3), number.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        SettingsTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 5 * DeviceDpi / 96f, number.BackColor,
            number.ContainsFocus ? SettingsTheme.Teal : SettingsTheme.InputLine);
    }
}

internal sealed class StatusIndicator : Control
{
    private Color color = SettingsTheme.InputLine;

    public StatusIndicator() => ResizeRedraw = true;

    public void SetConnected(bool connected)
    {
        var next = connected ? SettingsTheme.Green : SettingsTheme.InputLine;
        if (color == next) return;
        color = next;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float diameter = 12 * DeviceDpi / 96f;
        using var dot = new SolidBrush(color);
        e.Graphics.FillEllipse(dot, (Width - diameter) / 2, (Height - diameter) / 2, diameter, diameter);
    }
}

internal sealed class SettingsBadge : Label
{
    public SettingsBadge()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SettingsTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        SettingsTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), 5 * DeviceDpi / 96f, BackColor);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SettingsSlider : Control
{
    private readonly NumericUpDown value;
    private readonly bool fineAtStart;

    public SettingsSlider(NumericUpDown value, bool fineAtStart)
    {
        this.value = value;
        this.fineAtStart = fineAtStart;
        AccessibleName = value.AccessibleName;
        AccessibleRole = AccessibleRole.Slider;
        TabStop = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Cursor = Cursors.Hand;
        value.ValueChanged += (_, _) => { AccessibleDescription = value.Value.ToString(); Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float distance = Width - 16 * s;
        double fraction = (double)((value.Value - value.Minimum) / (value.Maximum - value.Minimum));
        if (fineAtStart) fraction = Math.Sqrt(fraction);
        float end = 8 * s + (float)fraction * distance;
        float y = Height / 2f;
        using var track = new Pen(Color.FromArgb(200, 207, 211), 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, 8 * s, y, Width - 8 * s, y);
        track.Color = SettingsTheme.Teal;
        g.DrawLine(track, 8 * s, y, end, y);
        using var brush = new SolidBrush(SettingsTheme.Teal);
        using var ring = new Pen(SettingsTheme.Surface, 1.4f * s);
        var thumb = new RectangleF(end - 7 * s, y - 7 * s, 14 * s, 14 * s);
        g.FillEllipse(brush, thumb);
        g.DrawEllipse(ring, thumb);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Capture = true;
        SetPosition(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) SetPosition(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        decimal next = e.KeyCode switch
        {
            Keys.Left or Keys.Down => value.Value - value.Increment,
            Keys.Right or Keys.Up => value.Value + value.Increment,
            Keys.Home => value.Minimum,
            Keys.End => value.Maximum,
            _ => value.Value
        };
        value.Value = Math.Clamp(next, value.Minimum, value.Maximum);
    }

    private void SetPosition(int x)
    {
        float inset = 8 * DeviceDpi / 96f;
        double fraction = Math.Clamp((x - inset) / Math.Max(1, Width - 2 * inset), 0, 1);
        if (fineAtStart) fraction *= fraction;
        decimal next = value.Minimum + (decimal)fraction * (value.Maximum - value.Minimum);
        value.Value = Math.Clamp(decimal.Round(next, value.DecimalPlaces), value.Minimum, value.Maximum);
    }
}

internal sealed class SegmentedMeter : Control
{
    private int level;
    public SegmentedMeter()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleName = "输入电平";
        AccessibleRole = AccessibleRole.ProgressBar;
    }
    public void SetLevel(int value)
    {
        if (level == value) return;
        level = value;
        AccessibleDescription = value + "%";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        SettingsTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), 5 * s, SettingsTheme.Surface, SettingsTheme.InputLine);
        const int segments = 16;
        float slot = (Width - 8 * s) / segments;
        int filled = (level * segments + 99) / 100;
        for (int i = 0; i < segments; i++)
            SettingsTheme.Box(g, new(4 * s + i * slot, 5 * s, slot - 4 * s, Height - 10 * s), 2 * s,
                i < filled ? SettingsTheme.Green : Color.FromArgb(219, 225, 229));
    }
}
