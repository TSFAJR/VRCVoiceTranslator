using System.Diagnostics;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class IncomingListener : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly IncomingSpeech speech;
    private int capturing;

    private IncomingListener(int processId, WaitHandle target, Settings settings, BailianClient asr, TranslationClient translator, Action<string> log,
        Func<Settings> currentSettings)
    {
        speech = new(settings, asr, translator, log, currentSettings);
        Work = RunAsync(processId, target, settings);
    }

    public Task Work { get; }
    public string Status => Volatile.Read(ref capturing) == 0 ? "正在启动" : speech.Status;
    public IncomingTranslation? TakeNext() => speech.TakeNext();
    public void Stop() => stop.Cancel();
    public void Dispose() => stop.Dispose();

    public static IncomingListener Start(Settings settings, BailianClient asr, TranslationClient translator, Action<string> log,
        Func<Settings> currentSettings)
    {
        if (!settings.Subtitles.Enabled) throw new ServiceException("请先开启头显字幕并保存设置。");
        if ((BailianEndpoints.ConfigurationError(settings) ?? TranslationClient.ConfigurationError(settings)) is { } error)
            throw new ServiceException(error);

        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName("VRChat");
        ProcessExitHandle? selected = null;
        int selectedId = 0;
        try
        {
            foreach (var process in candidates)
            {
                if (process.SessionId != current.SessionId) continue;
                var handle = ProcessExitHandle.TryOpen(process.Id, "VRChat");
                if (handle is null) continue;
                if (selected is not null && !selected.WaitOne(0))
                {
                    handle.Dispose();
                    throw new ServiceException("检测到多个 VRChat 实例，请只保留一个再开启来话翻译。");
                }
                selected?.Dispose();
                selected = handle;
                selectedId = process.Id;
            }
            if (selected is null) throw new ServiceException("请先启动 VRChat PC 版。");
            var listener = new IncomingListener(selectedId, selected, settings, asr, translator, log, currentSettings);
            selected = null; // RunAsync owns the handle until capture has stopped.
            return listener;
        }
        finally
        {
            selected?.Dispose();
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private async Task RunAsync(int processId, WaitHandle target, Settings settings)
    {
        using (target)
        {
            var cancellation = stop.Token;
            if (target.WaitOne(0)) return;
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var audio = new IncomingAudio(settings.IncomingStrongerNoiseFilter);
            var capture = IncomingLoopbackCapture.RunAsync(processId, settings.IncomingPlaybackDeviceId, audio,
                () => Volatile.Write(ref capturing, 1), cancellation);
            var pipeline = speech.RunAsync(audio, cancellation);
            RegisteredWaitHandle? registration = null;
            try
            {
                registration = ThreadPool.RegisterWaitForSingleObject(target,
                    static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited, Timeout.Infinite, executeOnlyOnce: true);
                var first = await Task.WhenAny(exited.Task, capture, pipeline).ConfigureAwait(false);
                await first.ConfigureAwait(false);
                if (first != exited.Task) cancellation.ThrowIfCancellationRequested();
            }
            finally
            {
                registration?.Unregister(null);
                stop.Cancel();
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
