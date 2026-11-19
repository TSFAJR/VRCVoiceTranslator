using System.Drawing.Drawing2D;

namespace VrcTranslator.Desktop;

internal enum SettingsButtonStyle { Neutral, Outline, Primary, Plain, Navigation }
internal enum SettingsGlyph
{
    None, Refresh, Eye, EyeOff, Plus, Folder, Chevron, Down, Microphone, Translate, Chat, Headset, Audio, Sun, Moon, Monitor
}

internal class SettingsButton : Button
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
        DoubleBuffered = true;
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
        Color ink = Enabled ? UiTheme.Ink : UiTheme.Muted;
        switch (style)
        {
            case SettingsButtonStyle.Primary:
                UiTheme.Box(g, bounds, 7 * scale, !Enabled ? UiTheme.Track : hot ? UiTheme.TealHover : UiTheme.Teal);
                if (Enabled) ink = UiTheme.OnAccent;
                break;
            case SettingsButtonStyle.Neutral:
            case SettingsButtonStyle.Outline:
                UiTheme.Box(g, bounds, 6 * scale, hot && Enabled ? UiTheme.Track : UiTheme.Surface,
                    Enabled && (hot || style == SettingsButtonStyle.Outline) ? UiTheme.Teal : UiTheme.Line);
                if (Enabled && style == SettingsButtonStyle.Outline) ink = UiTheme.AccentText;
                break;
            case SettingsButtonStyle.Plain when hot:
                UiTheme.Box(g, bounds, 6 * scale, UiTheme.Track);
                break;
            case SettingsButtonStyle.Navigation when selected:
                ink = UiTheme.TealHover;
                UiTheme.Box(g, bounds, 7 * scale, UiTheme.TealTint);
                UiTheme.Box(g, new(0, 6 * scale, 3 * scale, Height - 12 * scale), 1.5f * scale, UiTheme.Teal);
                break;
            case SettingsButtonStyle.Navigation when hot:
                UiTheme.Box(g, bounds, 7 * scale, UiTheme.Track);
                break;
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        using var emphasis = (style == SettingsButtonStyle.Navigation && selected) || style == SettingsButtonStyle.Primary ? new Font(Font, FontStyle.Bold) : null;
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
            int start = style == SettingsButtonStyle.Navigation ? (int)(13 * scale) : Math.Max(inset, (Width - textWidth - iconSize - gap) / 2);
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

    internal static void DrawGlyph(Graphics g, SettingsGlyph icon, float x, float y, float size, Color color)
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
            case SettingsGlyph.Microphone:
                using (var capsule = UiTheme.Rounded(new(6.5f, 1, 5, 10), 2.5f)) g.DrawPath(pen, capsule);
                g.DrawArc(pen, 3.5f, 3, 11, 11, 0, 180);
                g.DrawLine(pen, 9, 14, 9, 17);
                break;
            case SettingsGlyph.Translate:
                g.DrawLine(pen, 1, 4, 10, 4);
                g.DrawLine(pen, 5.5f, 1.5f, 5.5f, 4);
                g.DrawBezier(pen, 8, 5, 7, 9, 5, 11, 1.5f, 13);
                g.DrawBezier(pen, 3, 6, 4, 9, 5.5f, 10, 8, 11);
                g.DrawLines(pen, [new PointF(9, 16), new(13, 6.5f), new(17, 16)]);
                g.DrawLine(pen, 10.5f, 13, 15.5f, 13);
                break;
            case SettingsGlyph.Chat:
                using (var bubble = new GraphicsPath())
                {
                    bubble.AddLines([new PointF(2, 14), new(2, 4), new(4, 2), new(14, 2), new(16, 4), new(16, 11),
                        new(14, 13), new(7, 13), new(3, 16), new(3, 13)]);
                    g.DrawPath(pen, bubble);
                }
                g.DrawLine(pen, 5, 6, 13, 6);
                g.DrawLine(pen, 5, 9, 11, 9);
                break;
            case SettingsGlyph.Headset:
                g.DrawPolygon(pen, [new(3, 4), new(15, 4), new(17, 6), new(17, 12), new(15, 14), new(12, 14),
                    new(10, 11), new(8, 11), new(6, 14), new(3, 14), new(1, 12), new(1, 6)]);
                g.DrawLine(pen, 5, 8, 6, 8);
                g.DrawLine(pen, 12, 8, 13, 8);
                break;
            case SettingsGlyph.Audio:
                g.DrawPolygon(pen, [new PointF(1, 6), new(4, 6), new(8, 2.5f), new(8, 15.5f), new(4, 12), new(1, 12)]);
                g.DrawArc(pen, 7, 5, 6, 8, -58, 116);
                g.DrawArc(pen, 6, 2, 11, 14, -56, 112);
                break;
            case SettingsGlyph.Sun:
                g.DrawEllipse(pen, 5.5f, 5.5f, 7, 7);
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * MathF.PI / 4;
                    float rayX = MathF.Cos(angle), rayY = MathF.Sin(angle);
                    g.DrawLine(pen, 9 + 6.1f * rayX, 9 + 6.1f * rayY, 9 + 8.1f * rayX, 9 + 8.1f * rayY);
                }
                break;
            case SettingsGlyph.Moon:
                using (var crescent = new GraphicsPath())
                {
                    crescent.AddBezier(8.2f, 1.5f, 2.7f, 2.1f, .6f, 8.1f, 3.4f, 12.7f);
                    crescent.AddBezier(3.4f, 12.7f, 6.5f, 17.5f, 14.4f, 17.3f, 16.2f, 10.6f);
                    crescent.AddBezier(16.2f, 10.6f, 10.5f, 13.7f, 4.3f, 8.2f, 8.2f, 1.5f);
                    crescent.CloseFigure();
                    g.DrawPath(pen, crescent);
                }
                break;
            case SettingsGlyph.Monitor:
                using (var screen = UiTheme.Rounded(new(1.5f, 2.5f, 15, 10.5f), 1.25f)) g.DrawPath(pen, screen);
                g.DrawLine(pen, 9, 13, 9, 16);
                g.DrawLine(pen, 6, 16, 12, 16);
                break;
        }
        g.Restore(state);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
}

