using System.Diagnostics;
using System.Runtime.InteropServices;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SettingsWindow : Form
{
    private const int IncomingCardTop = 302;
    private static int IncomingCardHeight => IncomingLoopbackCapture.UsesProcessLoopback ? 114 : 153;
    private readonly SettingsStore store;
    private readonly DesktopRuntime runtime;
    private readonly ActivityLog log;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 125 };
    private readonly ToolTip help = new();
    private readonly StatusPill steamStatus = new();
    private readonly Label pageTitle = Caption("", LabelTone.Strong);
    private readonly BufferedPanel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly BufferedPanel content = new() { Dock = DockStyle.Fill };
    private readonly SettingsPage sidebar = new(216) { Dock = DockStyle.Left };
    private readonly SettingsPage sidebarFooter = new(216) { Dock = DockStyle.Bottom };
    private readonly Panel sidebarDivider = new() { BackColor = UiTheme.Line };
    private readonly BufferedPanel heading = new() { Dock = DockStyle.Top };
    private readonly SettingsPage headingActions = new(236) { Dock = DockStyle.Right };
    private readonly SettingsPage footer = new() { Dock = DockStyle.Bottom };
    private readonly Font bodyFont = new(UiTheme.FontFamily, UiTheme.TextSize);
    private readonly Font hintFont = new(UiTheme.FontFamily, UiTheme.HintSize);
    private readonly Font titleFont = new(UiTheme.FontFamily, UiTheme.TitleSize, FontStyle.Bold);
    private readonly Font brandFont = new(UiTheme.FontFamily, 13f, FontStyle.Bold);
    private readonly Label translationNotice = Caption("", LabelTone.Muted);
    private readonly SettingsBadge binding = new() { Text = "未连接" };
    private readonly Label saved = Caption("");
    private readonly SettingsButton logButton = MakeButton("日志");
    private readonly ThemeSegmentedControl appearance = new();
    private LogWindow? logWindow;
    private readonly ComboBox microphones = DropDown("麦克风");
    private readonly SegmentedMeter meter = new();
    private readonly SettingsButton micTest = MakeButton("测试电平");
    private readonly TextBox hotkey = Entry("键盘热键", readOnly: true);
    private readonly ComboBox vrMode = DropDown("SteamVR 触发", "原生组合键", "双动作同时按住");
    private readonly CheckBox overrideInput = new() { Text = "使用 SteamVR 实验性输入覆盖" };
    private readonly NumericUpDown maxRecording = Number("录音上限", 1, 60, unit: " 秒");
    private readonly TextBox workspaceId = Entry("百炼 Workspace ID");
    private readonly ComboBox bailianRegion = DropDown("百炼地域", "华北 2（北京）", "新加坡");
    private readonly TextBox bailianKey = Entry("百炼 API Key", password: true);
    private readonly TextBox deepLKey = Entry("DeepL API Key", password: true);
    private readonly TextFrame bailianPassword;
    private readonly TextFrame deepLPassword;
    private readonly ComboBox translationProvider = DropDown("翻译服务", "DeepL", "qwen-mt-flash", "qwen-mt-lite");
    private readonly ComboBox source = DropDown("语音语言", "中文", "英语", "日语", "韩语", "自动");
    private readonly SettingsButton hotwordButton = MakeButton("热词词库");
    private RecognitionVocabularyDialog? hotwordEditor;
    private IReadOnlyDictionary<string, int> recognitionHotwords = new Dictionary<string, int>();
    private bool recognitionHotwordsEnabled;
    private TerminologySettings terminology = TerminologySettings.Empty;
    private readonly LanguageOrder targets = new();
    private readonly SettingsSwitch incomingToggle = new("来话翻译") { AutoCheck = false };
    private readonly SettingsSwitch incomingNoiseFilter = new("加强来话底噪过滤");
    private readonly NumericUpDown incomingSentenceSilence = Number("来话断句间隔",
        Settings.MinIncomingSentenceSilenceMs, Settings.MaxIncomingSentenceSilenceMs, increment: 50, unit: " 毫秒");
    private readonly Label incomingState = Caption("已关闭", LabelTone.Muted);
    private readonly ComboBox playback = DropDown("来话播放设备");
    private readonly SettingsButton refreshPlayback = MakeButton("", SettingsButtonStyle.Neutral, SettingsGlyph.Refresh);
    private readonly SettingsSwitch chatEnabled = new("发送 Chatbox 消息");
    private readonly NumericUpDown oscPort = Number("OSC 端口", 1, 65535);
    private readonly Label oscState = Caption("", LabelTone.Muted);
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
    private readonly SettingsButton previewButton = MakeButton("在 VR 中预览", SettingsButtonStyle.Outline);
    private readonly HeadsetPreview headsetPreview = new();
    private readonly SettingsSwitch speechEnabled = new("启用自动播报");
    private readonly ComboBox speechMode = DropDown("播报模式", "中文原文", "英文译文", "日文译文");
    private readonly ComboBox voiceChinese = DropDown("中文音色");
    private readonly ComboBox voiceEnglish = DropDown("英文音色");
    private readonly ComboBox voiceJapanese = DropDown("日文音色");
    private IReadOnlyList<SpeechVoiceChoice> voiceLibrary = [];
    private SpeechVoiceManager? voiceManager;
    private readonly NumericUpDown speechVolume = Number("播报音量", 0, 100, unit: "%");
    private readonly ComboBox speechExpression = DropDown("播报语气", "自动匹配关键词", "原声");
    private readonly NumericUpDown speechRate = Number("播报语速", 0.50m, 2m, 2, 0.05m, "×");
    private IReadOnlyList<SpeechKeyword> speechKeywords = SpeechExpression.Defaults;
    private SpeechKeywordDialog? keywordEditor;
    private readonly ComboBox speechOutput = DropDown("播报输出设备");
    private readonly SettingsSwitch speechMonitor = new("监听播报");
    private readonly ComboBox speechMonitorDevice = DropDown("播报监听设备");
    private readonly SettingsButton speechPreview = MakeButton("试听", SettingsButtonStyle.Outline);
    private readonly SettingsButton speechStop = MakeButton("停止播放");
    private readonly Label speechState = Caption("待机", LabelTone.Muted);
    private readonly NavButton[] tabs;
    private readonly SettingsPage[] pages;
    private int activePage = -1;
    private int key;
    private bool assigningKey;
    private bool postingManual;
    private bool previewing;
    private bool advancedExpanded;
    private bool allowClose;
    private WindowLayout? currentLayout;
    private WindowLayout? savedLayout;
    private string lastVrInfo = "";
    private DateTime savedUntil;

    public SettingsWindow(SettingsStore store, DesktopRuntime runtime, ActivityLog log, Icon icon)
    {
        this.store = store;
        this.runtime = runtime;
        this.log = log;
        Text = "VRChat 语音翻译";
        Icon = icon;
        Font = bodyFont;
        DoubleBuffered = true;
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        sidebar.BackColor = UiTheme.Sidebar;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;

        bailianPassword = new(bailianKey, password: true);
        deepLPassword = new(deepLKey, password: true);
        pages = [MicrophonePage(), ServicePage(), ChatboxPage(), SubtitlePage(), SpeechPage()];
        tabs = new NavButton[pages.Length];

        CreateSidebar();
        CreateContent();
        Controls.Add(content);
        Controls.Add(sidebar);
        ApplyShellMetrics();

        LoadValues(runtime.Current);
        speechExpression.SelectedIndexChanged += (_, _) => ApplySpeechStyle();
        speechRate.ValueChanged += (_, _) => ApplySpeechStyle();
        bailianRegion.SelectedIndexChanged += (_, _) => ReloadVoiceChoices();
        appearance.SelectedModeChanged += (_, _) => UpdateAppearance();
        SelectPage(0);
        refresh.Tick += (_, _) => ReadStatus();
        VisibleChanged += (_, _) => { refresh.Enabled = Visible; if (Visible) ReadStatus(); };
        FormClosing += ClosingSettings;
        Activated += (_, _) =>
        {
            if (FollowsSystemAppearance) UpdateAppearance();
            runtime.SetKeyboardPaused(assigningKey || manualChatbox.Focused);
        };
        Deactivate += (_, _) => { StopAssigning(); runtime.SetKeyboardPaused(false); MaskPasswords(); };
    }

    private void CreateSidebar()
    {
        var brand = Caption("VRChat 语音翻译", LabelTone.Strong);
        brand.Font = brandFont;
        sidebar.Place(brand, 20, 22, 180, 30);
        steamStatus.Font = hintFont;
        steamStatus.SetStatus(false, "SteamVR 未连接 · 待机");
        sidebar.Place(steamStatus, 16, 62, 184, 27);
        string[] labels = ["麦克风与触发", "识别与翻译", "Chatbox", "VR 字幕", "语音播报"];
        SettingsGlyph[] icons = [SettingsGlyph.Microphone, SettingsGlyph.Translate, SettingsGlyph.Chat, SettingsGlyph.Headset, SettingsGlyph.Audio];
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            var nav = new NavButton(labels[i], icons[i]);
            nav.Click += (_, _) => SelectPage(index);
            tabs[i] = sidebar.Place(nav, 10, 110 + i * 42, 196, 38);
        }

        sidebarFooter.Place(sidebarDivider, 12, 0, 192, 1);
        sidebarFooter.Place(MakeButton("打开配置目录", SettingsButtonStyle.Plain, SettingsGlyph.Folder, OpenConfigFolder),
            11, 12, 134, 36);
        sidebar.Controls.Add(sidebarFooter);
    }

    private void CreateContent()
    {
        var headingLabels = new SettingsPage(494) { Dock = DockStyle.Fill };
        pageTitle.Font = titleFont;
        pageTitle.AccessibleRole = AccessibleRole.StaticText;
        headingLabels.Place(pageTitle, 0, 15, 227, 38);
        translationNotice.Font = hintFont;
        translationNotice.TextAlign = ContentAlignment.MiddleRight;
        headingLabels.Place(translationNotice, 229, 18, 253, 32);
        appearance.AttachToolTips(help);
        headingActions.Place(appearance, 0, 18, 132, 32);
        logButton.Click += (_, _) => OpenLogWindow();
        headingActions.Place(logButton, 142, 18, 94, 32);
        heading.Controls.Add(headingLabels);
        heading.Controls.Add(headingActions);

        saved.ForeColor = UiTheme.AccentText;
        saved.TextAlign = ContentAlignment.MiddleRight;
        footer.Place(saved, 445, 10, 131, 36);
        footer.Place(MakeButton("保存设置", SettingsButtonStyle.Primary, action: Save), 590, 10, 140, 36);

        foreach (var page in pages)
        {
            page.Visible = false;
            page.Dock = DockStyle.Top;
            viewport.Controls.Add(page);
        }

        content.Controls.Add(viewport);
        content.Controls.Add(footer);
        content.Controls.Add(heading);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyCaption(Handle);
        ApplyShellMetrics();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyShellMetrics();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyShellMetrics();
        var area = Screen.FromControl(this).WorkingArea;
        var saved = runtime.Current.Window;
        var border = SizeFromClientSize(Size.Empty);
        int availableWidth = Math.Max(1, area.Width - border.Width);
        int availableHeight = Math.Max(1, area.Height - border.Height);
        int Restore(int logical, int available) =>
            (int)Math.Min(available, Math.Round(logical * (DeviceDpi / 96d)));

        int defaultHeight = heading.Height + pages.Max(page => page.Height) + footer.Height + ScalePixels(8);
        ClientSize = new Size(saved is { Width: > 0 } ? Restore(saved.Width, availableWidth) : Math.Min(ScalePixels(984), availableWidth),
            saved is { Height: > 0 } ? Restore(saved.Height, availableHeight) : Math.Min(defaultHeight, availableHeight));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        currentLayout = NormalLayout();
        if (saved?.Maximized == true) WindowState = FormWindowState.Maximized;
        savedLayout = currentLayout;
    }

    private WindowLayout NormalLayout() => new(
        (int)Math.Round(ClientSize.Width * 96d / DeviceDpi), (int)Math.Round(ClientSize.Height * 96d / DeviceDpi));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (currentLayout is null) return;
        if (WindowState == FormWindowState.Normal) currentLayout = NormalLayout();
        else if (WindowState == FormWindowState.Maximized) currentLayout = currentLayout with { Maximized = true };
    }

    public void RestoreFromMinimized()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = currentLayout?.Maximized == true ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowLayout();
    }

    private void SaveWindowLayout()
    {
        if (currentLayout is null || currentLayout == savedLayout) return;
        try
        {
            store.SaveWindow(currentLayout);
            savedLayout = currentLayout;
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write($"窗口大小保存失败（0x{error.HResult:X8}）。");
        }
    }

    private bool FollowsSystemAppearance => appearance.SelectedMode == AppearanceMode.System;

    protected override void WndProc(ref Message message)
    {
        const int settingChange = 0x001A;
        const int themeChanged = 0x031A;
        bool themeNotification = message.Msg == themeChanged || message.Msg == settingChange && message.LParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(message.LParam) == "ImmersiveColorSet";
        base.WndProc(ref message);
        if (themeNotification && FollowsSystemAppearance && IsHandleCreated && !Disposing)
            BeginInvoke(UpdateAppearance);
    }

    private void UpdateAppearance()
    {
        if (Disposing || IsDisposed || !UiTheme.Select(appearance.SelectedMode)) return;
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        sidebar.BackColor = UiTheme.Sidebar;
        sidebarDivider.BackColor = UiTheme.Line;
        UiTheme.ApplyToChildren(this);
        saved.ForeColor = UiTheme.AccentText;
        ReadStatus();
        if (IsHandleCreated) UiTheme.ApplyCaption(Handle);
        Invalidate(invalidateChildren: true);
        logWindow?.ApplyTheme();
        hotwordEditor?.ApplyTheme();
        voiceManager?.ApplyTheme();
        keywordEditor?.ApplyTheme();
    }

    private void ApplyShellMetrics()
    {
        sidebar.Width = ScalePixels(216);
        sidebarFooter.Height = ScalePixels(54);
        content.Padding = new Padding(ScalePixels(18), 0, ScalePixels(18), 0);
        heading.Height = ScalePixels(62);
        headingActions.Width = ScalePixels(236);
        footer.Height = ScalePixels(58);
        var area = Screen.FromRectangle(Bounds).WorkingArea.Size;
        var minimum = SizeFromClientSize(new Size(ScalePixels(878), ScalePixels(404)));
        MinimumSize = new Size(Math.Min(area.Width, minimum.Width), Math.Min(area.Height, minimum.Height));
        viewport.AutoScrollMinSize = new Size(ScalePixels(626), 0);
        pages[0].Height = ScalePixels(advancedExpanded ? 385 : 328);
        pages[1].Height = ScalePixels(IncomingCardTop + IncomingCardHeight);
        pages[2].Height = ScalePixels(360);
        pages[3].Height = ScalePixels(402);
        pages[4].Height = ScalePixels(517);
    }

    private static CardPanel AddCard(SettingsPage page, int x, int y, int width, int height) =>
        page.Place(new CardPanel(width) { BackColor = UiTheme.Surface }, x, y, width, height);

    private Label Hint(string text)
    {
        var hint = Caption(text, LabelTone.Muted);
        hint.Font = hintFont;
        return hint;
    }

    private SettingsPage MicrophonePage()
    {
        var page = new SettingsPage();
        var mic = AddCard(page, 0, 0, 357, 270);
        var trigger = AddCard(page, 373, 0, 357, 270);
        var advancedCard = AddCard(page, 0, 284, 730, 44);

        mic.Place(Caption("麦克风"), 16, 14, 325, 24);
        mic.Place(new ComboFrame(microphones), 16, 46, 200, 33);
        var reload = MakeButton("", SettingsButtonStyle.Neutral, SettingsGlyph.Refresh, () => ReloadDevices(SelectedMicrophone));
        reload.AccessibleName = "刷新麦克风";
        help.SetToolTip(reload, reload.AccessibleName);
        mic.Place(reload, 224, 46, 32, 33);
        micTest.Click += (_, _) =>
        {
            if (runtime.MicTestActive) runtime.StopMicTest();
            else runtime.TestMicrophone(SelectedMicrophone);
        };
        mic.Place(micTest, 264, 46, 77, 33);
        mic.Place(Caption("输入电平"), 16, 99, 325, 24);
        mic.Place(meter, 16, 129, 325, 31);

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
        trigger.Place(Caption("键盘热键"), 16, 14, 325, 24);
        trigger.Place(new TextFrame(hotkey), 16, 46, 239, 33);
        trigger.Place(MakeButton("更改", action: StartAssigning), 264, 46, 77, 33);
        trigger.Place(Caption("SteamVR 触发"), 16, 98, 229, 26);
        binding.Font = hintFont;
        trigger.Place(binding, 262, 99, 79, 24);
        trigger.Place(new ComboFrame(vrMode), 16, 132, 165, 33);
        trigger.Place(MakeButton("打开 SteamVR 绑定", SettingsButtonStyle.Outline, action: runtime.OpenBindings), 189, 132, 152, 33);
        trigger.Place(Caption("录音上限"), 16, 183, 325, 25);
        trigger.Place(new NumberFrame(maxRecording), 16, 215, 117, 32);

        var advanced = MakeButton("高级设置", SettingsButtonStyle.Plain, SettingsGlyph.Chevron);
        advancedCard.Place(advanced, 13, 5, 106, 34);
        advancedCard.Place(overrideInput, 20, 43, 350, 26).Visible = false;
        var note = Hint("仅在按键冲突时启用。需同时开启 SteamVR 对应开发者设置。");
        advancedCard.Place(note, 20, 69, 680, 24).Visible = false;
        advanced.Click += (_, _) =>
        {
            advancedExpanded = !advancedExpanded;
            advanced.SetGlyph(advancedExpanded ? SettingsGlyph.Down : SettingsGlyph.Chevron);
            overrideInput.Visible = note.Visible = advancedExpanded;
            page.MoveControl(advancedCard, 284, advancedExpanded ? 101 : 44);
            page.Height = ScalePixels(advancedExpanded ? 385 : 328);
            if (advancedExpanded) viewport.ScrollControlIntoView(note);
        };
        return page;
    }

    private SettingsPage ServicePage()
    {
        var page = new SettingsPage();
        var credentials = AddCard(page, 0, 0, 730, 119);
        var translation = AddCard(page, 0, 131, 730, 159);
        var incoming = AddCard(page, 0, IncomingCardTop, 730, IncomingCardHeight);

        workspaceId.PlaceholderText = "业务空间 ID";
        credentials.Place(Caption("百炼 Workspace ID"), 16, 11, 147, 29);
        credentials.Place(new TextFrame(workspaceId), 167, 11, 260, 29);
        credentials.Place(Caption("服务地域"), 445, 11, 94, 29);
        credentials.Place(new ComboFrame(bailianRegion), 543, 11, 171, 29);
        credentials.Place(Caption("百炼 API Key"), 16, 45, 147, 29);
        credentials.Place(bailianPassword, 167, 45, 547, 29);
        var deepLLabel = credentials.Place(Caption("DeepL API Key"), 16, 79, 147, 29);
        credentials.Place(deepLPassword, 167, 79, 547, 29);
        var qwenKeyNote = credentials.Place(Hint("Qwen-MT 复用上方的百炼 API Key。"), 16, 79, 698, 29);

        translation.Place(Caption("翻译服务"), 16, 11, 147, 30);
        translation.Place(new ComboFrame(translationProvider), 167, 11, 260, 30);
        translation.Place(Caption("语音语言"), 445, 11, 94, 30);
        translation.Place(new ComboFrame(source), 543, 11, 171, 30);
        void UpdateKeyField()
        {
            bool useDeepL = translationProvider.SelectedIndex == (int)TranslationProvider.DeepL;
            deepLLabel.Visible = deepLPassword.Visible = useDeepL;
            qwenKeyNote.Visible = !useDeepL;
            if (!useDeepL) deepLPassword.MaskPassword();
        }
        translationProvider.SelectedIndexChanged += (_, _) => UpdateKeyField();

        translation.Place(Caption("译文顺序"), 16, 47, 147, 30);
        translation.Place(targets, 167, 47, 547, 2 * LanguageOrder.LogicalItemHeight);
        var add = MakeButton("添加语言", SettingsButtonStyle.Outline, SettingsGlyph.Plus, () =>
        {
            if (LanguageOrder.ChooseLanguage(this, Font) is { } code) targets.AddLanguage(code);
        });
        translation.Place(add, 167, 121, 152, 28);
        translation.Place(hotwordButton, 536, 121, 178, 28);
        hotwordButton.Click += (_, _) => EditHotwords();
        void ResizeLanguages()
        {
            int rows = Math.Min(2, targets.Items.Count);
            targets.Visible = rows > 0;
            translation.MoveControl(targets, 47, rows * LanguageOrder.LogicalItemHeight);
            translation.MoveControl(add, 47 + rows * LanguageOrder.LogicalItemHeight + (rows == 0 ? 0 : 7));
        }
        targets.LanguagesChanged += (_, _) => ResizeLanguages();
        ResizeLanguages();

        incoming.Place(Caption("来话翻译"), 16, 11, 112, 29);
        incoming.Place(incomingToggle, 125, 11, 52, 29);
        incomingState.Font = hintFont;
        incoming.Place(incomingState, 189, 11, 262, 29);
        incoming.Place(Caption("加强底噪过滤"), 502, 11, 153, 29);
        incoming.Place(incomingNoiseFilter, 662, 11, 52, 29);
        help.SetToolTip(incomingNoiseFilter, "开启来话翻译前调整。加强过滤也可能漏掉轻声说话。");
        incomingToggle.Click += (_, _) => runtime.SetIncoming(!runtime.Status.IncomingActive, SelectedPlayback, incomingNoiseFilter.Checked);
        incoming.Place(Caption("断句间隔"), 16, 47, 147, 30);
        incoming.Place(new NumberFrame(incomingSentenceSilence), 167, 47, 148, 30);
        help.SetToolTip(incomingSentenceSilence, "停顿达到这个时长后，百炼可结束当前句子。保存后从下一段来话识别开始生效。");
        incoming.Place(Hint("保存后从下一段来话识别开始生效"), 330, 47, 384, 30);
        if (IncomingLoopbackCapture.UsesProcessLoopback)
        {
            incoming.Place(Hint("采集 VRChat 的声音，音频交给百炼，文字交给所选翻译服务。"), 16, 81, 698, 26);
        }
        else
        {
            incoming.Place(Caption("播放设备"), 16, 81, 147, 30);
            incoming.Place(new ComboFrame(playback), 167, 81, 503, 30);
            refreshPlayback.AccessibleName = "刷新播放设备";
            refreshPlayback.Click += (_, _) => ReloadPlaybackDevices(playback, SelectedPlayback);
            incoming.Place(refreshPlayback, 681, 81, 33, 30);
            incoming.Place(Hint("采集所选设备的全部声音。音频交给百炼，文字交给所选翻译服务。"), 16, 116, 698, 26);
        }
        return page;
    }

    private SettingsPage ChatboxPage()
    {
        var page = new SettingsPage();
        var output = AddCard(page, 0, 0, 730, 93);
        var manual = AddCard(page, 0, 106, 730, 112);
        var recent = AddCard(page, 0, 231, 730, 129);

        output.Place(Caption("发送 Chatbox 消息"), 16, 12, 168, 30);
        output.Place(chatEnabled, 189, 12, 52, 30);
        output.Place(Caption("OSC 端口"), 16, 49, 168, 31);
        output.Place(new NumberFrame(oscPort), 189, 49, 115, 31);
        output.Place(MakeButton("发送测试消息", SettingsButtonStyle.Outline, action: () => runtime.TestChatbox((int)oscPort.Value)), 575, 49, 139, 31);
        oscState.Font = hintFont;
        output.Place(oscState, 323, 49, 239, 31);

        manual.Place(Caption("手动输入"), 16, 14, 83, 28);
        manual.Place(new TextFrame(manualChatbox), 101, 14, 461, 84);
        manual.Place(sendManual, 575, 32, 139, 33);
        var shortcut = Hint("Ctrl + Enter");
        shortcut.TextAlign = ContentAlignment.MiddleCenter;
        manual.Place(shortcut, 575, 67, 139, 23);
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

        recent.Place(Caption("最近输出预览"), 16, 8, 698, 26);
        recent.Place(new TextFrame(oscPreview, preview: true), 16, 38, 698, 76);
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
        var options = AddCard(page, 0, 0, 284, 402);
        var preview = AddCard(page, 300, 0, 430, 350);
        options.Place(Caption("显示头显字幕"), 16, 13, 191, 28);
        options.Place(subtitleEnabled, 215, 13, 53, 28);
        options.Place(Caption("显示原文"), 16, 49, 191, 28);
        options.Place(original, 215, 49, 53, 28);
        SubtitleRow(options, "显示时长", duration, 89, fineAtStart: true);
        SubtitleRow(options, "字号", font, 149);
        SubtitleRow(options, "不透明度", opacity, 209);
        SubtitleRow(options, "视线下方", angle, 269);
        SubtitleRow(options, "距离", distance, 329);
        preview.Place(Caption("头显视角预览"), 16, 8, 398, 27);
        preview.Place(headsetPreview, 16, 41, 398, 250);
        preview.Place(previewButton, 145, 306, 140, 32);
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

    private SettingsPage SpeechPage()
    {
        var page = new SettingsPage();
        var speech = AddCard(page, 0, 0, 730, 97);
        var expression = AddCard(page, 0, 110, 730, 85);
        var voices = AddCard(page, 0, 208, 730, 160);
        var playbackCard = AddCard(page, 0, 381, 730, 136);

        speech.Place(Caption("启用自动播报"), 16, 9, 142, 30);
        speech.Place(speechEnabled, 161, 9, 52, 30);
        speech.Place(Hint("保存设置后应用"), 224, 9, 174, 30);
        speech.Place(Caption("监听播报"), 497, 9, 153, 30);
        speech.Place(speechMonitor, 662, 9, 52, 30);
        speech.Place(Caption("播报模式"), 16, 51, 102, 32);
        speech.Place(new ComboFrame(speechMode), 121, 51, 220, 32);
        speech.Place(Caption("播报音量"), 363, 51, 78, 32);
        var volumeSlider = speech.Place(new SettingsSlider(speechVolume, false), 446, 51, 173, 32);
        speech.Place(new NumberFrame(speechVolume, pill: true), 629, 51, 85, 32);
        help.SetToolTip(volumeSlider, "同时调整输出设备和监听设备的音量。");

        speechExpression.Items.AddRange(SpeechExpression.Tags.ToArray());
        expression.Place(Caption("播报语气"), 16, 6, 102, 32);
        expression.Place(new ComboFrame(speechExpression), 121, 6, 245, 32);
        expression.Place(MakeButton("关键词规则", SettingsButtonStyle.Outline, action: EditSpeechKeywords), 380, 6, 110, 32);
        expression.Place(Hint("从下一句生效，保存后记住"), 502, 6, 213, 32);
        expression.Place(Caption("播报语速"), 16, 46, 102, 32);
        expression.Place(new NumberFrame(speechRate), 121, 46, 102, 32);
        expression.Place(MakeButton("恢复 1.00×", action: () => speechRate.Value = 1m), 235, 46, 111, 32);
        expression.Place(Hint("中文、英文和日文共用语速"), 362, 46, 300, 32);

        voices.Place(MakeButton("音色管理", SettingsButtonStyle.Outline, action: ManageVoices), 566, 8, 148, 30);
        VoiceRow(voices, voiceChinese, 48);
        VoiceRow(voices, voiceEnglish, 83);
        VoiceRow(voices, voiceJapanese, 118);

        playbackCard.Place(Caption("输出设备"), 16, 11, 145, 31);
        playbackCard.Place(new ComboFrame(speechOutput), 167, 11, 503, 31);
        var reload = MakeButton("", SettingsButtonStyle.Neutral, SettingsGlyph.Refresh,
            () => ReloadSpeechDevices(ReadSpeech()));
        reload.AccessibleName = "刷新播报和监听设备";
        playbackCard.Place(reload, 681, 11, 33, 31);
        playbackCard.Place(Caption("监听设备"), 16, 48, 145, 31);
        playbackCard.Place(new ComboFrame(speechMonitorDevice), 167, 48, 503, 31);
        speechMonitorDevice.Enabled = false;
        speechMonitor.CheckedChanged += (_, _) => speechMonitorDevice.Enabled = speechMonitor.Checked;
        playbackCard.Place(speechPreview, 16, 93, 95, 31);
        playbackCard.Place(speechStop, 122, 93, 104, 31);
        playbackCard.Place(speechState, 245, 93, 469, 31);
        speechState.Font = hintFont;
        speechPreview.Click += (_, _) => runtime.PreviewSpeech(ReadSpeech());
        speechStop.Click += (_, _) => runtime.StopSpeech();
        return page;
    }

    private void VoiceRow(SettingsPage page, ComboBox picker, int y)
    {
        page.Place(Caption(picker.AccessibleName!), 16, y, 145, 30);
        page.Place(new ComboFrame(picker), 167, y, 547, 30);
        picker.SelectedIndexChanged += (_, _) => help.SetToolTip(picker, SelectedVoiceId(picker));
    }

    private static string SelectedVoiceId(ComboBox picker) => (picker.SelectedItem as SpeechVoiceChoice)?.Id ?? "";

    private void EditSpeechKeywords()
    {
        using var dialog = new SpeechKeywordDialog(Font, speechKeywords);
        dialog.Activated += (_, _) => runtime.SetKeyboardPaused(true);
        dialog.Deactivate += (_, _) => runtime.SetKeyboardPaused(false);
        keywordEditor = dialog;
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                speechKeywords = dialog.Rules;
                ApplySpeechStyle();
            }
        }
        finally { keywordEditor = null; }
    }

    private void ManageVoices()
    {
        using var dialog = new SpeechVoiceManager(Font, ReadSpeech(), runtime.SetKeyboardPaused);
        voiceManager = dialog;
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) LoadVoiceLibrary(dialog.Draft);
        }
        finally { voiceManager = null; }
    }

    private void LoadVoiceLibrary(SpeechSettings speech)
    {
        voiceLibrary = speech.Voices ?? [];
        FillVoicePicker(voiceChinese, SpeechMode.ChineseOriginal, speech.ChineseVoice);
        FillVoicePicker(voiceEnglish, SpeechMode.EnglishTranslation, speech.EnglishVoice);
        FillVoicePicker(voiceJapanese, SpeechMode.JapaneseTranslation, speech.JapaneseVoice);
    }

    private void ReloadVoiceChoices()
    {
        FillVoicePicker(voiceChinese, SpeechMode.ChineseOriginal, SelectedVoiceId(voiceChinese));
        FillVoicePicker(voiceEnglish, SpeechMode.EnglishTranslation, SelectedVoiceId(voiceEnglish));
        FillVoicePicker(voiceJapanese, SpeechMode.JapaneseTranslation, SelectedVoiceId(voiceJapanese));
    }

    private void FillVoicePicker(ComboBox picker, SpeechMode mode, string selectedId)
    {
        var choices = SpeechVoiceCatalog.Choices(mode, (BailianRegion)bailianRegion.SelectedIndex, selectedId, voiceLibrary);
        picker.BeginUpdate();
        try
        {
            picker.Items.Clear();
            picker.Items.AddRange(choices);
            int selected = Array.FindIndex(choices, choice => choice.Id == selectedId);
            picker.SelectedIndex = selected >= 0 ? selected : choices.Length - 1;
        }
        finally { picker.EndUpdate(); }
    }

    private void SubtitleRow(SettingsPage page, string label, NumericUpDown number, int y, bool fineAtStart = false)
    {
        page.Place(Caption(label), 16, y, 148, 29);
        page.Place(new NumberFrame(number, pill: true), 171, y, 97, 29);
        page.Place(new SettingsSlider(number, fineAtStart), 13, y + 31, 258, 23);
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
        viewport.AutoScrollPosition = Point.Empty;
        pageTitle.Text = tabs[index].Text;
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

    private void EditHotwords()
    {
        using var dialog = new RecognitionVocabularyDialog(Font, recognitionHotwordsEnabled, recognitionHotwords, terminology,
            (BailianRegion)bailianRegion.SelectedIndex);
        dialog.Activated += (_, _) => runtime.SetKeyboardPaused(true);
        dialog.Deactivate += (_, _) => runtime.SetKeyboardPaused(false);
        hotwordEditor = dialog;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            recognitionHotwords = dialog.Vocabulary;
            recognitionHotwordsEnabled = dialog.HotwordsEnabled;
            terminology = dialog.Terminology;
            RefreshHotwordButton();
        }
        finally { hotwordEditor = null; }
    }

    private void RefreshHotwordButton()
    {
        hotwordButton.Text = !recognitionHotwordsEnabled ? "热词词库（已停用）"
            : recognitionHotwords.Count > 0 ? $"热词词库（{recognitionHotwords.Count}）" : "热词词库";
    }

    private void LoadValues(Settings settings)
    {
        recognitionHotwords = settings.RecognitionHotwords;
        recognitionHotwordsEnabled = settings.RecognitionHotwordsEnabled;
        terminology = settings.Terminology;
        RefreshHotwordButton();
        appearance.SelectedMode = settings.Appearance;
        ReloadDevices(settings.MicrophoneId);
        if (!IncomingLoopbackCapture.UsesProcessLoopback) ReloadPlaybackDevices(playback, settings.IncomingPlaybackDeviceId);
        incomingNoiseFilter.Checked = settings.IncomingStrongerNoiseFilter;
        incomingSentenceSilence.Value = settings.IncomingSentenceSilenceMs;
        ReloadSpeechDevices(settings.Speech);
        speechEnabled.Checked = settings.Speech.Enabled;
        speechMonitor.Checked = settings.Speech.MonitorEnabled;
        speechMode.SelectedIndex = (int)settings.Speech.Mode;
        speechVolume.Value = settings.Speech.VolumePercent;
        speechRate.Value = settings.Speech.Rate;
        speechKeywords = settings.Speech.Keywords;
        speechExpression.SelectedItem = settings.Speech.Expression switch
        {
            SpeechExpression.Auto => speechExpression.Items[0],
            SpeechExpression.Plain => speechExpression.Items[1],
            var id => SpeechExpression.Tags.First(tag => tag.Id == id)
        };
        key = settings.KeyboardVirtualKey;
        hotkey.Text = ((Keys)key).ToString();
        vrMode.SelectedIndex = (int)settings.VrBinding;
        overrideInput.Checked = settings.ExperimentalVrInputOverride;
        maxRecording.Value = settings.MaxRecordingSeconds;
        workspaceId.Text = settings.BailianWorkspaceId;
        bailianRegion.SelectedIndex = (int)settings.BailianRegion;
        LoadVoiceLibrary(settings.Speech.Validate());
        bailianKey.Text = settings.BailianApiKey;
        deepLKey.Text = settings.DeepLApiKey;
        translationProvider.SelectedIndex = (int)settings.TranslationProvider;
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

    private SpeechSettings ReadSpeech() => new()
    {
        Enabled = speechEnabled.Checked, Mode = (SpeechMode)speechMode.SelectedIndex,
        ChineseVoice = SelectedVoiceId(voiceChinese), EnglishVoice = SelectedVoiceId(voiceEnglish), JapaneseVoice = SelectedVoiceId(voiceJapanese),
        Voices = voiceLibrary,
        Rate = speechRate.Value, Keywords = speechKeywords,
        Expression = SelectedExpression,
        VolumePercent = (int)speechVolume.Value, OutputDeviceId = (speechOutput.SelectedItem as CaptureDevice)?.Id,
        MonitorEnabled = speechMonitor.Checked, MonitorDeviceId = (speechMonitorDevice.SelectedItem as CaptureDevice)?.Id
    };

    private string SelectedExpression => speechExpression.SelectedItem is SpeechTag tag ? tag.Id :
        speechExpression.SelectedIndex == 1 ? SpeechExpression.Plain : SpeechExpression.Auto;

    private void ApplySpeechStyle()
    {
        var current = runtime.Current;
        runtime.Apply(current with { Speech = current.Speech with
            { Expression = SelectedExpression, Keywords = speechKeywords, Rate = speechRate.Value } });
    }

    private void ReloadSpeechDevices(SpeechSettings settings)
    {
        var devices = ReadPlaybackDevices();
        LoadPlaybackDevices(speechOutput, devices, settings.OutputDeviceId);
        LoadPlaybackDevices(speechMonitorDevice, devices, settings.MonitorDeviceId);
    }

    private void Save()
    {
        try
        {
            var updated = store.Save(new Settings
            {
                Window = currentLayout,
                Appearance = appearance.SelectedMode,
                MicrophoneId = SelectedMicrophone, KeyboardVirtualKey = key, VrBinding = (VrBindingMode)vrMode.SelectedIndex,
                IncomingPlaybackDeviceId = IncomingLoopbackCapture.UsesProcessLoopback ? runtime.Current.IncomingPlaybackDeviceId : SelectedPlayback,
                IncomingStrongerNoiseFilter = incomingNoiseFilter.Checked,
                IncomingSentenceSilenceMs = (int)incomingSentenceSilence.Value,
                ExperimentalVrInputOverride = overrideInput.Checked, MaxRecordingSeconds = (int)maxRecording.Value,
                BailianWorkspaceId = workspaceId.Text, BailianRegion = (BailianRegion)bailianRegion.SelectedIndex,
                BailianApiKey = bailianKey.Text, DeepLApiKey = deepLKey.Text,
                TranslationProvider = (TranslationProvider)translationProvider.SelectedIndex,
                SourceLanguage = (SourceLanguage)source.SelectedIndex, TargetLanguages = targets.Codes.ToArray(),
                RecognitionHotwords = recognitionHotwords, RecognitionHotwordsEnabled = recognitionHotwordsEnabled,
                Terminology = terminology,
                ChatboxEnabled = chatEnabled.Checked, OscPort = (int)oscPort.Value, Subtitles = ReadSubtitles(), Speech = ReadSpeech()
            });
            runtime.Apply(updated);
            savedLayout = currentLayout;
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
    private string? SelectedPlayback => (playback.SelectedItem as CaptureDevice)?.Id;

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

    private void ReloadPlaybackDevices(ComboBox picker, string? selected) =>
        LoadPlaybackDevices(picker, ReadPlaybackDevices(), selected);

    private List<CaptureDevice> ReadPlaybackDevices()
    {
        try { return CaptureDevice.PlaybackDevices(); }
        catch (Exception error)
        {
            ShowError($"无法枚举播放设备（0x{error.HResult:X8}）。");
            return [new(null, "Windows 默认播放设备")];
        }
    }

    private static void LoadPlaybackDevices(ComboBox picker, List<CaptureDevice> devices, string? selected)
    {
        picker.Items.Clear();
        picker.Items.AddRange(devices.Cast<object>().ToArray());
        int index = devices.FindIndex(device => device.Id == selected);
        if (index < 0)
            index = picker.Items.Add(new CaptureDevice(selected, "已保存的播放设备（当前不可用）"));
        picker.SelectedIndex = index;
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
        speechPreview.Enabled = status.Phase is not VoicePhase.Recording;
        speechStop.Enabled = status.SpeechActive;
        if (speechState.Text != status.SpeechStatus)
        {
            speechState.Text = status.SpeechStatus;
            help.SetToolTip(speechState, status.SpeechStatus);
        }
        string phase = status.Phase switch
        {
            VoicePhase.Recording => "录音中", VoicePhase.Recognizing => "识别中", VoicePhase.Translating => "翻译中", _ => "待机"
        };
        steamStatus.SetStatus(status.SteamVrConnected, $"SteamVR {(status.SteamVrConnected ? "已连接" : "未连接")} · {phase}");
        var vr = status.Binding;
        binding.Text = !status.SteamVrConnected ? "未连接" : !vr.Bound ? "待绑定" :
            !vr.Trigger.Available ? "不可用" : vr.Trigger.Held ? "按住" : "已绑定";
        binding.ForeColor = !status.SteamVrConnected ? UiTheme.Muted : vr.Bound ? UiTheme.AccentText : UiTheme.Amber;
        binding.BackColor = !status.SteamVrConnected ? UiTheme.Track : vr.Bound ? UiTheme.TealTint : UiTheme.AmberTint;
        if (lastVrInfo != status.Vr)
        {
            lastVrInfo = status.Vr;
            help.SetToolTip(binding, status.Vr);
            help.SetToolTip(steamStatus, status.Vr);
        }
        if (translationNotice.Text != status.TranslationNotice)
        {
            translationNotice.Text = status.TranslationNotice;
            help.SetToolTip(translationNotice, status.TranslationNotice);
        }
        incomingToggle.Checked = status.IncomingActive;
        incomingNoiseFilter.Enabled = !status.IncomingActive;
        playback.Enabled = refreshPlayback.Enabled = !status.IncomingActive;
        if (incomingState.Text != status.IncomingStatus)
        {
            incomingState.Text = status.IncomingStatus;
            help.SetToolTip(incomingState, status.IncomingStatus);
        }
        oscState.Text = status.ChatboxState;
        oscPreview.ForeColor = status.ChatboxText.Length == 0 ? UiTheme.Muted : UiTheme.Ink;
        string output = status.ChatboxText.Length == 0 ? "发送的文字会显示在这里" : status.ChatboxText.Replace("\n", "\r\n");
        if (oscPreview.Text != output) oscPreview.Text = output;
        meter.SetLevel(runtime.Meter);
        micTest.Text = runtime.MicTestActive ? "停止测试" : "测试电平";
        if (DateTime.UtcNow >= savedUntil) saved.Text = "";
    }

    private void OpenLogWindow()
    {
        if (logWindow is { IsDisposed: false } existing)
        {
            existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new LogWindow(log) { Font = Font, Icon = Icon };
        window.FormClosed += (_, _) => logWindow = null;
        logWindow = window;
        window.Show(this);
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
        SaveWindowLayout();
        logWindow?.Close();
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

    private void ShowError(string error)
    {
        log.Write(error.ReplaceLineEndings(" "));
        MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private int ScalePixels(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96f);

    private static Label Caption(string text, LabelTone tone = LabelTone.Normal) => new ThemeLabel(tone)
    {
        Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };

    private static TextBox Entry(string name, bool readOnly = false, bool password = false) => new()
    {
        AccessibleName = name, ReadOnly = readOnly, UseSystemPasswordChar = password
    };

    private static SettingsButton MakeButton(string text, SettingsButtonStyle style = SettingsButtonStyle.Neutral,
        SettingsGlyph glyph = SettingsGlyph.None, Action? action = null)
    {
        var button = new SettingsButton(text, style, glyph);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static ComboBox DropDown(string name, params string[] items)
    {
        var combo = new DarkComboBox
        {
            AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink, ItemHeight = 22
        };
        combo.Items.AddRange(items);
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool isSelection = (e.State & DrawItemState.ComboBoxEdit) != 0;
            Color fill = !isSelection && (e.State & DrawItemState.Selected) != 0 ? UiTheme.TealTint : combo.BackColor;
            using var brush = new SolidBrush(fill);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var area = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), e.Font, area, combo.Enabled ? UiTheme.Ink : UiTheme.Muted,
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
        if (disposing) { bodyFont.Dispose(); hintFont.Dispose(); titleFont.Dispose(); brandFont.Dispose(); }
    }
}
