using System.Diagnostics;
using System.Threading.Channels;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal enum VoicePhase { Idle, Recording, Recognizing, Translating }

internal sealed record CompletedTake(VoiceResult? Result, IReadOnlyList<string> Errors, IReadOnlyList<string> Notices);

internal sealed class RecordingSession : IDisposable
{
    private readonly CancellationTokenSource cancelled = new();
    private readonly CancellationTokenSource finalDeadline = new();
    private readonly TaskCompletionSource<long> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int phase = (int)VoicePhase.Recording;
    public VoicePhase Phase => (VoicePhase)Volatile.Read(ref phase);
    public Settings Settings { get; }
    public Task<CompletedTake> Work { get; }

    public RecordingSession(Settings settings, BailianClient asr, TranslationClient translator, Action<string> log, Action<SpeechSelection> announce)
    {
        Settings = settings;
        Work = Task.Run(() => ProcessAsync(asr, translator, log, announce));
    }

    public void Submit()
    {
        if (released.Task.IsCompleted) return;
        finalDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        Volatile.Write(ref phase, (int)VoicePhase.Recognizing);
        released.TrySetResult(Stopwatch.GetTimestamp());
    }

    public void Cancel() => cancelled.Cancel();

    private async Task<CompletedTake> ProcessAsync(BailianClient asr, TranslationClient translator, Action<string> log, Action<SpeechSelection> announce)
    {
        cancelled.Token.ThrowIfCancellationRequested();
        if (released.Task.IsCompleted) return new(null, [], []);
        if (BailianEndpoints.ConfigurationError(Settings) is { } error) throw new ServiceException(error);
        using var recognitionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, finalDeadline.Token);
        var audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        int limit = checked(Settings.MaxRecordingSeconds * 32000);
        int bytesLeft = limit;
        string original;
        long releasedAt;
        var recognition = asr.RecognizeAsync(Settings, audio.Reader.ReadAllAsync(), recognitionLifetime.Token);
        try
        {
            using (var cue = new RecordingCue())
            using (var microphone = OpenMicrophone((buffer, count) =>
            {
                // The total number of bytes put into this channel is limited per recording.
                int size = Math.Min(count, bytesLeft) & ~1;
                if (size == 0 || cancelled.IsCancellationRequested) return;
                audio.Writer.TryWrite(buffer.AsSpan(0, size).ToArray());
                bytesLeft -= size;
            }))
            {
                bool started = !released.Task.IsCompleted && !cancelled.IsCancellationRequested;
                if (started)
                {
                    StartMicrophone(microphone);
                    if (!released.Task.IsCompleted && !cancelled.IsCancellationRequested) cue.Begin();
                }
                var first = await Task.WhenAny(released.Task, recognition, microphone.Stopped)
                    .WaitAsync(recognitionLifetime.Token).ConfigureAwait(false);
                if (first == recognition) _ = await recognition.ConfigureAwait(false);
                if (first == microphone.Stopped)
                {
                    await WaitForMicrophoneAsync(microphone, recognitionLifetime.Token).ConfigureAwait(false);
                    throw new ServiceException("麦克风采集意外停止。");
                }
                if (!released.Task.IsCompleted) throw new ServiceException("百炼识别意外结束。");
                releasedAt = await released.Task.ConfigureAwait(false);
                if (started)
                {
                    microphone.Stop();
                    await WaitForMicrophoneAsync(microphone, recognitionLifetime.Token).ConfigureAwait(false);
                }
            }
            if (bytesLeft == limit) return new(null, [], []);
            audio.Writer.TryComplete();
            original = await recognition.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (finalDeadline.IsCancellationRequested && !cancelled.IsCancellationRequested)
        { throw new ServiceException("松手后等待百炼最终识别超时。"); }
        finally
        {
            recognitionLifetime.Cancel();
            audio.Writer.TryComplete();
            try { await recognition.ConfigureAwait(false); }
            catch (Exception) { /* The primary failure is reported by the recording task. */ }
        }

        log($"松手或自动提交至最终原文：{Stopwatch.GetElapsedTime(releasedAt).TotalMilliseconds:F0} 毫秒");
        if (string.IsNullOrWhiteSpace(original)) return new(null, [], []);
        cancelled.Token.ThrowIfCancellationRequested();
        Volatile.Write(ref phase, (int)VoicePhase.Translating);
        log("开始翻译。");
        long start = Stopwatch.GetTimestamp();
        var batch = await TakeTranslation.TranslateAsync(translator, original, Settings, announce, cancelled.Token).ConfigureAwait(false);
        log($"原文至译文就绪：{Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} 毫秒，译文 {batch.Results.Count} 项");
        return new(new(original, batch.Results), batch.Errors, batch.Notices);
    }

    private Microphone OpenMicrophone(Action<byte[], int> receiver)
    {
        try { return new Microphone(Settings.MicrophoneId, receiver); }
        catch (ServiceException) { throw; }
        catch (Exception exception) { throw new ServiceException($"打开麦克风失败（0x{exception.HResult:X8}）。"); }
    }

    private static void StartMicrophone(Microphone microphone)
    {
        try { microphone.Start(); }
        catch (Exception exception) { throw new ServiceException($"启动 WASAPI 麦克风失败（0x{exception.HResult:X8}）。"); }
    }

    private static async Task WaitForMicrophoneAsync(Microphone microphone, CancellationToken cancellation)
    {
        try { await microphone.Stopped.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new ServiceException($"麦克风采集失败（0x{exception.HResult:X8}）。"); }
    }

    public void Dispose()
    {
        cancelled.Dispose();
        finalDeadline.Dispose();
    }
}
