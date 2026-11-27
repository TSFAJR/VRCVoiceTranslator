using System.Diagnostics;
using System.Threading.Channels;

namespace VrcTranslator;

public sealed record IncomingTranslation(string Text, long RecognizedAt, long ReadyAt);

public sealed class IncomingSpeech(Settings settings, BailianClient asr, TranslationClient translator, Action<string> log,
    Func<Settings>? currentSettings = null)
{
    private static readonly TimeSpan TranslationDeadline = TimeSpan.FromSeconds(5);
    private static readonly string[] IncomingTarget = ["ZH-HANS"];
    private Settings CloudSettings() => (currentSettings?.Invoke() ?? settings) with
    {
        SourceLanguage = SourceLanguage.Auto, TargetLanguages = IncomingTarget,
        IncomingStrongerNoiseFilter = settings.IncomingStrongerNoiseFilter
    };
    private readonly Channel<PendingSentence> pending = Channel.CreateBounded<PendingSentence>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    }, _ => log("来话翻译繁忙，已跳过一条等待中的句子。"));
    private readonly Channel<IncomingTranslation> completed = Channel.CreateBounded<IncomingTranslation>(new BoundedChannelOptions(1)
    {
        SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private string? reconnectReason;
    private string? translationError;
    private string? outcome;

    public string Status => Volatile.Read(ref reconnectReason) is { } reason ? $"正在重连 · {reason}" :
        Volatile.Read(ref translationError) is { } error ? $"翻译错误 · {error}" :
        Volatile.Read(ref outcome) is { } note ? $"正在监听 · {note}" : "正在监听";

    public IncomingTranslation? TakeNext() => completed.Reader.TryRead(out var translation) ? translation : null;

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
            while (completed.Reader.TryRead(out _)) { }
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
                var cloud = CloudSettings();
                var audioFrames = audio.ReadSpeechAsync(utterance, cloud.IncomingSentenceSilenceMs, taskLifetime.Token);
                await asr.StreamSentencesAsync(cloud, audioFrames, sentence =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    recognized = true;
                    consecutiveFailures = 0;
                    long recognizedAt = Stopwatch.GetTimestamp();
                    string original = cloud.Terminology.Correct(sentence.Text, out int corrections);
                    if (sentence.EndMilliseconds is { } end)
                    {
                        double delay = Stopwatch.GetElapsedTime(utterance.StartTimestamp, recognizedAt).TotalMilliseconds - end;
                        log($"来话句末至识别（估算）：{Math.Max(0, delay):F0} 毫秒，纠错 {corrections} 次");
                    }
                    else if (corrections > 0) log($"来话识别纠错 {corrections} 次。");
                    pending.Writer.TryWrite(new(original, recognizedAt, cloud));
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
                var batch = await translator.TranslateAsync(sentence.Text, sentence.Settings, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (batch.Errors.Count > 0) { SetTranslationError(batch.Errors[0]); continue; }
                Volatile.Write(ref translationError, null);
                long now = Stopwatch.GetTimestamp();
                if (batch.Results.FirstOrDefault() is not { } translation)
                    Note(batch.Notices.Count > 0 ? "译文与原文相同" : "最近一句已是中文");
                else if (Stopwatch.GetElapsedTime(sentence.RecognizedAt, now) < TranslationDeadline)
                {
                    Volatile.Write(ref outcome, null);
                    completed.Writer.TryWrite(new(translation.Text, sentence.RecognizedAt, now));
                    if (batch.RequestedTermCount > 0)
                        log($"来话识别至译文：{Stopwatch.GetElapsedTime(sentence.RecognizedAt, now).TotalMilliseconds:F0} 毫秒，术语 {batch.RequestedTermCount} 条");
                }
                else Note("来话译文已过显示时限");
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

    private readonly record struct PendingSentence(string Text, long RecognizedAt, Settings Settings);
}
