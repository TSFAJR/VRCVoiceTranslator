using VrcTranslator;

namespace VrcTranslator.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\VRCVoiceTranslator", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("程序已运行。请从系统托盘打开设置。", "VRChat 语音翻译", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localAppData, "VRCVoiceTranslator");
        var store = new SettingsStore(Path.Combine(directory, "config.json"));
        var log = new ActivityLog(Path.Combine(directory, "activity.log"));
        Settings settings;
        try { settings = store.Load(); }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            settings = new();
            string message = error is FormatException ? error.Message : $"{store.Path}{Environment.NewLine}读取配置失败（0x{error.HResult:X8}）。";
            MessageBox.Show(message, "VRChat 语音翻译 · 读取配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            log.Write("配置读取失败，已使用内存默认值。");
        }

        using var iconData = typeof(Program).Assembly.GetManifestResourceStream("VRCVoiceTranslator.AppIcon.ico")!;
        using var icon = new Icon(iconData);
        using var runtime = new DesktopRuntime(settings, log);
        using var tray = new TranslatorTray(store, runtime, icon);
        Application.Run(tray);
    }
}

internal sealed class TranslatorTray : ApplicationContext
{
    private readonly SettingsWindow settings;
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly ContextMenuStrip menu;

    public TranslatorTray(SettingsStore store, DesktopRuntime runtime, Icon icon)
    {
        settings = new(store, runtime, icon);
        trayIcon = new Icon(icon, SystemInformation.SmallIconSize);
        menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "VRChat 语音翻译",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowSettings();
        ShowSettings();
    }

    private void ShowSettings()
    {
        settings.Show();
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        settings.CloseForExit();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            trayIcon.Dispose();
            menu.Dispose();
            settings.Dispose();
        }
        base.Dispose(disposing);
    }
}
