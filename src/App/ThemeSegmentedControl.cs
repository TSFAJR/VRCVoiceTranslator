using System.ComponentModel;
using System.Drawing.Drawing2D;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class ThemeSegmentedControl : Control
{
    private static readonly (AppearanceMode Mode, string Label, SettingsGlyph Glyph)[] Options =
    [
        (AppearanceMode.Light, "浅色模式", SettingsGlyph.Sun),
        (AppearanceMode.Dark, "深色模式", SettingsGlyph.Moon),
        (AppearanceMode.System, "跟随系统", SettingsGlyph.Monitor)
    ];

    private int selectedIndex = 2;
    private int hoveredIndex = -1;
    private ToolTip? toolTip;

    public ThemeSegmentedControl()
    {
        AccessibleName = "界面外观";
        AccessibleRole = AccessibleRole.Grouping;
        Cursor = Cursors.Hand;
        TabStop = true;
        ResizeRedraw = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.StandardDoubleClick, false);
    }

    public event EventHandler? SelectedModeChanged;

    [DefaultValue(AppearanceMode.System)]
    public AppearanceMode SelectedMode
    {
        get => Options[selectedIndex].Mode;
        set
        {
            int index = Array.FindIndex(Options, option => option.Mode == value);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(value));
            SelectOption(index);
        }
    }

    public void AttachToolTips(ToolTip help)
    {
        toolTip = help;
        help.SetToolTip(this, AccessibleName);
    }

    // Draw the track and all options together so repainting one segment cannot leave child backgrounds behind.
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = 17 * DeviceDpi / 96f;
        UiTheme.Box(g, new(.5f, .5f, Width - 1, Height - 1), Height / 2f, UiTheme.Sidebar, UiTheme.Line);
        for (int i = 0; i < Options.Length; i++)
        {
            var rect = SegmentBounds(i);
            var pill = new RectangleF(rect.X + 1, rect.Y + .5f, rect.Width - 2, rect.Height - 1);
            bool selected = i == selectedIndex;
            bool hovered = i == hoveredIndex;
            if (selected)
                UiTheme.Box(g, pill, pill.Height / 2f, UiTheme.ThemeSelection, Focused && ShowFocusCues ? UiTheme.AccentText : null);
            else if (hovered)
                UiTheme.Box(g, pill, pill.Height / 2f, UiTheme.Track);
            SettingsButton.DrawGlyph(g, Options[i].Glyph, rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2,
                size, selected ? UiTheme.ThemeIconActive : hovered ? UiTheme.Secondary : UiTheme.ThemeIconIdle);
        }
    }

    private Rectangle SegmentBounds(int index)
    {
        int inset = (int)Math.Round(3 * DeviceDpi / 96f);
        int width = Math.Max(0, ClientSize.Width - 2 * inset);
        int left = inset + index * width / Options.Length;
        int right = inset + (index + 1) * width / Options.Length;
        return new Rectangle(left, inset, right - left, Math.Max(0, ClientSize.Height - 2 * inset));
    }

    private int HitSegment(Point point)
    {
        for (int i = 0; i < Options.Length; i++)
            if (SegmentBounds(i).Contains(point)) return i;
        return -1;
    }

    private void SelectOption(int index)
    {
        if (selectedIndex == index) return;
        int previousIndex = selectedIndex;
        selectedIndex = index;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.StateChange, previousIndex);
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        if (Focused) AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        SelectedModeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitSegment(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetHover(-1);
        base.OnMouseLeave(e);
    }

    private void SetHover(int index)
    {
        if (hoveredIndex == index) return;
        hoveredIndex = index;
        toolTip?.SetToolTip(this, index < 0 ? AccessibleName : Options[index].Label);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int index = HitSegment(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
        {
            Focus();
            SelectOption(index);
        }
        base.OnMouseClick(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        int target = keyData switch
        {
            Keys.Left or Keys.Up => (selectedIndex + Options.Length - 1) % Options.Length,
            Keys.Right or Keys.Down => (selectedIndex + 1) % Options.Length,
            Keys.Home => 0,
            Keys.End => Options.Length - 1,
            _ => -1
        };
        if (target < 0) return base.ProcessDialogKey(keyData);
        SelectOption(target);
        return true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        AccessibilityNotifyClients(AccessibleEvents.Focus, selectedIndex);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ThemeAccessibility(this);

    private sealed class ThemeAccessibility(ThemeSegmentedControl owner) : ControlAccessibleObject(owner)
    {
        private readonly OptionAccessibility[] children = Enumerable.Range(0, Options.Length)
            .Select(index => new OptionAccessibility(owner, index)).ToArray();

        public override int GetChildCount() => children.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < children.Length ? children[index] : null;
        public override AccessibleObject? GetFocused() => owner.Focused ? children[owner.selectedIndex] : null;
        public override AccessibleObject GetSelected() => children[owner.selectedIndex];
        public override AccessibleObject? HitTest(int x, int y) => GetChild(owner.HitSegment(owner.PointToClient(new(x, y)))) ?? base.HitTest(x, y);
    }

    private sealed class OptionAccessibility(ThemeSegmentedControl owner, int index) : AccessibleObject
    {
        public override AccessibleObject Parent => owner.AccessibilityObject;
        public override string? Name { get => Options[index].Label; set { } }
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override Rectangle Bounds => owner.RectangleToScreen(owner.SegmentBounds(index));
        public override string DefaultAction => "选择";
        public override AccessibleStates State
        {
            get
            {
                var state = owner.Enabled ? AccessibleStates.Focusable | AccessibleStates.Selectable : AccessibleStates.Unavailable;
                if (!owner.Visible) state |= AccessibleStates.Invisible;
                if (owner.selectedIndex == index)
                {
                    state |= AccessibleStates.Checked | AccessibleStates.Selected;
                    if (owner.Focused) state |= AccessibleStates.Focused;
                }
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (owner.Enabled) owner.SelectOption(index);
        }
    }
}
