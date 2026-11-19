using System.Drawing.Drawing2D;
using System.Drawing.Text;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed record LanguageChoice(string Code, string Name)
{
    public override string ToString() => Name == Code ? Code : Name + "  " + Code;
}

internal sealed class LanguageOrder : ListBox
{
    public const int LogicalItemHeight = 32;
    private static readonly LanguageChoice[] Languages =
    [
        new("EN-US", "英语（美国）"), new("EN-GB", "英语（英国）"), new("JA", "日语"), new("KO", "韩语"),
        new("ZH-HANS", "简体中文"), new("ZH-HANT", "繁体中文"), new("DE", "德语"), new("FR", "法语"),
        new("ES", "西班牙语"), new("PT-BR", "葡萄牙语（巴西）"), new("IT", "意大利语"), new("RU", "俄语")
    ];
    private Point mouseOrigin;
    private int draggedIndex = -1;
    private int dropIndex = -1;

    public LanguageOrder()
    {
        BorderStyle = BorderStyle.None;
        DrawMode = DrawMode.OwnerDrawFixed;
        // The row's rounded border and trailing actions move when its width changes.
        ResizeRedraw = true;
        DoubleBuffered = true;
        IntegralHeight = false;
        AllowDrop = true;
        TabStop = true;
        AccessibleName = "译文顺序";
        AccessibleDescription = "拖动左侧手柄调整顺序，或使用 Alt 加上、下方向键。按 Delete 移除。";
        BackColor = UiTheme.Surface;
    }

    public event EventHandler? LanguagesChanged;
    public IEnumerable<string> Codes => Items.Cast<LanguageChoice>().Select(choice => choice.Code);

    public void AddLanguage(string code, bool select = true)
    {
        var choice = Languages.FirstOrDefault(item => item.Code == code) ?? new(code, code);
        int index = Items.IndexOf(choice);
        if (index == -1)
        {
            index = Items.Add(choice);
            LanguagesChanged?.Invoke(this, EventArgs.Empty);
        }
        if (select) SelectedIndex = index;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateHeight(); }
    private void UpdateHeight() => ItemHeight = (int)Math.Round(LogicalItemHeight * DeviceDpi / 96f);

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using var background = new SolidBrush(BackColor);
        g.FillRectangle(background, e.Bounds);
        Color fill = e.Index == dropIndex || e.Index == SelectedIndex && Focused ? UiTheme.TealTint : UiTheme.Input;
        UiTheme.Box(g, new(e.Bounds.Left + .5f, e.Bounds.Top + .5f, e.Bounds.Width - 1, e.Bounds.Height - 8 * s), 5 * s, fill, UiTheme.Line);
        var choice = (LanguageChoice)Items[e.Index];
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var name = new Rectangle(e.Bounds.X + (int)(49 * s), e.Bounds.Y, Math.Max(1, e.Bounds.Width - (int)(157 * s)), e.Bounds.Height - (int)(8 * s));
        var code = new Rectangle(e.Bounds.Right - (int)(115 * s), e.Bounds.Y, (int)(73 * s), name.Height);
        TextRenderer.DrawText(g, choice.Name, Font, name, UiTheme.Ink, flags);
        TextRenderer.DrawText(g, choice.Code, Font, code, UiTheme.Muted, flags | TextFormatFlags.Right);

