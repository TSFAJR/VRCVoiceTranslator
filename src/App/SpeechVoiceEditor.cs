using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SpeechVoiceEditor : Form
{
    private readonly TextBox id = new() { AccessibleName = "音色 ID" };
    private readonly TextBox alias = new() { AccessibleName = "音色别名" };

    private readonly CheckBox chinese = new() { Text = "中文" };
    private readonly CheckBox english = new() { Text = "英文" };
    private readonly CheckBox japanese = new() { Text = "日文" };

    public SpeechVoiceEditor(Font font, SpeechVoiceChoice? existing, Func<string, bool> isDuplicate)
    {
        bool editing = existing is not null;
        Font = font;
        Text = editing ? "修改音色" : "添加音色";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(570, 202);
        Padding = new Padding(16, 8, 16, 10);
        DoubleBuffered = true;

        id.Text = existing?.Id ?? "";
        alias.Text = existing?.Alias ?? "";
        var languages = existing?.Languages ?? SpeechVoiceLanguages.None;
        chinese.Checked = languages.HasFlag(SpeechVoiceLanguages.Chinese);
        english.Checked = languages.HasFlag(SpeechVoiceLanguages.English);
        japanese.Checked = languages.HasFlag(SpeechVoiceLanguages.Japanese);
        var layout = new SettingsPage(538) { Dock = DockStyle.Fill };
        layout.Place(Label("音色 ID"), 0, 4, 96, 30);
        layout.Place(new TextFrame(id), 96, 4, 442, 30);
        layout.Place(Label("别名"), 0, 42, 96, 30);
        layout.Place(new TextFrame(alias), 96, 42, 442, 30);
        layout.Place(Label("支持的语言"), 0, 80, 96, 30);
        layout.Place(chinese, 96, 80, 92, 30);
        layout.Place(english, 197, 80, 92, 30);
        layout.Place(japanese, 298, 80, 92, 30);

        var cancel = new SettingsButton("取消") { DialogResult = DialogResult.Cancel };
        var confirm = new SettingsButton("确定", SettingsButtonStyle.Primary);
        confirm.Click += (_, _) =>
        {
            if (VoiceId.Length == 0 || VoiceId.Any(char.IsControl))
            {
                MessageBox.Show(this, "请输入有效的音色 ID。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                id.Focus();
                return;
            }
            if (Alias.Any(char.IsControl))
            {
                MessageBox.Show(this, "别名不能包含控制字符。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                alias.Focus();
                return;
            }
            if (isDuplicate(VoiceId))
            {
                MessageBox.Show(this, "音色库中已有这个 ID。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                id.Focus();
                return;
            }

            var selectedLanguages = SpeechVoiceLanguages.None;
            if (chinese.Checked) selectedLanguages |= SpeechVoiceLanguages.Chinese;
            if (english.Checked) selectedLanguages |= SpeechVoiceLanguages.English;
            if (japanese.Checked) selectedLanguages |= SpeechVoiceLanguages.Japanese;
            if (selectedLanguages == SpeechVoiceLanguages.None)
            {
                MessageBox.Show(this, "请勾选至少一种支持的语言。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                chinese.Focus();
                return;
            }
            Choice = new(VoiceId, Alias, selectedLanguages);
            DialogResult = DialogResult.OK;
        };
        layout.Place(cancel, 348, 133, 88, 31);
        layout.Place(confirm, 450, 133, 88, 31);
        Controls.Add(layout);
        CancelButton = cancel;
        AcceptButton = confirm;
        Shown += (_, _) => (editing ? alias : id).Focus();
        ApplyTheme();
    }

    public SpeechVoiceChoice? Choice { get; private set; }
    private string VoiceId => id.Text.Trim();
    private string Alias => alias.Text.Trim();

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
