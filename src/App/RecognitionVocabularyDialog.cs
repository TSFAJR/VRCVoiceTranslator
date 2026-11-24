using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class RecognitionVocabularyDialog : Form
{
    private readonly SettingsSwitch enabled = new("使用识别热词");
    private readonly TextBox entries = Editor("识别热词");
    private readonly TextBox context = Editor("识别上下文", wrap: true);
    private readonly TextBox corrections = Editor("识别纠错");
    private readonly TextBox terms = Editor("译文术语");
    private readonly BufferedPanel[] pages;
    private readonly SettingsButton[] tabs;

    public RecognitionVocabularyDialog(Font font, bool useHotwords, IReadOnlyDictionary<string, int> vocabulary,
        TerminologySettings terminology, BailianRegion region)
    {
        Font = font;
        Text = "热词词库";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(588, 455);
        MinimumSize = new Size(530, 410);
        Padding = new Padding(16, 10, 16, 12);
        DoubleBuffered = true;
        enabled.Checked = useHotwords;
        Vocabulary = vocabulary;
        Terminology = terminology;
        entries.Lines = vocabulary.Select(pair => pair.Value == RecognitionVocabulary.DefaultWeight
            ? pair.Key
            : FormattableString.Invariant($"{pair.Key}\t{pair.Value}")).ToArray();
        context.Text = terminology.Context.ReplaceLineEndings(Environment.NewLine);
        context.PlaceholderText = "在 VRChat 里，全追是全身追踪的简称。";
        corrections.Lines = terminology.Corrections.Select(rule => $"{rule.From}\t{rule.To}").ToArray();
        terms.Lines = terminology.Terms.Select(term => $"{term.Source}\t{term.TargetLanguage}\t{term.Target}").ToArray();

        bool singapore = region == BailianRegion.Singapore;
        var heading = new SettingsPage(556) { Dock = DockStyle.Top, Height = singapore ? 114 : 88 };
        heading.Place(Label("使用识别热词"), 0, 1, 110, 30);
        heading.Place(enabled, 114, 1, 52, 30);
        heading.Place(Label("每行一个词，可整列粘贴。默认权重 4。", LabelTone.Muted), 0, 32, 556, 18);
        heading.Place(Label("调整权重时，在词后按 Tab 并输入 1–5 或 50。", LabelTone.Muted), 0, 50, 556, 18);
        heading.Place(Label("权重 50 用于经常认错的模型名或地图名，最多 50 条。", LabelTone.Muted), 0, 68, 556, 18);
        if (singapore)
            heading.Place(Label("新加坡子业务空间不支持即时热词，请关闭上方开关。", LabelTone.Muted), 0, 88, 556, 22);

        var recognition = new SettingsPage(556) { Dock = DockStyle.Top, Height = 154 };
        recognition.Place(Label("识别上下文（可选，最多 200 字）"), 0, 0, 416, 22);
        recognition.Place(CopyButton("在 VRChat 里，全追是全身追踪的简称。", "复制语料示例"), 440, 0, 116, 22);
        recognition.Place(new TextFrame(context), 0, 23, 556, 63);
        recognition.Place(Label("仅将这里填写的语料随百炼识别请求发送。", LabelTone.Muted), 0, 88, 556, 20);
        recognition.Place(Label("识别纠错"), 0, 110, 350, 25);
        recognition.Place(CopyButton("什么全锥 → 什么全追\r\nKeepful → Kipfel"), 440, 110, 116, 25);
        recognition.Place(Label("每行一条。按「误识别 → 正确写法」填写，两栏也可用 Tab 分隔。", LabelTone.Muted), 0, 135, 556, 19);

        var translation = new SettingsPage(556) { Dock = DockStyle.Top, Height = 70 };
        translation.Place(Label("只随 Qwen-MT Flash 或 Lite 请求发送本句命中的术语。", LabelTone.Muted), 0, 0, 556, 23);
        translation.Place(Label("每行填写原词、目标语言代码和指定译法，用 Tab 分隔。", LabelTone.Muted), 0, 23, 556, 20);
        translation.Place(Label("目标语言代码示例：EN-US、JA", LabelTone.Muted), 0, 43, 416, 24);
        translation.Place(CopyButton("全追\tEN-US\tfull-body tracking\r\n全追\tJA\tフルトラ"), 440, 43, 116, 24);

        pages = [MakePage(heading, entries), MakePage(recognition, corrections), MakePage(translation, terms)];
        var navigation = new SettingsPage(556) { Dock = DockStyle.Top, Height = 39 };
        tabs = [new("识别热词", SettingsButtonStyle.Navigation), new("上下文与纠错", SettingsButtonStyle.Navigation),
            new("译文术语", SettingsButtonStyle.Navigation)];
        for (int i = 0; i < tabs.Length; i++)
        {
            int page = i;
            navigation.Place(tabs[i], i * 138, 0, 132, 31);
            tabs[i].Click += (_, _) => ShowPage(page);
        }

        var actions = new SettingsPage(556) { Dock = DockStyle.Bottom, Height = 48 };
        var cancel = new SettingsButton("取消") { DialogResult = DialogResult.Cancel };
        var apply = new SettingsButton("确定", SettingsButtonStyle.Primary);
        apply.Click += (_, _) =>
        {
            int page = 0;
            try
            {
                var updated = RecognitionVocabulary.ParseLines(entries.Lines);
                page = 1;
                var rules = ParseRows(corrections.Lines, 2, "识别纠错", allowArrow: true)
                    .Select(row => new RecognitionCorrection(row[0], row[1])).ToArray();
                var recognitionOptions = new TerminologySettings(context.Text, rules);
                page = 2;
                var translations = ParseRows(terms.Lines, 3, "译文术语")
                    .Select(row => new TranslationTerm(row[0], row[1], row[2])).ToArray();
                Terminology = new(recognitionOptions.Context, recognitionOptions.Corrections, translations);
                Vocabulary = updated;
                DialogResult = DialogResult.OK;
            }
            catch (FormatException error)
            {
                ShowPage(page);
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        actions.Place(Label("在主窗口点击「保存设置」后生效", LabelTone.Muted), 0, 12, 316, 32);
        actions.Place(cancel, 362, 12, 90, 32);
        actions.Place(apply, 466, 12, 90, 32);

        var body = new BufferedPanel { Dock = DockStyle.Fill };
        body.Controls.AddRange(pages);
        Controls.Add(body);
        Controls.Add(actions);
        Controls.Add(navigation);
        AcceptButton = apply;
        CancelButton = cancel;
        Shown += (_, _) => ShowPage(0);
        ApplyTheme();
    }

    public IReadOnlyDictionary<string, int> Vocabulary { get; private set; }
    public TerminologySettings Terminology { get; private set; }

    public bool HotwordsEnabled => enabled.Checked;

    private void ShowPage(int index)
    {
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == index;
            tabs[i].SetSelected(i == index);
        }
        (index switch { 0 => entries, 1 => context, _ => terms }).Focus();
    }

    private static BufferedPanel MakePage(SettingsPage heading, TextBox text)
    {
        var panel = new BufferedPanel { Dock = DockStyle.Fill };
        panel.Controls.Add(new TextFrame(text) { Dock = DockStyle.Fill });
        panel.Controls.Add(heading);
        return panel;
    }

    private static TextBox Editor(string name, bool wrap = false) => new()
    {
        AccessibleName = name, Multiline = true, AcceptsReturn = true, AcceptsTab = !wrap,
        WordWrap = wrap, ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both
    };

    private SettingsButton CopyButton(string sample, string label = "复制示例")
    {
        var button = new SettingsButton(label, SettingsButtonStyle.Outline);
        button.Click += (_, _) =>
        {
            try { Clipboard.SetText(sample); }
            catch (System.Runtime.InteropServices.ExternalException)
            { MessageBox.Show(this, "剪贴板暂时不可用。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        return button;
    }

    private static IEnumerable<string[]> ParseRows(string[] lines, int fields, string label, bool allowArrow = false)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] row = allowArrow && !lines[i].Contains('\t') ? lines[i].Split('→', 2) : lines[i].Split('\t');
            if (row.Length != fields || row.Any(string.IsNullOrWhiteSpace))
                throw new FormatException($"{label}第 {i + 1} 行格式无效。请按页面示例填写。");
            yield return row;
        }
    }

    public void ApplyTheme()
    {
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        UiTheme.ApplyToChildren(this);
        if (IsHandleCreated) UiTheme.ApplyCaption(Handle);
        Invalidate(invalidateChildren: true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyCaption(Handle);
    }

    private static Label Label(string text, LabelTone tone = LabelTone.Normal) => new ThemeLabel(tone)
    {
        Text = text, TextAlign = ContentAlignment.MiddleLeft
    };
}