        float center = e.Bounds.Top + (e.Bounds.Height - 8 * s) / 2f;
        using var ink = new SolidBrush(UiTheme.Ink);
        for (int y = -1; y <= 1; y++)
        for (int x = 0; x < 2; x++)
            g.FillEllipse(ink, e.Bounds.X + (14 + x * 7) * s, center + (y * 6 - 1.5f) * s, 3 * s, 3 * s);
        using var stroke = new Pen(UiTheme.Ink, 1.35f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cross = e.Bounds.Right - 22 * s;
        float half = 5 * s;
        g.DrawLine(stroke, cross - half, center - half, cross + half, center + half);
        g.DrawLine(stroke, cross + half, center - half, cross - half, center + half);
        if (Focused && ShowFocusCues && e.Index == SelectedIndex) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(e.Bounds, -3, -4));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int index = IndexFromPoint(e.Location);
        draggedIndex = -1;
        if (index < 0) return;
        float s = DeviceDpi / 96f;
        var bounds = GetItemRectangle(index);
        if (e.X >= bounds.Right - 40 * s) { RemoveAt(index); return; }
        if (e.X >= bounds.Left + 42 * s) return;
        draggedIndex = index;
        mouseOrigin = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = IndexFromPoint(e.Location);
        float s = DeviceDpi / 96f;
        var bounds = hover >= 0 ? GetItemRectangle(hover) : Rectangle.Empty;
        Cursor = hover < 0 ? Cursors.Default : e.X < bounds.Left + 42 * s ? Cursors.SizeAll :
            e.X >= bounds.Right - 40 * s ? Cursors.Hand : Cursors.Default;
        if (e.Button != MouseButtons.Left || draggedIndex < 0) return;
        var threshold = SystemInformation.DragSize;
        if (Math.Abs(e.X - mouseOrigin.X) < threshold.Width / 2 && Math.Abs(e.Y - mouseOrigin.Y) < threshold.Height / 2) return;
        DoDragDrop("VRCVoiceTranslator:Languages", DragDropEffects.Move);
        draggedIndex = dropIndex = -1;
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);
    protected override void OnDragOver(DragEventArgs e)
    {
        if (draggedIndex < 0) return;
        var point = PointToClient(new(e.X, e.Y));
        if (point.Y < ItemHeight / 3 && TopIndex > 0) TopIndex--;
        if (point.Y > Height - ItemHeight / 3 && TopIndex + 2 < Items.Count) TopIndex++;
        int hit = IndexFromPoint(point);
        e.Effect = hit < 0 ? DragDropEffects.None : DragDropEffects.Move;
        if (dropIndex == hit) return;
        dropIndex = hit;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (draggedIndex >= 0 && dropIndex >= 0) Reorder(draggedIndex, dropIndex);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (SelectedIndex < 0) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Delete: RemoveAt(SelectedIndex); return true;
            case Keys.Alt | Keys.Up: Reorder(SelectedIndex, SelectedIndex - 1); return true;
            case Keys.Alt | Keys.Down: Reorder(SelectedIndex, SelectedIndex + 1); return true;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    private void Reorder(int from, int to)
    {
        if (to < 0 || to >= Items.Count || from == to) return;
        object selected = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, selected);
        SelectedIndex = to;
        Invalidate();
    }

    private void RemoveAt(int index)
    {
        Items.RemoveAt(index);
        SelectedIndex = Math.Min(index, Items.Count - 1);
        LanguagesChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string? ChooseLanguage(IWin32Window parent, Font font)
    {
        using var dialog = new Form
        {
            Font = font, Text = "添加语言", ClientSize = new Size(328, 120), BackColor = UiTheme.Canvas, ForeColor = UiTheme.Ink,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi
        };
        dialog.HandleCreated += (_, _) => UiTheme.ApplyCaption(dialog.Handle);
        var select = new ComboBox
        {
            Location = new(16, 40), Width = 296, MaxLength = 8, AccessibleName = "目标语言代码",
            BackColor = UiTheme.Input, ForeColor = UiTheme.Ink
        };
        select.Items.AddRange(Languages);
        var caption = new Label { Text = "选择语言，或输入目标语言代码", AutoSize = true, Location = new(16, 13) };
        var add = new SettingsButton("添加", SettingsButtonStyle.Primary) { Bounds = new(235, 78, 77, 30) };
        var cancel = new SettingsButton("取消") { Bounds = new(149, 78, 77, 30), DialogResult = DialogResult.Cancel };
        string? code = null;
        add.Click += (_, _) =>
        {
            try
            {
                code = select.SelectedItem is LanguageChoice choice ? choice.Code : Settings.NormalizeTarget(select.Text);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (FormatException error) { MessageBox.Show(dialog, error.Message, dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        dialog.Controls.AddRange([caption, select, cancel, add]);
        dialog.AcceptButton = add;
        dialog.CancelButton = cancel;
        select.Select();
        return dialog.ShowDialog(parent) == DialogResult.OK ? code : null;
    }
}

internal sealed class HeadsetPreview : Control
{
    private SubtitleSettings options = new();
    public HeadsetPreview()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleName = "头显字幕位置预览";
    }
    public void SetOptions(SubtitleSettings value) { options = value; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Width < 16 || Height < 16) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float centerX = Width / 2f;
        float centerY = Height / 2f;
        UiTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), 7 * s, Color.FromArgb(5, 8, 12), UiTheme.Line);

        float inset = 8 * s;
        float curve = Height * .08f;
        using var visor = new GraphicsPath();
        visor.AddBezier(inset * 2, inset, Width * .35f, inset + curve, Width * .65f, inset + curve, Width - inset * 2, inset);
        visor.AddBezier(Width - inset * 2, inset, Width + inset, centerY * .6f, Width + inset, centerY * 1.4f, Width - inset * 2, Height - inset);
        visor.AddBezier(Width - inset * 2, Height - inset, Width * .65f, Height - inset - curve, Width * .35f, Height - inset - curve, inset * 2, Height - inset);
        visor.AddBezier(inset * 2, Height - inset, -inset, centerY * 1.4f, -inset, centerY * .6f, inset * 2, inset);
        visor.CloseFigure();
        using var backlight = new PathGradientBrush(visor)
        {
            CenterPoint = new PointF(centerX, centerY),
            CenterColor = Color.FromArgb(20, 37, 49),
            SurroundColors = [Color.FromArgb(7, 14, 21)]
        };
        g.FillPath(backlight, visor);
        using var rim = new Pen(Color.FromArgb(47, 66, 79), s);
        g.DrawPath(rim, visor);
        var viewport = g.Save();
        g.SetClip(visor);
        using var grid = new Pen(Color.FromArgb(20, 134, 188, 203), s * .75f);
        for (int col = -8; col <= 8; col++)
        {
            float offset = col * Width / 16f;
            g.DrawBezier(grid, centerX + offset * .81f, 0, centerX + offset * 1.13f, Height / 3f,
                centerX + offset * 1.13f, Height * 2 / 3f, centerX + offset * .81f, Height);
        }
        for (int row = -5; row <= 5; row++)
        {
            float offset = row * Height / 10f;
            g.DrawBezier(grid, 0, centerY + offset * 1.16f, Width / 3f, centerY + offset * .88f,
                Width * 2 / 3f, centerY + offset * .88f, Width, centerY + offset * 1.16f);
        }
        using var star = new SolidBrush(Color.FromArgb(145, 170, 206, 224));
        using var halo = new SolidBrush(Color.FromArgb(52, 111, 180, 191));
        var pattern = new Random(27);
        for (int i = 0; i < 30; i++)
        {
            float x = Width * (.07f + pattern.NextSingle() * .86f);
            float y = Height * (.12f + pattern.NextSingle() * .76f);
            float radius = (i % 7 == 0 ? 1.5f : .65f) * s;
            if (i % 7 == 0) g.FillEllipse(halo, x - 3 * s, y - 3 * s, 6 * s, 6 * s);
            g.FillEllipse(star, x - radius, y - radius, radius * 2, radius * 2);
        }
        g.FillEllipse(halo, centerX - 11 * s, centerY - 11 * s, 22 * s, 22 * s);
        using var reticle = new Pen(Color.FromArgb(169, 210, 221), s);
        g.DrawLine(reticle, centerX - 8 * s, centerY, centerX + 8 * s, centerY);
        g.DrawLine(reticle, centerX, centerY - 8 * s, centerX, centerY + 8 * s);
        if (options.Enabled) DrawCaption(g, s, centerX, centerY);
        g.Restore(viewport);
    }

    private void DrawCaption(Graphics g, float s, float centerX, float centerY)
    {
        string caption = SubtitlePresentation.Text(DesktopRuntime.PreviewExample, options.ShowOriginal);
        int maxWidth = Math.Max(1, (int)(Width - 50 * s));
        using var captionFont = new Font(Font.FontFamily, Math.Clamp(14 * s * options.FontPercent / 100, 6, 44 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        var measure = g.MeasureString(caption, captionFont, maxWidth, format);
        float margin = 21 * s;
        float height = Math.Min(Math.Max(1, Height - 2 * margin), measure.Height + 14 * s);
        float width = Math.Min(maxWidth + 16 * s, measure.Width + 24 * s);
        float y = Math.Clamp(centerY + (float)options.DownAngleDegrees / 43 * centerY - height / 2, margin, Math.Max(margin, Height - height - margin));
        float x = centerX - width / 2;
        int alpha = options.OpacityPercent * 255 / 100;
        UiTheme.Box(g, new(x, y, width, height), 7 * s, Color.FromArgb(alpha * 9 / 10, 24, 40, 53),
            Color.FromArgb(alpha / 2, 111, 150, 169));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var ink = new SolidBrush(Color.FromArgb(alpha, 241, 245, 249));
        g.DrawString(caption, captionFont, ink, new RectangleF(x + 7 * s, y + 6 * s, width - 14 * s, height - 6 * s), format);
    }
}
