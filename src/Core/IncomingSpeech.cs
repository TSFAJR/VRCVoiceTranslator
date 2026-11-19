using System.Diagnostics;
using System.Threading.Channels;

namespace VrcTranslator;

public sealed record IncomingTranslation(string Text, long RecognizedAt, long ReadyAt);

public sealed class IncomingSpeech(Settings settings, BailianClient asr, DeepLClient deepL, Action<string> log)
{
    private static readonly TimeSpan TranslationDeadline = TimeSpan.FromSeconds(5);
    private readonly Settings cloud = settings with { SourceLanguage = SourceLanguage.Auto, TargetLanguages = ["ZH-HANS"] };
    private readonly Channel<PendingSentence> pending = Channel.CreateBounded<PendingSentence>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    }, _ => log("来话翻译繁忙，已跳过一条等待中的句子。"));
    private IncomingTranslation? latest;
    private string? reconnectReason;
    private string? translationError;
    private string? outcome;

    public string Status => Volatile.Read(ref reconnectReason) is { } reason ? $"正在重连 · {reason}" :
        Volatile.Read(ref translationError) is { } error ? $"翻译错误 · {error}" :
        Volatile.Read(ref outcome) is { } note ? $"正在监听 · {note}" : "正在监听";

    public IncomingTranslation? TakeLatest() => Interlocked.Exchange(ref latest, null);

    public async Task RunAsync(IncomingAudio audio, CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var recognizing = RecognizeAsync(audio, lifetime.Token);
        var translating = TranslateAsync(lifetime.Token);
        var first = await Task.WhenAny(recognizing, translating).ConfigureAwait(false);
        try { await first.ConfigureAwait(false); }
        finally
        {
            lifetime.Cancel();
            try { await (first == recognizing ? translating : recognizing).ConfigureAwait(false); }
            catch (Exception) { /* The first task owns the termination reason. */ }
            Interlocked.Exchange(ref latest, null);
        }
    }

    private async Task RecognizeAsync(IncomingAudio audio, CancellationToken cancellation)
    {
        long capturedSince = 0;
        int consecutiveFailures = 0;
        while (true)
        {
            var utterance = await audio.WaitForSpeechAsync(capturedSince, cancellation).ConfigureAwait(false);
            using var taskLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            taskLifetime.CancelAfter(TimeSpan.FromSeconds(25));
            bool recognized = false;
            string failure;
            try
            {
                await asr.StreamSentencesAsync(cloud, audio.ReadSpeechAsync(utterance, taskLifetime.Token), sentence =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    recognized = true;
                    consecutiveFailures = 0;
                    long recognizedAt = Stopwatch.GetTimestamp();
                    if (sentence.EndMilliseconds is { } end)
                    {
                        double delay = Stopwatch.GetElapsedTime(utterance.StartTimestamp, recognizedAt).TotalMilliseconds - end;
                        log($"来话句末至识别（估算）：{Math.Max(0, delay):F0} 毫秒");
                    }
                    pending.Writer.TryWrite(new(sentence.Text, recognizedAt));
                }, taskLifetime.Token, () => Volatile.Write(ref reconnectReason, null)).ConfigureAwait(false);
                consecutiveFailures = 0;
                if (!recognized) Note("最近一段未识别到完整语句");
                continue;
            }
            catch (BailianConnectionException error) when (!error.CanRetry) { throw new ServiceException(error.Message); }
            catch (BailianConnectionException error) { failure = error.Message; }
            catch (ServiceException error) { failure = error.Message; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && taskLifetime.IsCancellationRequested)
            { failure = "来话识别超时。"; }

            cancellation.ThrowIfCancellationRequested();
            Volatile.Write(ref reconnectReason, failure);
            log($"来话识别：{failure}");
            consecutiveFailures = Math.Min(consecutiveFailures + 1, 4);
            await Task.Delay(TimeSpan.FromSeconds(1 << consecutiveFailures), cancellation).ConfigureAwait(false);
            capturedSince = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 300 / 1000;
        }
    }

    private async Task TranslateAsync(CancellationToken cancellation)
    {
        await foreach (var sentence in pending.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            TimeSpan remaining = TranslationDeadline - Stopwatch.GetElapsedTime(sentence.RecognizedAt);
            if (remaining <= TimeSpan.Zero)
            {
                Note("来话译文已过显示时限");
                continue;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(remaining);
            try
            {
                var batch = await deepL.TranslateAsync(sentence.Text, cloud, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (batch.Errors.Count > 0) { SetTranslationError(batch.Errors[0]); continue; }
                Volatile.Write(ref translationError, null);
                long now = Stopwatch.GetTimestamp();
                if (batch.Results.FirstOrDefault() is not { } translation) Note("最近一句已是中文");
                else if (Stopwatch.GetElapsedTime(sentence.RecognizedAt, now) < TranslationDeadline)
                {
                    Volatile.Write(ref outcome, null);
                    Interlocked.Exchange(ref latest, new(translation.Text, sentence.RecognizedAt, now));
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { SetTranslationError("来话翻译超时。"); }
        }
    }

    private void SetTranslationError(string error)
    {
        if (Interlocked.Exchange(ref translationError, error) != error) log($"来话翻译：{error}");
    }

    private void Note(string message)
    {
        if (Interlocked.Exchange(ref outcome, message) != message) log($"来话：{message}。");
    }

    private readonly record struct PendingSentence(string Text, long RecognizedAt);
}
