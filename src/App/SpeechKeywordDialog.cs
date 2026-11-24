using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SpeechKeywordDialog : Form
{
    private readonly DataGridView grid = new()
    {
        AccessibleName = "语气关键词规则", Dock = DockStyle.Fill, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AllowUserToAddRows = false,
        AllowUserToResizeRows = false, EditMode = DataGridViewEditMode.EditOnEnter,
        EnableHeadersVisualStyles = false
    };

    public IReadOnlyList<SpeechKeyword> Rules { get; private set; }

    public SpeechKeywordDialog(Font font, IReadOnlyList<SpeechKeyword> rules)
    {
        Rules = rules;
        Font = font;
        Text = "语气关键词规则";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(610, 390);
        MinimumSize = new Size(525, 330);
        Padding = new Padding(16, 10, 16, 12);

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "关键词或短语", SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "标签", DataSource = SpeechExpression.Tags.ToArray(), DisplayMember = nameof(SpeechTag.DisplayName),
            ValueMember = nameof(SpeechTag.Id), FlatStyle = FlatStyle.Flat
        });
        foreach (var rule in rules) grid.Rows.Add(rule.Phrase, rule.Tag);

        var toolbar = new SettingsPage(578) { Dock = DockStyle.Top, Height = 73 };
        var add = new SettingsButton("添加规则", SettingsButtonStyle.Outline, SettingsGlyph.Plus);
        var remove = new SettingsButton("删除选中行");
        add.Click += (_, _) =>
        {
            int row = grid.Rows.Add("", SpeechExpression.Tags[0].Id);
            grid.CurrentCell = grid[0, row];
            grid.Focus();
            grid.BeginEdit(true);
        };
        remove.Click += (_, _) =>
        {
            if (grid.CurrentRow is { } row)
            {
                grid.CancelEdit();
                grid.Rows.RemoveAt(row.Index);
            }
        };
        toolbar.Place(add, 0, 0, 110, 32);
        toolbar.Place(remove, 120, 0, 120, 32);
        toolbar.Place(new ThemeLabel(LabelTone.Muted)
        {
            Text = "匹配原文中最长的短语；等长时取最先出现的。", TextAlign = ContentAlignment.MiddleLeft
        }, 0, 36, 578, 28);
        var footer = new SettingsPage(578) { Dock = DockStyle.Bottom, Height = 48 };
        var cancel = new SettingsButton("取消") { DialogResult = DialogResult.Cancel };
        var confirm = new SettingsButton("确定", SettingsButtonStyle.Primary);
        confirm.Click += (_, _) => SaveRules();
        footer.Place(new ThemeLabel(LabelTone.Muted)
        {
            Text = "确定后用于下一句，保存设置后记住", TextAlign = ContentAlignment.MiddleLeft
        }, 0, 12, 345, 32);
        footer.Place(cancel, 384, 12, 90, 32);
        footer.Place(confirm, 488, 12, 90, 32);
        Controls.Add(grid);
        Controls.Add(footer);
        Controls.Add(toolbar);
        CancelButton = cancel;
        ApplyTheme();
    }

    private void SaveRules()
    {
        if (!grid.EndEdit()) return;
        try
        {
            Rules = SpeechExpression.ValidateRules(grid.Rows.Cast<DataGridViewRow>().Select(row =>
                new SpeechKeyword(row.Cells[0].Value as string ?? "", row.Cells[1].Value as string ?? "")).ToArray());
            DialogResult = DialogResult.OK;
        }
        catch (FormatException error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void ApplyTheme()
    {
        BackColor = UiTheme.Canvas;
        ForeColor = UiTheme.Ink;
        grid.BackgroundColor = UiTheme.Input;
        grid.GridColor = UiTheme.Line;
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink;
        grid.DefaultCellStyle.BackColor = UiTheme.Input;
        grid.DefaultCellStyle.ForeColor = UiTheme.Ink;
        grid.DefaultCellStyle.SelectionBackColor = UiTheme.TealTint;
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink;
        UiTheme.ApplyToChildren(this);
        if (IsHandleCreated) UiTheme.ApplyCaption(Handle);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyCaption(Handle);
    }
}
