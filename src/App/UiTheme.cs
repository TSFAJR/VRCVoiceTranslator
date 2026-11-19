using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal static class UiTheme
{
    public const string FontFamily = "Microsoft YaHei UI";
    public const float TextSize = 9.5f;
    public const float HintSize = 8.5f;
    public const float TitleSize = 16f;

    public static Color Canvas => Colors.Canvas;
    public static Color Sidebar => Colors.Sidebar;
    public static Color Surface => Colors.Surface;
    public static Color Input => Colors.Input;
    public static Color Line => Colors.Line;
    public static Color Track => Colors.Track;
    public static Color Rail => Colors.Rail;
    public static Color Teal => Colors.Teal;
    public static Color TealHover => Colors.TealHover;
    public static Color TealTint => Colors.TealTint;
    public static Color Ink => Colors.Ink;
    public static Color Secondary => Colors.Secondary;
    public static Color Muted => Colors.Muted;
    public static Color Amber => Colors.Amber;
    public static Color AmberTint => Colors.AmberTint;
    public static Color OnAccent => Colors.OnAccent;
    public static Color AccentText => Colors.AccentText;
    public static Color ThemeSelection => IsDark ? Rgb(0x1E293B) : Color.White;
    public static Color ThemeIconActive => Rgb(0x14B8A6);
    public static Color ThemeIconIdle => Rgb(0x94A3B8);

    private static readonly Palette Dark = new(
        Canvas: Rgb(0x0F1218),
        Sidebar: Rgb(0x141821),
        Surface: Rgb(0x1B212C),
        Input: Rgb(0x131822),
        Line: Rgb(0x2A3241),
        Track: Rgb(0x232B38),
        Rail: Rgb(0x434D5A),
        Teal: Rgb(0x14B8A6),
        TealHover: Rgb(0x2DD4BF),
        TealTint: Rgb(0x193236),
        Ink: Rgb(0xF1F5F9),
        Secondary: Rgb(0xCBD5E1),
        Muted: Rgb(0x94A3B8),
        Amber: Rgb(0xF8C979),
        AmberTint: Rgb(0x3A3023),
        OnAccent: Rgb(0x0F1218),
        AccentText: Rgb(0x2DD4BF));
    private static readonly Palette Light = new(
        Canvas: Rgb(0xF1F5F9),
        Sidebar: Rgb(0xE8EEF3),
        Surface: Rgb(0xFFFFFF),
        Input: Rgb(0xFAFCFE),
        Line: Rgb(0xCAD5E1),
        Track: Rgb(0xE8EEF5),
        Rail: Rgb(0x8C9CAD),
        Teal: Rgb(0x087E73),
        TealHover: Rgb(0x08695F),
        TealTint: Rgb(0xD9F3EF),
        Ink: Rgb(0x121C2C),
        Secondary: Rgb(0x2C3C50),
        Muted: Rgb(0x52647A),
        Amber: Rgb(0x925016),
        AmberTint: Rgb(0xFCEBD0),
        OnAccent: Rgb(0xFFFFFF),
        AccentText: Rgb(0x06675E));

    private static Palette? current;
    private static Palette Colors => current ?? Dark;
    public static bool IsDark => ReferenceEquals(Colors, Dark);

    public static bool Select(AppearanceMode appearance)
    {
        var next = appearance == AppearanceMode.Dark || appearance == AppearanceMode.System && SystemUsesDarkApps()
            ? Dark : Light;
        if (ReferenceEquals(current, next)) return false;
        current = next;
#pragma warning disable WFO5001
        Application.SetColorMode(IsDark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        return true;
    }

    private static bool SystemUsesDarkApps()
    {
        try
        {
            const string path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            return Registry.GetValue(path, "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void ApplyToChildren(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            switch (child)
            {
                case CardPanel: child.BackColor = Surface; break;
                case LanguageOrder: child.BackColor = Surface; child.ForeColor = Ink; break;
                case DarkComboBox: child.BackColor = Input; child.ForeColor = Ink; break;
                case TextFrame text: text.ApplyTheme(); break;
                case NumberFrame number: number.ApplyTheme(); break;
                case ThemeLabel label: label.ApplyTheme(); break;
            }
            ApplyToChildren(child);
        }
    }

    public static void ApplyCaption(IntPtr handle)
    {
        const int immersiveDarkMode = 20;
        int enabled = IsDark ? 1 : 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
            DwmSetWindowAttribute(handle, immersiveDarkMode, ref enabled, sizeof(int)) != 0 &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985))
        {
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    private static Color Rgb(int value) => Color.FromArgb(unchecked((int)0xFF000000) | value);

    private sealed record Palette(
        Color Canvas, Color Sidebar, Color Surface, Color Input, Color Line, Color Track, Color Rail,
        Color Teal, Color TealHover, Color TealTint, Color Ink, Color Secondary, Color Muted, Color Amber, Color AmberTint,
        Color OnAccent, Color AccentText);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Box(Graphics g, RectangleF bounds, float radius, Color fill, Color? outline = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var shape = Rounded(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, shape);
        if (outline is { } color)
        {
            using var pen = new Pen(color);
            g.DrawPath(pen, shape);
        }
    }
}