internal sealed class NavButton(string text, SettingsGlyph glyph) : SettingsButton(text, SettingsButtonStyle.Navigation, glyph);

internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

// Coordinates are logical pixels at 96 DPI. The horizontal layout fits the available width.
internal class SettingsPage(int designWidth = 730) : BufferedPanel
{
    private readonly List<(Control Control, Rectangle Design)> placements = [];

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

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (designWidth <= 0) return;
        float horizontal = ClientSize.Width / (float)designWidth;
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

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }
}

internal sealed class CardPanel(int designWidth = 730) : SettingsPage(designWidth)
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        UiTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 10 * DeviceDpi / 96f, BackColor, UiTheme.Line);
    }
}

internal sealed class DarkComboBox : ComboBox
{
    private bool hovered;

    public DarkComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        SelectedIndexChanged += (_, _) => Invalidate();
        DropDownClosed += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiTheme.Input);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        int inset = (int)(7 * s);
        int arrowSlot = (int)(25 * s);
        var bounds = new Rectangle(inset, 0, Math.Max(0, Width - inset - arrowSlot), Height);
        TextRenderer.DrawText(g, GetItemText(SelectedItem), Font, bounds, Enabled ? UiTheme.Ink : UiTheme.Muted,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        SettingsButton.DrawGlyph(g, SettingsGlyph.Down, Width - 20 * s, (Height - 14 * s) / 2, 14 * s,
            Enabled && hovered ? UiTheme.TealHover : UiTheme.Muted);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
}

internal sealed class ComboFrame : BufferedPanel
{
    private readonly ComboBox combo;

    public ComboFrame(ComboBox combo)
    {
        this.combo = combo;
        combo.Enter += (_, _) => Invalidate();
        combo.Leave += (_, _) => Invalidate();
        Controls.Add(combo);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (combo is null) return;
        float s = DeviceDpi / 96f;
        int pad = (int)Math.Round(3 * s);
        int itemHeight = Math.Max(1, (int)Math.Round(18 * s));
        if (combo.ItemHeight != itemHeight) combo.ItemHeight = itemHeight;
        int y = Math.Max(0, (Height - combo.PreferredHeight) / 2);
        combo.SetBounds(pad, y, Math.Max(0, Width - 2 * pad), combo.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        UiTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 6 * DeviceDpi / 96f, UiTheme.Input,
            ContainsFocus ? UiTheme.Teal : UiTheme.Line);
    }
}

internal sealed class TextFrame : Panel
{
    private readonly TextBox input;
    private readonly bool preview;
    private readonly SettingsButton? reveal;

    public TextFrame(TextBox input, bool password = false, bool preview = false)
    {
        this.input = input;
        DoubleBuffered = true;
        ResizeRedraw = true;
        this.preview = preview;
        input.BorderStyle = BorderStyle.None;
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
        ApplyTheme();
    }

    private Color Fill => preview ? UiTheme.Canvas : UiTheme.Input;

