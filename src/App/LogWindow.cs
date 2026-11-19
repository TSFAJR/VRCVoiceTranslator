namespace VrcTranslator.Desktop;

internal sealed class LogWindow : Form
{
    private readonly ActivityLog log;
    private readonly BufferedPanel body = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };
    private readonly RichTextBox entries = new()
    {
        AccessibleName = "活动日志", ReadOnly = true, DetectUrls = false,
        BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical
    };
    private readonly Label status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };
    private readonly SettingsButton refresh = new("刷新", SettingsButtonStyle.Outline)
    {
        Dock = DockStyle.Right, Width = 88
    };

    public LogWindow(ActivityLog log)
    {
        this.log = log;
        Text = "VRChat 语音翻译 · 日志";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        ClientSize = new Size(750, 462);
        MinimumSize = new Size(470, 280);
        Padding = new Padding(16);
        body.Controls.Add(entries);
        var actions = new BufferedPanel
        {
            Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 12, 0, 0)
        };
        actions.Controls.Add(status);
        actions.Controls.Add(refresh);
        Controls.Add(body);
        Controls.Add(actions);
        refresh.Click += async (_, _) => await ReloadAsync();
        Shown += async (_, _) => await ReloadAsync();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        status.ForeColor = UiTheme.Muted;
        body.BackColor = entries.BackColor = UiTheme.Input;
        entries.ForeColor = UiTheme.Ink;
        if (IsHandleCreated) UiTheme.ApplyCaption(Handle);
        Invalidate(invalidateChildren: true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyCaption(Handle);
    }

    private async Task ReloadAsync()
    {
        if (!refresh.Enabled) return;
        refresh.Enabled = false;
        status.Text = "正在读取日志…";
        try
        {
            string text = (await Task.Run(log.ReadHistory)).ReplaceLineEndings("\n");
            if (IsDisposed) return;
            if (entries.Text != text)
            {
                entries.Text = text;
                entries.SelectionStart = entries.TextLength;
                entries.ScrollToCaret();
            }
            status.Text = text.Length == 0 ? "暂无日志" : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) status.Text = $"无法读取日志（0x{error.HResult:X8}）。";
        }
        finally
        {
            if (!IsDisposed) refresh.Enabled = true;
        }
    }
}
