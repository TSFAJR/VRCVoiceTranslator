using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SpeechVoiceManager : Form
{
    private readonly List<SpeechVoiceChoice> voices;
    private readonly ListView list = new()
    {
        AccessibleName = "音色列表", View = View.Details, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false, HideSelection = false
    };
    private readonly SettingsButton edit = new("修改");
    private readonly SettingsButton remove = new("删除");
    private readonly Action<bool> pauseKeyboard;
    private SpeechVoiceEditor? editor;

    public SpeechVoiceManager(Font font, SpeechSettings settings, Action<bool> pauseKeyboard)
    {
        Draft = settings;
        voices = [..settings.Voices ?? []];
        this.pauseKeyboard = pauseKeyboard;
        Font = font;
        Text = "音色管理";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(724, 428);
        MinimumSize = new Size(600, 340);
        Padding = new Padding(16, 12, 16, 12);
        DoubleBuffered = true;

        list.Dock = DockStyle.Fill;
        list.Columns.Add("别名");
        list.Columns.Add("音色 ID");
        list.Columns.Add("支持的语言");
        list.Resize += (_, _) => ResizeColumns();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += (_, _) => { if (SelectedIndex >= 0) EditVoice(SelectedIndex); };
        var toolbar = new SettingsPage(692) { Dock = DockStyle.Top, Height = 44 };
        var add = new SettingsButton("添加音色", SettingsButtonStyle.Outline, SettingsGlyph.Plus);
        add.Click += (_, _) => EditVoice(-1);
        edit.Click += (_, _) => { if (SelectedIndex >= 0) EditVoice(SelectedIndex); };
        remove.Click += (_, _) =>
        {
            int index = SelectedIndex;
            if (index < 0) return;
            ChangeSelectedId(voices[index].Id, "");
            voices.RemoveAt(index);
            RefreshList(Math.Min(index, voices.Count - 1));
        };
        toolbar.Place(add, 0, 0, 116, 31);
        toolbar.Place(edit, 126, 0, 82, 31);
        toolbar.Place(remove, 218, 0, 82, 31);

        var actions = new SettingsPage(692) { Dock = DockStyle.Bottom, Height = 48 };
        var cancel = new SettingsButton("取消") { DialogResult = DialogResult.Cancel };
        var confirm = new SettingsButton("确定", SettingsButtonStyle.Primary);
        confirm.Click += (_, _) =>
        {
            Draft = (Draft with { Voices = voices.ToArray() }).Validate();
            DialogResult = DialogResult.OK;
        };
        var hint = new ThemeLabel(LabelTone.Muted)
        {
            Text = "返回主窗口后点击「保存设置」", TextAlign = ContentAlignment.MiddleLeft
        };
        actions.Place(hint, 0, 12, 410, 32);
        actions.Place(cancel, 500, 12, 90, 32);
        actions.Place(confirm, 602, 12, 90, 32);

        Controls.Add(list);
        Controls.Add(actions);
        Controls.Add(toolbar);
        CancelButton = cancel;
        Activated += (_, _) => pauseKeyboard(true);
        Deactivate += (_, _) => pauseKeyboard(false);
        RefreshList(voices.Count > 0 ? 0 : -1);
        ApplyTheme();
    }

    public SpeechSettings Draft { get; private set; }

    private int SelectedIndex => list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0];

    private void EditVoice(int index)
    {
        var current = index >= 0 ? voices[index] : null;
        using var dialog = new SpeechVoiceEditor(Font, current,
            id => id != current?.Id && voices.Any(voice => voice.Id == id));
        dialog.Activated += (_, _) => pauseKeyboard(true);
        dialog.Deactivate += (_, _) => pauseKeyboard(false);
        editor = dialog;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Choice is not { } updated) return;
            if (current is null)
            {
                index = voices.Count;
                voices.Add(updated);
            }
            else
            {
                ChangeSelectedId(current.Id, updated.Id);
                voices[index] = updated;
            }
            RefreshList(index);
        }
        finally { editor = null; }
    }

    private void ChangeSelectedId(string previous, string replacement)
    {
        Draft = Draft with
        {
            ChineseVoice = Draft.ChineseVoice == previous ? replacement : Draft.ChineseVoice,
            EnglishVoice = Draft.EnglishVoice == previous ? replacement : Draft.EnglishVoice,
            JapaneseVoice = Draft.JapaneseVoice == previous ? replacement : Draft.JapaneseVoice
        };
    }

    private void RefreshList(int selectedIndex)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var voice in voices)
            {
                var names = new List<string>(3);
                if (voice.Languages.HasFlag(SpeechVoiceLanguages.Chinese)) names.Add("中文");
                if (voice.Languages.HasFlag(SpeechVoiceLanguages.English)) names.Add("英文");
                if (voice.Languages.HasFlag(SpeechVoiceLanguages.Japanese)) names.Add("日文");
                list.Items.Add(new ListViewItem([voice.Alias, voice.Id, string.Join("、", names)]));
            }
            if (selectedIndex >= 0)
            {
                list.Items[selectedIndex].Selected = true;
                list.EnsureVisible(selectedIndex);
            }
        }
        finally { list.EndUpdate(); }
        UpdateButtons();
    }

    private void UpdateButtons() => edit.Enabled = remove.Enabled = SelectedIndex >= 0;

    private void ResizeColumns()
    {
        int width = Math.Max(0, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        list.Columns[0].Width = width * 25 / 100;
        list.Columns[1].Width = width * 51 / 100;
        list.Columns[2].Width = width - list.Columns[0].Width - list.Columns[1].Width;
    }

    public void ApplyTheme()
    {
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        list.BackColor = UiTheme.Input;
        list.ForeColor = UiTheme.Ink;
        UiTheme.ApplyToChildren(this);
        if (IsHandleCreated) UiTheme.ApplyCaption(Handle);
        Invalidate(invalidateChildren: true);
        editor?.ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyCaption(Handle);
    }
}