    public void ApplyTheme()
    {
        input.BackColor = Fill;
        input.ForeColor = UiTheme.Ink;
        if (reveal is not null) reveal.BackColor = Fill;
    }

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
        UiTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), 5 * DeviceDpi / 96f, Fill,
            ContainsFocus ? UiTheme.Teal : UiTheme.Line);
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
        DoubleBuffered = true;
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
        bool on = Checked;
        UiTheme.Box(g, new(1, y, width, height), height / 2, on && Enabled ? UiTheme.Teal : UiTheme.Track, UiTheme.Line);
        float d = Math.Min(height - 7 * s, width * .34f);
        using var dot = new SolidBrush(Enabled ? on ? UiTheme.OnAccent : UiTheme.Muted : UiTheme.Rail);
        float dotX = on ? width - d - 3 * s : 1 + 3.5f * s;
        g.FillEllipse(dot, dotX, y + (height - d) / 2, d, d);
        using var hint = new Font(Font.FontFamily, 7f, FontStyle.Bold);
        int textLeft = (int)(on ? 3 * s : dotX + d + s);
        int textRight = (int)(on ? dotX - s : width - 2 * s);
        var text = Rectangle.FromLTRB(textLeft, 0, Math.Max(textLeft, textRight), Height);
        TextRenderer.DrawText(g, on ? "ON" : "OFF", hint, text, on && Enabled ? UiTheme.OnAccent : UiTheme.Muted,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
    private readonly bool pill;
    public NumberFrame(NumericUpDown number, bool pill = false)
    {
        this.number = number;
        DoubleBuffered = true;
        ResizeRedraw = true;
        number.BorderStyle = BorderStyle.None;
        this.pill = pill;
        ApplyTheme();
        // Leave the WinForms increment/decrement hit testing, repeat and keyboard behavior in place.
        foreach (Control child in number.Controls)
        {
            if (child is TextBox) continue;
            child.Paint += (_, e) => PaintArrows(e.Graphics, child);
        }
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
        UiTheme.Box(e.Graphics, new(.5f, .5f, Width - 1, Height - 1), (pill ? 14 : 6) * DeviceDpi / 96f, number.BackColor,
            number.ContainsFocus ? UiTheme.Teal : UiTheme.Line);
    }

    public void ApplyTheme()
    {
        number.BackColor = pill ? UiTheme.Track : UiTheme.Input;
        number.ForeColor = UiTheme.Ink;
    }

    private static void PaintArrows(Graphics g, Control buttons)
    {
        g.Clear(buttons.Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = buttons.DeviceDpi / 96f;
        var pointer = buttons.PointToClient(Cursor.Position);
        bool hovered = buttons.ClientRectangle.Contains(pointer);
        DrawArrow(false, buttons.Height * .25f, hovered && pointer.Y < buttons.Height / 2);
        DrawArrow(true, buttons.Height * .75f, hovered && pointer.Y >= buttons.Height / 2);

        void DrawArrow(bool down, float y, bool hot)
        {
            float x = buttons.Width / 2f;
            float tip = down ? 1.5f * s : -1.5f * s;
            using var pen = new Pen(hot && buttons.Enabled ? UiTheme.TealHover : UiTheme.Muted, 1.2f * s)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            g.DrawLines(pen, [new PointF(x - 2.5f * s, y - tip / 2), new(x, y + tip), new(x + 2.5f * s, y - tip / 2)]);
        }
    }
}

internal enum LabelTone { Normal, Muted, Strong }

internal sealed class ThemeLabel : Label
{
    private readonly LabelTone tone;

    public ThemeLabel(LabelTone tone)
    {
        this.tone = tone;
        ApplyTheme();
    }

    public void ApplyTheme() => ForeColor = tone switch
    {
        LabelTone.Muted => UiTheme.Muted,
        LabelTone.Strong => UiTheme.Ink,
        _ => UiTheme.Secondary
    };
}

internal sealed class StatusPill : Label
{
    private bool connected;

    public StatusPill()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public void SetStatus(bool isConnected, string text)
    {
        if (connected == isConnected && Text == text) return;
        connected = isConnected;
        Text = text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float y = Height / 2f;
        UiTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), 16 * s, UiTheme.Track, UiTheme.Line);
        if (connected)
        {
            using var halo = new SolidBrush(Color.FromArgb(55, UiTheme.Teal));
            g.FillEllipse(halo, 8 * s, y - 6 * s, 12 * s, 12 * s);
        }
        using var dot = new SolidBrush(connected ? UiTheme.Teal : UiTheme.Muted);
        g.FillEllipse(dot, 10.5f * s, y - 3.5f * s, 7 * s, 7 * s);
        var text = Rectangle.FromLTRB((int)(26 * s), 0, Width - (int)(7 * s), Height);
        TextRenderer.DrawText(g, Text, Font, text, UiTheme.Secondary, TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        UiTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), Height / 2f, BackColor);
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
        using var track = new Pen(UiTheme.Rail, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, 8 * s, y, Width - 8 * s, y);
        track.Color = UiTheme.Teal;
        g.DrawLine(track, 8 * s, y, end, y);
        using var brush = new SolidBrush(UiTheme.Teal);
        using var ring = new Pen(UiTheme.Surface, 1.4f * s);
        using var halo = new SolidBrush(Color.FromArgb(50, UiTheme.Teal));
        g.FillEllipse(halo, end - 9 * s, y - 9 * s, 18 * s, 18 * s);
        var thumb = new RectangleF(end - 6 * s, y - 6 * s, 12 * s, 12 * s);
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
        const int segments = 20;
        float slot = Width / (float)segments;
        int filled = (level * segments + 99) / 100;
        for (int i = 0; i < segments; i++)
        {
            float x = i * slot + 2 * s;
            if (i < filled)
                UiTheme.Box(g, new(x - s, 2 * s, slot - 2 * s, Height - 4 * s), 4 * s, Color.FromArgb(60, UiTheme.Teal));
            UiTheme.Box(g, new(x, 4 * s, slot - 4 * s, Height - 8 * s), 3 * s, i < filled ? UiTheme.Teal : UiTheme.Track);
        }
    }
}
