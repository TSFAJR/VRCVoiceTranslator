using System.Diagnostics;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class IncomingListener : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly IncomingSpeech speech;
    private int capturing;

    private IncomingListener(Process target, Settings settings, BailianClient asr, DeepLClient deepL, Action<string> log)
    {
        speech = new(settings, asr, deepL, log);
        Work = RunAsync(target, settings);
    }

    public Task Work { get; }
    public string Status => Volatile.Read(ref capturing) == 0 ? "正在启动" : speech.Status;
    public IncomingTranslation? TakeLatest() => speech.TakeLatest();
    public void Stop() => stop.Cancel();
    public void Dispose() => stop.Dispose();

    public static IncomingListener Start(Settings settings, BailianClient asr, DeepLClient deepL, Action<string> log)
    {
        if (!settings.Subtitles.Enabled) throw new ServiceException("请先开启头显字幕并保存设置。");
        if (string.IsNullOrWhiteSpace(settings.BailianApiKey) || !Settings.IsWebSocketEndpoint(settings.BailianEndpoint) ||
            string.IsNullOrWhiteSpace(settings.DeepLApiKey))
            throw new ServiceException("请先填写百炼地址、百炼 API Key 和 DeepL API Key 并保存设置。");

        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName("VRChat");
        Process? selected = null;
        try
        {
            foreach (var process in candidates)
            {
                if (process.HasExited || process.SessionId != current.SessionId) continue;
                if (selected is not null) throw new ServiceException("检测到多个 VRChat 实例，请只保留一个再开启来话翻译。");
                // Keep this process handle alive for the whole capture so we can watch its lifetime.
                _ = process.SafeHandle;
                if (!process.HasExited) selected = process;
            }
            if (selected is null) throw new ServiceException("请先启动 VRChat PC 版。");
            var listener = new IncomingListener(selected, settings, asr, deepL, log);
            foreach (var candidate in candidates)
                if (candidate != selected) candidate.Dispose();
            return listener;
        }
        catch
        {
            foreach (var candidate in candidates) candidate.Dispose();
            throw;
        }
    }

    private async Task RunAsync(Process target, Settings settings)
    {
        using (target)
        {
            var cancellation = stop.Token;
            var exited = target.WaitForExitAsync(cancellation);
            if (exited.IsCompleted) { await exited.ConfigureAwait(false); return; }
            var audio = new IncomingAudio(settings.IncomingStrongerNoiseFilter);
            var capture = IncomingLoopbackCapture.RunAsync(target.Id, settings.IncomingPlaybackDeviceId, audio,
                () => Volatile.Write(ref capturing, 1), cancellation);
            var pipeline = speech.RunAsync(audio, cancellation);
            var first = await Task.WhenAny(exited, capture, pipeline).ConfigureAwait(false);
            try
            {
                await first.ConfigureAwait(false);
                if (first != exited) cancellation.ThrowIfCancellationRequested();
            }
            finally
            {
                stop.Cancel();
                await ObserveAsync(exited).ConfigureAwait(false);
                await ObserveAsync(capture).ConfigureAwait(false);
                await ObserveAsync(pipeline).ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The first task supplies the outcome. */ }
    }
}
