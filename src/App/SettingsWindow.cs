using System.Diagnostics;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SettingsWindow : Form
{
    private readonly SettingsStore store;
    private readonly DesktopRuntime runtime;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 125 };
    private readonly ToolTip help = new();
    private readonly StatusIndicator steamDot = new();
    private readonly Label steamStatus = Caption("SteamVR 未连接");
    private readonly Label phase = Caption("待机");
    private readonly SettingsBadge binding = new() { Text = "未连接" };
    private readonly Label saved = Caption("");
    private readonly SettingsButton errorButton = MakeButton("查看错误", SettingsButtonStyle.Outline);
    private readonly ComboBox microphones = DropDown("麦克风");
    private readonly SegmentedMeter meter = new();
    private readonly SettingsButton micTest = MakeButton("测试电平");
    private readonly TextBox hotkey = Entry("键盘热键", readOnly: true);
    private readonly ComboBox vrMode = DropDown("SteamVR 触发", "原生组合键", "双动作同时按住");
    private readonly CheckBox overrideInput = new() { Text = "使用 SteamVR 实验性输入覆盖", ForeColor = SettingsTheme.Ink };
    private readonly NumericUpDown maxRecording = Number("录音上限", 1, 60, unit: " 秒");
    private readonly TextBox endpoint = Entry("百炼地址");
    private readonly TextBox bailianKey = Entry("百炼 API Key", password: true);
    private readonly TextBox deepLKey = Entry("DeepL API Key", password: true);
    private readonly TextFrame bailianPassword;
    private readonly TextFrame deepLPassword;
    private readonly ComboBox source = DropDown("语音语言", "中文", "英语", "日语", "韩语", "自动");
    private readonly LanguageOrder targets = new();
    private readonly SettingsSwitch chatEnabled = new("发送 Chatbox 消息");
    private readonly NumericUpDown oscPort = Number("OSC 端口", 1, 65535);
    private readonly Label oscState = Caption("");
    private readonly TextBox manualChatbox = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "手动输入 Chatbox 文字"
    };
    private readonly SettingsButton sendManual = MakeButton("翻译并发送", SettingsButtonStyle.Primary);
    private readonly TextBox oscPreview = new()
    {
        ReadOnly = true, TabStop = false, Multiline = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "最近输出预览"
    };
    private readonly SettingsSwitch subtitleEnabled = new("显示头显字幕");
    private readonly SettingsSwitch original = new("显示原文");
    private readonly NumericUpDown duration = Number("显示时长", 0.5m, 30, 1, 0.5m, " 秒");
    private readonly NumericUpDown font = Number("字号", 50, 200, increment: 5, unit: "%");
    private readonly NumericUpDown opacity = Number("不透明度", 10, 100, increment: 5, unit: "%");
    private readonly NumericUpDown angle = Number("视线下方", -30, 60, 1, unit: "°");
    private readonly NumericUpDown distance = Number("距离", 0.5m, 5, 2, 0.1m, " 米");
    private readonly SettingsButton previewButton = MakeButton("在 VR 中预览");
    private readonly HeadsetPreview headsetPreview = new();
    private readonly SettingsButton[] tabs;
    private readonly SettingsPage[] pages;
    private int activePage = -1;
    private int key;
    private bool assigningKey;
    private bool postingManual;
    private bool previewing;
    private bool advancedExpanded;
    private int heightBeforeAdvanced;
    private bool allowClose;
    private string lastVrInfo = "";
    private DateTime savedUntil;

    public SettingsWindow(SettingsStore store, DesktopRuntime runtime, Icon icon)
    {
        this.store = store;
        this.runtime = runtime;
        Text = "VRChat 语音翻译";
        Icon = icon;
        Font = new Font("Microsoft YaHei UI", 12F);
        BackColor = SettingsTheme.Canvas;
        ForeColor = SettingsTheme.Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(714, 500);
        MinimumSize = new Size(685, 527);

        bailianPassword = new(bailianKey, password: true);
        deepLPassword = new(deepLKey, password: true);
        pages = [MicrophonePage(), ServicePage(), ChatboxPage(), SubtitlePage()];
        tabs = new SettingsButton[pages.Length];

        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 41));
        layout.RowStyles.Add(new(SizeType.Absolute, 46));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 59));
        layout.Controls.Add(CreateHeader(), 0, 0);
        layout.Controls.Add(CreateTabs(), 0, 1);
        var card = new SettingsCard { Dock = DockStyle.Fill, Margin = new Padding(15, 0, 15, 0), Padding = new Padding(2), BackColor = SettingsTheme.Canvas };
        foreach (var page in pages)
        {
            page.Visible = false;
            page.Dock = DockStyle.Fill;
            card.Controls.Add(page);
        }
        layout.Controls.Add(card, 0, 2);
        layout.Controls.Add(CreateFooter(), 0, 3);
        Controls.Add(layout);

        LoadValues(runtime.Current);
        SelectPage(0);
        refresh.Tick += (_, _) => ReadStatus();
        VisibleChanged += (_, _) => { refresh.Enabled = Visible; if (Visible) ReadStatus(); };
        FormClosing += ClosingSettings;
        Activated += (_, _) => runtime.SetKeyboardPaused(assigningKey || manualChatbox.Focused);
        Deactivate += (_, _) => { StopAssigning(); runtime.SetKeyboardPaused(false); MaskPasswords(); };
    }

    private SettingsPage CreateHeader()
    {
        var header = new SettingsPage { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SettingsTheme.Canvas };
        header.Place(steamDot, 24, 2, 18, 35);
        header.Place(steamStatus, 43, 2, 130, 35);
        header.Place(new Panel { BackColor = SettingsTheme.InputLine }, 178, 13, 1, 14);
        header.Place(phase, 201, 2, 140, 35);
        errorButton.Visible = false;
        errorButton.Click += (_, _) => { if (runtime.Status.Error.Length > 0) ShowError(runtime.Status.Error); };
        header.Place(errorButton, 570, 5, 91, 30);
        header.Place(new Panel { BackColor = SettingsTheme.Line }, 0, 40, 682, 1);
        return header;
    }

    private SettingsPage CreateTabs()
    {
        var bar = new SettingsPage { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SettingsTheme.Canvas };
        string[] labels = ["麦克风与触发", "识别与翻译", "Chatbox", "VR 字幕"];
        int[] widths = [139, 125, 104, 108];
        int x = 19;
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            var button = MakeButton(labels[i], SettingsButtonStyle.Tab, action: () => SelectPage(index));
            button.BackColor = bar.BackColor;
            tabs[i] = bar.Place(button, x, 0, widths[i], 45);
            x += widths[i];
        }
        return bar;
    }

    private SettingsPage CreateFooter()
    {
        var footer = new SettingsPage { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SettingsTheme.Canvas };
        var folder = MakeButton("打开配置目录", SettingsButtonStyle.Plain, SettingsGlyph.Folder, OpenConfigFolder);
        var save = MakeButton("保存设置", SettingsButtonStyle.Primary, action: Save);
        folder.BackColor = save.BackColor = footer.BackColor;
        saved.ForeColor = SettingsTheme.DarkTeal;
        saved.TextAlign = ContentAlignment.MiddleRight;
        footer.Place(folder, 20, 12, 148, 34);
        footer.Place(saved, 409, 12, 134, 34);
        footer.Place(save, 550, 12, 114, 35);
        return footer;
    }

    private SettingsPage MicrophonePage()
    {
        var page = new SettingsPage();
        page.Place(Caption("麦克风"), 25, 20, 130, 34);
        page.Place(microphones, 162, 22, 330, 34);
        var reload = MakeButton("", SettingsButtonStyle.Neutral, SettingsGlyph.Refresh, () => ReloadDevices(SelectedMicrophone));
        reload.AccessibleName = "刷新麦克风";
        help.SetToolTip(reload, reload.AccessibleName);
        page.Place(reload, 503, 20, 42, 34);
        micTest.Click += (_, _) =>
        {
            if (runtime.MicTestActive) runtime.StopMicTest();
            else runtime.TestMicrophone(SelectedMicrophone);
        };
        page.Place(micTest, 560, 20, 101, 34);
        page.Place(Caption("输入电平"), 25, 68, 130, 31);
        page.Place(meter, 162, 72, 307, 28);
        Divide(page, 120);

        hotkey.TabStop = false;
        hotkey.PreviewKeyDown += (_, e) => { if (assigningKey) e.IsInputKey = true; };
        hotkey.KeyDown += (_, e) =>
        {
            if (!assigningKey) return;
            if (e.KeyCode != Keys.Escape && (int)e.KeyCode is >= 0x08 and <= 0xFE) key = (int)e.KeyCode;
            StopAssigning();
            e.SuppressKeyPress = true;
        };
        hotkey.Leave += (_, _) => StopAssigning();
        page.Place(Caption("键盘热键"), 25, 140, 130, 34);
        page.Place(new TextFrame(hotkey), 162, 140, 144, 34);
        page.Place(MakeButton("更改", action: StartAssigning), 319, 140, 72, 34);
        page.Place(Caption("SteamVR 触发"), 25, 190, 130, 33);
        page.Place(vrMode, 162, 192, 229, 33);
        page.Place(binding, 404, 194, 81, 27);
        page.Place(MakeButton("打开 SteamVR 绑定", SettingsButtonStyle.Outline, action: runtime.OpenBindings), 496, 190, 165, 34);
        page.Place(Caption("录音上限"), 25, 237, 130, 34);
        page.Place(new NumberFrame(maxRecording), 162, 237, 136, 34);
        Divide(page, 287);

        var advanced = MakeButton("高级设置", SettingsButtonStyle.Plain, SettingsGlyph.Chevron);
        page.Place(advanced, 23, 296, 118, 35);
        page.Place(overrideInput, 30, 332, 350, 28).Visible = false;
        var note = Caption("仅在按键冲突时启用。需同时开启 SteamVR 对应开发者设置。", SettingsTheme.Muted);
        page.Place(note, 30, 359, 620, 30).Visible = false;
        advanced.Click += (_, _) =>
        {
            advancedExpanded = !advancedExpanded;
            advanced.SetGlyph(advancedExpanded ? SettingsGlyph.Down : SettingsGlyph.Chevron);
            overrideInput.Visible = note.Visible = advancedExpanded;
            int expandedHeight = ScalePixels(579);
            if (advancedExpanded) heightBeforeAdvanced = Height;
            MinimumSize = new Size(MinimumSize.Width, ScalePixels(advancedExpanded ? 579 : 527));
            if (!advancedExpanded && Height == expandedHeight) Height = Math.Max(MinimumSize.Height, heightBeforeAdvanced);
        };
        return page;
    }

    private SettingsPage ServicePage()
    {
        var page = new SettingsPage();
        endpoint.PlaceholderText = "wss://…/api-ws/v1/inference";
        page.Place(Caption("百炼地址"), 25, 15, 130, 34);
        page.Place(new TextFrame(endpoint), 163, 15, 495, 33);
        page.Place(Caption("百炼 API Key"), 25, 55, 130, 34);
        page.Place(bailianPassword, 163, 55, 495, 33);
        page.Place(Caption("DeepL API Key"), 25, 95, 130, 34);
        page.Place(deepLPassword, 163, 95, 495, 33);
        page.Place(Caption("语音语言"), 25, 135, 130, 34);
        page.Place(source, 163, 137, 213, 34);
        Divide(page, 184);

        page.Place(Caption("译文顺序"), 25, 198, 130, 32);
        page.Place(targets, 163, 198, 495, 82);
        var add = MakeButton("添加语言", SettingsButtonStyle.Outline, SettingsGlyph.Plus, () =>
        {
            if (LanguageOrder.ChooseLanguage(this, Font) is { } code) targets.AddLanguage(code);
        });
        page.Place(add, 163, 287, 131, 34);
        void ResizeLanguages()
        {
            int rows = Math.Min(2, targets.Items.Count);
            targets.Visible = rows > 0;
            page.MoveControl(targets, 198, rows * 41);
            page.MoveControl(add, 198 + rows * 41 + (rows == 0 ? 0 : 7));
        }
        targets.LanguagesChanged += (_, _) => ResizeLanguages();
        ResizeLanguages();
        return page;
    }

    private SettingsPage ChatboxPage()
    {
        var page = new SettingsPage();
        page.Place(Caption("发送 Chatbox 消息"), 25, 18, 166, 34);
        page.Place(chatEnabled, 200, 18, 52, 34);
        page.Place(Caption("OSC 端口"), 25, 69, 130, 34);
        page.Place(new NumberFrame(oscPort), 200, 69, 135, 34);
        page.Place(MakeButton("发送测试消息", SettingsButtonStyle.Outline, action: () => runtime.TestChatbox((int)oscPort.Value)), 357, 69, 135, 34);
        page.Place(oscState, 506, 69, 155, 34);
        oscState.ForeColor = SettingsTheme.Muted;
        page.Place(Caption("手动输入"), 25, 111, 160, 28);
        page.Place(new TextFrame(manualChatbox), 25, 142, 501, 77);
        page.Place(sendManual, 543, 142, 115, 34);
        var shortcut = Caption("Ctrl + Enter", SettingsTheme.Muted);
        shortcut.TextAlign = ContentAlignment.MiddleCenter;
        page.Place(shortcut, 543, 179, 115, 27);
        sendManual.Click += SendManualChatbox;
        manualChatbox.TextChanged += (_, _) => UpdateManualSendButton();
        manualChatbox.Enter += (_, _) => runtime.SetKeyboardPaused(true);
        manualChatbox.Leave += (_, _) => runtime.SetKeyboardPaused(assigningKey);
        chatEnabled.CheckedChanged += (_, _) => UpdateManualSendButton();
        manualChatbox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.Control) return;
            e.SuppressKeyPress = true;
            sendManual.PerformClick();
        };
        UpdateManualSendButton();

        page.Place(Caption("最近输出预览"), 25, 225, 165, 30);
        page.Place(new TextFrame(oscPreview, preview: true), 25, 258, 633, 80);
        return page;
    }

    private async void SendManualChatbox(object? sender, EventArgs e)
    {
        if (!sendManual.Enabled) return;
        string draft = manualChatbox.Text;
        if (sendManual.Focused) manualChatbox.Focus();
        postingManual = true;
        UpdateManualSendButton();
        try
        {
            if (await runtime.SendManualChatboxAsync(draft, (int)oscPort.Value) && !IsDisposed && manualChatbox.Text == draft)
                manualChatbox.Clear();
        }
        catch (OperationCanceledException) { /* Application is exiting. */ }
        finally
        {
            postingManual = false;
            if (!IsDisposed) UpdateManualSendButton();
        }
    }

    private void UpdateManualSendButton()
    {
        sendManual.Text = postingManual ? "翻译中…" : "翻译并发送";
        sendManual.Enabled = chatEnabled.Checked && !postingManual && !string.IsNullOrWhiteSpace(manualChatbox.Text);
    }

    private SettingsPage SubtitlePage()
    {
        var page = new SettingsPage();
        page.Place(Caption("显示头显字幕"), 20, 17, 134, 31);
        page.Place(subtitleEnabled, 155, 17, 52, 31);
        page.Place(Caption("显示原文"), 20, 52, 134, 31);
        page.Place(original, 155, 52, 52, 31);
        SubtitleRow(page, "显示时长", duration, 92, fineAtStart: true);
        SubtitleRow(page, "字号", font, 131);
        SubtitleRow(page, "不透明度", opacity, 170);
        SubtitleRow(page, "视线下方", angle, 209);
        SubtitleRow(page, "距离", distance, 248);
        page.Place(headsetPreview, 373, 34, 294, 198);
        page.Place(previewButton, 420, 250, 198, 34);
        previewButton.Click += (_, _) =>
        {
            previewing = !previewing;
            previewButton.Text = previewing ? "退出 VR 预览" : "在 VR 中预览";
            UpdatePreview();
        };
        subtitleEnabled.CheckedChanged += (_, _) => UpdatePreview();
        original.CheckedChanged += (_, _) => UpdatePreview();
        return page;
    }

    private void SubtitleRow(SettingsPage page, string label, NumericUpDown number, int y, bool fineAtStart = false)
    {
        page.Place(Caption(label), 20, y, 82, 33);
        page.Place(new SettingsSlider(number, fineAtStart), 101, y, 154, 33);
        page.Place(new NumberFrame(number), 265, y, 89, 32);
        number.ValueChanged += (_, _) => UpdatePreview();
    }

    private void SelectPage(int index)
    {
        if (index == activePage) return;
        MaskPasswords();
        if (activePage >= 0) pages[activePage].Hide();
        activePage = index;
        pages[index].Show();
        pages[index].BringToFront();
        for (int i = 0; i < tabs.Length; i++) tabs[i].SetSelected(i == index);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            int step = (keyData & Keys.Shift) == 0 ? 1 : -1;
            SelectPage((activePage + step + pages.Length) % pages.Length);
            tabs[activePage].Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void LoadValues(Settings settings)
    {
        ReloadDevices(settings.MicrophoneId);
        key = settings.KeyboardVirtualKey;
        hotkey.Text = ((Keys)key).ToString();
        vrMode.SelectedIndex = (int)settings.VrBinding;
        overrideInput.Checked = settings.ExperimentalVrInputOverride;
        maxRecording.Value = settings.MaxRecordingSeconds;
        endpoint.Text = settings.BailianEndpoint;
        bailianKey.Text = settings.BailianApiKey;
        deepLKey.Text = settings.DeepLApiKey;
        source.SelectedIndex = (int)settings.SourceLanguage;
        foreach (var target in settings.TargetLanguages) targets.AddLanguage(target, select: false);
        chatEnabled.Checked = settings.ChatboxEnabled;
        oscPort.Value = settings.OscPort;
        subtitleEnabled.Checked = settings.Subtitles.Enabled;
        original.Checked = settings.Subtitles.ShowOriginal;
        duration.Value = (decimal)settings.Subtitles.DurationSeconds;
        font.Value = settings.Subtitles.FontPercent;
        opacity.Value = settings.Subtitles.OpacityPercent;
        angle.Value = (decimal)settings.Subtitles.DownAngleDegrees;
        distance.Value = (decimal)settings.Subtitles.DistanceMeters;
        UpdatePreview();
    }

    private SubtitleSettings ReadSubtitles() => new()
    {
        Enabled = subtitleEnabled.Checked, ShowOriginal = original.Checked, DurationSeconds = (double)duration.Value,
        FontPercent = (int)font.Value, OpacityPercent = (int)opacity.Value, DownAngleDegrees = (double)angle.Value, DistanceMeters = (double)distance.Value
    };

    private void Save()
    {
        try
        {
            var updated = store.Save(new Settings
            {
                MicrophoneId = SelectedMicrophone, KeyboardVirtualKey = key, VrBinding = (VrBindingMode)vrMode.SelectedIndex,
                ExperimentalVrInputOverride = overrideInput.Checked, MaxRecordingSeconds = (int)maxRecording.Value,
                BailianEndpoint = endpoint.Text, BailianApiKey = bailianKey.Text, DeepLApiKey = deepLKey.Text,
                SourceLanguage = (SourceLanguage)source.SelectedIndex, TargetLanguages = targets.Codes.ToArray(),
                ChatboxEnabled = chatEnabled.Checked, OscPort = (int)oscPort.Value, Subtitles = ReadSubtitles()
            });
            runtime.Apply(updated);
            MaskPasswords();
            savedUntil = DateTime.UtcNow.AddSeconds(3);
            saved.Text = "设置已保存";
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            ShowError(error is FormatException ? error.Message : $"无法写入配置。{Environment.NewLine}{store.Path}{Environment.NewLine}0x{error.HResult:X8}");
        }
    }

    private string? SelectedMicrophone => (microphones.SelectedItem as CaptureDevice)?.Id;

    private void ReloadDevices(string? selected)
    {
        List<CaptureDevice> devices;
        try { devices = Microphone.Devices(); }
        catch (Exception error)
        {
            devices = [new(null, "Windows 默认麦克风")];
            ShowError($"无法枚举录音设备（0x{error.HResult:X8}）。");
        }
        int index = devices.FindIndex(device => device.Id == selected);
        if (index == -1)
        {
            index = devices.Count;
            devices.Add(new(selected, "已保存的麦克风（当前不可用）"));
        }
        microphones.Items.Clear();
        microphones.Items.AddRange(devices.Cast<object>().ToArray());
        microphones.SelectedIndex = index;
        microphones.DropDownWidth = Math.Max(ScalePixels(440), microphones.Width);
    }

    private void StartAssigning()
    {
        assigningKey = true;
        runtime.SetKeyboardPaused(true);
        hotkey.Text = "请按一个键…";
        hotkey.Focus();
    }

    private void StopAssigning()
    {
        if (!assigningKey) return;
        assigningKey = false;
        runtime.SetKeyboardPaused(manualChatbox.Focused);
        hotkey.Text = ((Keys)key).ToString();
    }

    private void MaskPasswords()
    {
        bailianPassword.MaskPassword();
        deepLPassword.MaskPassword();
    }

    private void UpdatePreview()
    {
        var options = ReadSubtitles();
        headsetPreview.SetOptions(options with { Enabled = options.Enabled || previewing });
        runtime.Preview(previewing ? options : null);
    }

    private void ReadStatus()
    {
        var status = runtime.Status;
        steamStatus.Text = status.SteamVrConnected ? "SteamVR 已连接" : "SteamVR 未连接";
        steamDot.SetConnected(status.SteamVrConnected);
        phase.Text = status.Phase switch
        {
            VoicePhase.Recording => "录音中", VoicePhase.Recognizing => "识别中", VoicePhase.Translating => "翻译中", _ => "待机"
        };
        phase.ForeColor = status.Phase == VoicePhase.Idle ? SettingsTheme.Ink : SettingsTheme.DarkTeal;
        var vr = status.Binding;
        binding.Text = !status.SteamVrConnected ? "未连接" : !vr.Bound ? "待绑定" :
            !vr.Trigger.Available ? "不可用" : vr.Trigger.Held ? "按住" : "已绑定";
        binding.ForeColor = !status.SteamVrConnected ? SettingsTheme.Muted : vr.Bound ? SettingsTheme.DarkTeal : Color.FromArgb(148, 86, 13);
        binding.BackColor = !status.SteamVrConnected ? SettingsTheme.Line : vr.Bound ? SettingsTheme.TealTint : Color.FromArgb(252, 235, 204);
        if (lastVrInfo != status.Vr)
        {
            lastVrInfo = status.Vr;
            help.SetToolTip(binding, status.Vr);
            help.SetToolTip(steamStatus, status.Vr);
        }
        errorButton.Visible = status.Error.Length > 0;
        oscState.Text = status.ChatboxState;
        oscPreview.ForeColor = status.ChatboxText.Length == 0 ? SettingsTheme.Muted : SettingsTheme.Ink;
        string output = status.ChatboxText.Length == 0 ? "发送的文字会显示在这里" : status.ChatboxText.Replace("\n", "\r\n");
        if (oscPreview.Text != output) oscPreview.Text = output;
        meter.SetLevel(runtime.Meter);
        micTest.Text = runtime.MicTestActive ? "停止测试" : "测试电平";
        if (DateTime.UtcNow >= savedUntil) saved.Text = "";
    }

    private void OpenConfigFolder()
    {
        try
        {
            string path = Path.GetDirectoryName(store.Path)!;
            Directory.CreateDirectory(path);
            using var opened = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError($"无法打开配置目录（0x{error.HResult:X8}）。"); }
    }

    private void ClosingSettings(object? sender, FormClosingEventArgs e)
    {
        previewing = false;
        previewButton.Text = "在 VR 中预览";
        UpdatePreview();
        runtime.StopMicTest();
        StopAssigning();
        runtime.SetKeyboardPaused(false);
        MaskPasswords();
        if (allowClose || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        Hide();
    }

    public void CloseForExit() { allowClose = true; Close(); }

    private void ShowError(string error) => MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private int ScalePixels(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96f);

    private static void Divide(SettingsPage page, int y) => page.Place(new Panel { BackColor = SettingsTheme.Line }, 18, y, 640, 1);
    private static Label Caption(string text, Color? ink = null) => new()
    {
        Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = ink ?? SettingsTheme.Ink
    };

    private static TextBox Entry(string name, bool readOnly = false, bool password = false) => new()
    {
        AccessibleName = name, ReadOnly = readOnly, UseSystemPasswordChar = password
    };

    private static SettingsButton MakeButton(string text, SettingsButtonStyle style = SettingsButtonStyle.Neutral,
        SettingsGlyph glyph = SettingsGlyph.None, Action? action = null)
    {
        var button = new SettingsButton(text, style, glyph) { BackColor = SettingsTheme.Surface };
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static ComboBox DropDown(string name, params string[] items)
    {
        var combo = new ComboBox
        {
            AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(253, 254, 254), ItemHeight = 27
        };
        combo.Items.AddRange(items);
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool isSelection = (e.State & DrawItemState.ComboBoxEdit) != 0;
            Color fill = !isSelection && (e.State & DrawItemState.Selected) != 0 ? SettingsTheme.TealTint : combo.BackColor;
            using var brush = new SolidBrush(fill);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var area = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), e.Font, area, SettingsTheme.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!isSelection) e.DrawFocusRectangle();
        };
        return combo;
    }

    private static NumericUpDown Number(string name, decimal min, decimal max, int decimals = 0, decimal increment = 1, string unit = "") =>
        new SettingsNumber(unit) { AccessibleName = name + unit, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refresh.Dispose(); help.Dispose(); }
        base.Dispose(disposing);
    }
}
