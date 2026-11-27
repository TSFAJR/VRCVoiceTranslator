using System.Diagnostics;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SpeechPlayback : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly PreparedSocket? prepared;
    private readonly SpeechRenderOutput output;
    private readonly SpeechRenderOutput? monitor;
    private string? monitorWarning;

    public SpeechPlayback(BailianTtsClient tts, SpeechSelection selection, Settings settings, long readyAt, Action<string> log,
        PreparedSocket? prepared = null)
    {
        this.prepared = prepared;
        var options = settings.Speech;
        output = new(options.OutputDeviceId, options.VolumePercent, stop.Token,
            () => log($"播报文本就绪至开始播放：{Stopwatch.GetElapsedTime(readyAt).TotalMilliseconds:F0} 毫秒"));
        if (options.MonitorEnabled)
            monitor = new(options.MonitorDeviceId, options.VolumePercent, stop.Token, () => { }, output.Endpoint, queueCapacity: 48);
        Work = Task.Run(() => RunAsync(tts, settings, selection, readyAt, log));
    }

    public Task Work { get; }
    public string Status => WithMonitorWarning(output.Playing || monitor?.Playing == true ? "正在播放" : "正在合成");
    public string CompletionStatus => WithMonitorWarning("播报完成");

    public void Stop()
    {
        stop.Cancel();
        // Do not wait for the network before allowing the next recording.
        output.WaitUntilStopped();
        monitor?.WaitUntilStopped();
    }

    private async Task RunAsync(BailianTtsClient tts, Settings settings, SpeechSelection selection, long readyAt, Action<string> log)
    {
        var monitorDrain = ObserveMonitorAsync(log);
        var synthesis = ProduceAsync(tts, selection, settings, readyAt, log);
        var first = await Task.WhenAny(synthesis, output.Work).ConfigureAwait(false);
        var other = first == synthesis ? output.Work : synthesis;
        try
        {
            await first.ConfigureAwait(false);
            await other.ConfigureAwait(false);
            await monitorDrain.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            try { await other.ConfigureAwait(false); }
            catch (Exception) { /* Preserve the first failure. */ }
            await monitorDrain.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(BailianTtsClient tts, SpeechSelection selection, Settings settings, long readyAt, Action<string> log)
    {
        bool first = true;
        bool monitoring = monitor is not null;
        bool usedPrepared = false;
        await tts.SynthesizeAsync(selection.Text!, settings, async (pcm, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (first)
            {
                first = false;
                log($"播报文本就绪至首个音频块：{Stopwatch.GetElapsedTime(readyAt).TotalMilliseconds:F0} 毫秒{(usedPrepared ? "（预连）" : "")}");
            }

            var block = pcm.ToArray();
            await output.Input.WriteAsync(block, cancellation).ConfigureAwait(false);
            if (monitoring && monitor is { } destination)
            {
                if (destination.Work.IsCompleted) monitoring = false;
                else if (!destination.Input.TryWrite(block))
                {
                    monitoring = false;
                    destination.Input.TryComplete();
                    ReportMonitorWarning("监听跟不上，已停止向监听设备送入后续音频。", log);
                }
            }
        }, stop.Token, selection.Tag, prepared, hit => usedPrepared = hit).ConfigureAwait(false);
        output.Input.TryComplete();
        monitor?.Input.TryComplete();
    }

    private async Task ObserveMonitorAsync(Action<string> log)
    {
        if (monitor is null) return;
        try { await monitor.Work.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (stop.IsCancellationRequested) return;
            string detail = error is ServiceException ? error.Message : $"0x{error.HResult:X8}";
            ReportMonitorWarning($"监听失败 · {detail}", log);
        }
    }

    private void ReportMonitorWarning(string message, Action<string> log)
    {
        Volatile.Write(ref monitorWarning, message);
        log($"语音播报：{message}");
    }

    private string WithMonitorWarning(string state) =>
        Volatile.Read(ref monitorWarning) is { } warning ? $"{state} · {warning}" : state;

    public void Dispose()
    {
        prepared?.Dispose();
        stop.Dispose();
    }
}
