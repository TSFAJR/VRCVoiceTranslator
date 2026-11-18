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
        IntegralHeight = false;
        AllowDrop = true;
        TabStop = true;
        AccessibleName = "译文顺序";
        AccessibleDescription = "拖动左侧手柄调整顺序，或使用 Alt 加上、下方向键。按 Delete 移除。";
        BackColor = SettingsTheme.Surface;
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
    private void UpdateHeight() => ItemHeight = (int)Math.Round(41 * DeviceDpi / 96f);

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using var background = new SolidBrush(BackColor);
        g.FillRectangle(background, e.Bounds);
        Color fill = e.Index == dropIndex || e.Index == SelectedIndex && Focused ? SettingsTheme.TealTint : Color.FromArgb(247, 250, 251);
        SettingsTheme.Box(g, new(e.Bounds.Left + .5f, e.Bounds.Top + .5f, e.Bounds.Width - 1, e.Bounds.Height - 8 * s), 5 * s, fill, SettingsTheme.InputLine);
        var choice = (LanguageChoice)Items[e.Index];
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var name = new Rectangle(e.Bounds.X + (int)(49 * s), e.Bounds.Y, Math.Max(1, e.Bounds.Width - (int)(157 * s)), e.Bounds.Height - (int)(8 * s));
        var code = new Rectangle(e.Bounds.Right - (int)(115 * s), e.Bounds.Y, (int)(73 * s), name.Height);
        TextRenderer.DrawText(g, choice.Name, Font, name, SettingsTheme.Ink, flags);
        TextRenderer.DrawText(g, choice.Code, Font, code, SettingsTheme.Muted, flags | TextFormatFlags.Right);

        float center = e.Bounds.Top + (e.Bounds.Height - 8 * s) / 2f;
        using var ink = new SolidBrush(SettingsTheme.Ink);
        for (int y = -1; y <= 1; y++)
        for (int x = 0; x < 2; x++)
            g.FillEllipse(ink, e.Bounds.X + (14 + x * 7) * s, center + (y * 6 - 1.5f) * s, 3 * s, 3 * s);
        using var stroke = new Pen(SettingsTheme.Ink, 1.35f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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
            Font = font, Text = "添加语言", ClientSize = new Size(328, 120), BackColor = SettingsTheme.Canvas,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi
        };
        var select = new ComboBox { Location = new(16, 40), Width = 296, MaxLength = 8, AccessibleName = "DeepL 语言代码" };
        select.Items.AddRange(Languages);
        var caption = new Label { Text = "选择语言，或输入 DeepL 语言代码", AutoSize = true, Location = new(16, 13) };
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
    public HeadsetPreview() { DoubleBuffered = true; AccessibleName = "头显字幕位置预览"; }
    public void SetOptions(SubtitleSettings value) { options = value; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        SettingsTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), 9 * s, Color.FromArgb(52, 69, 89));
        float centerX = Width / 2f;
        float centerY = Height / 2f;
        using var line = new Pen(Color.FromArgb(212, 231, 239), 1.1f * s);
        g.DrawLine(line, centerX - 10 * s, centerY, centerX + 10 * s, centerY);
        g.DrawLine(line, centerX, centerY - 10 * s, centerX, centerY + 10 * s);
        if (!options.Enabled) return;

        string caption = SubtitlePresentation.Text(DesktopRuntime.PreviewExample, options.ShowOriginal);
        int maxWidth = Math.Max(1, (int)(Width - 30 * s));
        using var captionFont = new Font(Font.FontFamily, Math.Clamp(14 * s * options.FontPercent / 100, 6, 44 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        var measure = g.MeasureString(caption, captionFont, maxWidth, format);
        float height = Math.Min(Height - 14 * s, measure.Height + 11 * s);
        float width = Math.Min(maxWidth + 12 * s, measure.Width + 20 * s);
        float y = Math.Clamp(centerY + (float)options.DownAngleDegrees / 43 * centerY - height / 2, 7 * s, Math.Max(7 * s, Height - height - 7 * s));
        float x = centerX - width / 2;
        int alpha = options.OpacityPercent * 255 / 100;
        SettingsTheme.Box(g, new(x, y, width, height), 8 * s, Color.FromArgb(alpha, 27, 39, 53));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var ink = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        g.DrawString(caption, captionFont, ink, new RectangleF(x + 5 * s, y + 5 * s, width - 10 * s, height - 5 * s), format);
    }
}
