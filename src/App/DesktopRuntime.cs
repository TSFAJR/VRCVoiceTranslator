using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed record DesktopStatus(VoicePhase Phase, string Vr, string Error, string ChatboxState, string ChatboxText, VrStatus Binding = default)
{
    public bool SteamVrConnected => Binding.Connected;
    public bool IncomingActive { get; init; }
    public string IncomingStatus { get; init; } = "已关闭";
    public bool SpeechActive { get; init; }
    public string SpeechStatus { get; init; } = "待机";
    public string TranslationNotice { get; init; } = "";
}

internal sealed partial class DesktopRuntime : IDisposable
{
    internal static readonly VoiceResult PreviewExample = new("一起去看海吗？",
        [new("EN-US", "Want to go see the ocean?"), new("JA", "海を見に行かない？")]);
    private static readonly string PreviewTranslations = SubtitlePresentation.Text(PreviewExample, showOriginal: false);
    private static readonly string PreviewWithOriginal = SubtitlePresentation.Text(PreviewExample, showOriginal: true);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource exiting = new();
    private readonly AutoResetEvent controlSignal = new(false);
    private readonly ConcurrentQueue<Action> controls = new();
    private readonly TriggerGate gate = new();
    private readonly SubtitlePresentation subtitles = new();
    private readonly BailianClient asr;
    private readonly BailianTtsClient tts = new();
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        EnableMultipleHttp2Connections = true
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TranslationClient translate;
    private readonly ActivityLog log;
    private readonly SteamVr vr;
    private readonly Thread worker;
    private Settings settings;
    private Settings? observedInput;
    private SubtitleSettings? preview;
    private bool keyboardPaused;
    private bool wasPreviewing;
    private string? lastOverlayError;
    private string? lastTickError;
    private int overlayRetryMs;
    private TimeSpan nextOverlayAttempt;
    private RecordingSession? active;
    private long activeSpeechGeneration;
    private SpeechPlayback? playback;
    private PreparedSocket? preparedSpeech;
    private long preparedSpeechGeneration;
    private readonly List<Task> retiringSpeech = [];
    private long speechGeneration;
    private DesktopStatus status = new(VoicePhase.Idle, "等待 SteamVR", "", "", "");
    private Task? micTest;
    private CancellationTokenSource? micTestStop;
    private IncomingListener? incoming;
    private IncomingListener? stoppingIncoming;
    private int meter;

    public DesktopRuntime(Settings initial, ActivityLog log)
    {
        this.log = log;
        settings = initial;
        asr = new(log: log.Write);
        translate = new(http);
        vr = new(log.Write);
        worker = new Thread(Run) { IsBackground = true, Name = "VRC SteamVR/Input" };
        worker.Start();
    }

    public Settings Current => Volatile.Read(ref settings);
    public DesktopStatus Status => Volatile.Read(ref status);
    public int Meter => Volatile.Read(ref meter);
    public bool MicTestActive => Volatile.Read(ref micTest) is { IsCompleted: false };
    public void Apply(Settings value, bool clearError = false)
    {
        var previous = Interlocked.Exchange(ref settings, value);
        if (clearError) Post(() => SetStatus(s => s with { Error = "" }));
        if (previous.Speech.Enabled && !value.Speech.Enabled) Post(() => InterruptSpeech("自动播报已关闭"));
    }
    public void Preview(SubtitleSettings? value) => Volatile.Write(ref preview, value);
    public void SetKeyboardPaused(bool paused) => Volatile.Write(ref keyboardPaused, paused);
    public void OpenBindings() => Post(() =>
    {
        SetStatus(s => s with { Error = "" });
        vr.OpenBindings();
    });
    public void StopMicTest() => Post(StopTest);
    public void StopSpeech() => Post(() => InterruptSpeech("已停止"));
    public void PreviewSpeech(SpeechSettings options) => Post(() =>
    {
        if (gate.Capturing) { SetSpeechStatus("请在本次录音结束后试听", false); return; }
        SetStatus(s => s with { Error = "" });
        InterruptSpeech();
        StartSpeech(options.PreviewSpeech(), Current with { Speech = options }, Stopwatch.GetTimestamp(), "试听");
    });
    public void SetIncoming(bool enabled, string? playbackDeviceId, bool strongerNoiseFilter) => Post(() =>
    {
        if (enabled) StartIncoming(playbackDeviceId, strongerNoiseFilter);
        else StopIncoming("已关闭");
    });

    public void TestChatbox(int port) => Post(() =>
    {
        SetStatus(s => s with { Error = "", TranslationNotice = "" });
        SendChatbox(() => Chatbox.Format(Chatbox.Example), port);
    });

    public async Task<bool> SendManualChatboxAsync(string text, int port)
    {
        string original = Chatbox.NormalizeManualText(text);
        if (original.Length == 0) return false;
        var cancellation = exiting.Token;
        var snapshot = Current;
        long started = Stopwatch.GetTimestamp();
        TranslationBatch batch;
        long? generation = null;
        try
        {
            generation = snapshot.Speech.Enabled ? await ReserveManualSpeechAsync(snapshot, cancellation).ConfigureAwait(false) : null;
            batch = generation is { } ticket
                ? await TakeTranslation.TranslateAsync(translate, original, snapshot,
                    selection => QueueSpeech(selection, snapshot, ticket), cancellation).ConfigureAwait(false)
                : await translate.TranslateAsync(original, snapshot, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (generation is { } failedTicket) Post(() => DiscardPreparedSpeech(failedTicket));
            batch = new([], [$"翻译请求失败（{error.GetType().Name}）。"]);
        }
        cancellation.ThrowIfCancellationRequested();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                result.TrySetCanceled(cancellation);
                return;
            }
            SetStatus(s => s with { Error = "" });
            if (batch.Errors.Count > 0)
                ReportError(new ServiceException(string.Join("  ", batch.Errors)), "文字翻译");
            ShowTranslationNotices(batch.Notices);
            var outgoing = new VoiceResult(original, batch.Results);
            bool sent = SendChatbox(() => Chatbox.FormatManual(original, batch.Results), port);
            PresentOutgoing(outgoing, Current.Subtitles);
            result.SetResult(sent);
            if (batch.RequestedTermCount > 0)
                log.Write($"手动文字至译文：{elapsed:F0} 毫秒，术语 {batch.RequestedTermCount} 条");
        });
        return await result.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private Task<long?> ReserveManualSpeechAsync(Settings snapshot, CancellationToken cancellation)
    {
        var reservation = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (gate.Capturing || cancellation.IsCancellationRequested || !Current.Speech.Enabled)
            {
                reservation.SetResult(null);
                return;
            }
            InterruptSpeech();
            if (snapshot.Speech.SelectMode().Target is not null) PrepareSpeech(snapshot, speechGeneration);
            reservation.SetResult(speechGeneration);
        });
        return reservation.Task.WaitAsync(cancellation);
    }

    public void TestMicrophone(string? deviceId) => Post(() =>
    {
        if (active is not null) throw new ServiceException("请等本次语音处理完成后再测试麦克风。");
        StopTest();
        if (micTest is { IsCompleted: false }) throw new ServiceException("请等待上一次麦克风测试停止。");
        SetStatus(s => s with { Error = "" });
        micTestStop?.Dispose();
        micTestStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = micTestStop.Token;
        Volatile.Write(ref micTest, Task.Run(async () =>
        {
            try
            {
                using var capture = new Microphone(deviceId, (buffer, count) => Volatile.Write(ref meter, Microphone.Peak(buffer, count)));
                capture.Start();
                await capture.Stopped.WaitAsync(token).ConfigureAwait(false);
                if (!token.IsCancellationRequested) throw new ServiceException("麦克风测试意外停止。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Post(() => ReportError(error, "麦克风测试")); }
            finally { Volatile.Write(ref meter, 0); }
        }));
    });

    private void Post(Action action)
    {
        controls.Enqueue(action);
        try { controlSignal.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Run()
    {
        log.Write("托盘工具已启动。");
        WaitHandle[] wake = [exiting.Token.WaitHandle, controlSignal];
        try
        {
            while (!exiting.IsCancellationRequested)
            {
                while (controls.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception error) { ReportError(error, "设置操作"); }
                }
                try
                {
                    Tick();
                    lastTickError = null;
                }
                catch (Exception error)
                {
                    if (lastTickError != error.Message) ReportError(error, "后台任务");
                    lastTickError = error.Message;
                }
                WaitHandle.WaitAny(wake, 12);
            }
        }
        finally
        {
            StopTest();
            InterruptSpeech();
            StopIncoming("已关闭");
            active?.Cancel();
            WaitForShutdown(micTest);
            WaitForShutdown(active?.Work);
            WaitForShutdown(stoppingIncoming?.Work);
            foreach (var task in retiringSpeech) WaitForShutdown(task);
            retiringSpeech.Clear();
            stoppingIncoming?.Dispose();
            active?.Dispose();
            micTestStop?.Dispose();
            vr.Dispose();
            log.Write("托盘工具已退出。");
        }
    }

    private void Tick()
    {
        var current = Current;
        var inputSettings = gate.Capturing && active is not null ? active.Settings : current;
        if (observedInput?.KeyboardVirtualKey != inputSettings.KeyboardVirtualKey) gate.Reset(TriggerSource.Keyboard);
        if (observedInput?.VrBinding != inputSettings.VrBinding ||
            observedInput?.ExperimentalVrInputOverride != inputSettings.ExperimentalVrInputOverride) gate.Reset(TriggerSource.Vr);
        observedInput = inputSettings;

        var now = clock.Elapsed;
        var vrStatus = vr.Poll(now, inputSettings);
        if (!vrStatus.Connected) subtitles.Clear();
        string vrText = vrStatus.Connected ? "SteamVR 已连接 · " + vrStatus.Text : vrStatus.Text;
        var keyboard = Volatile.Read(ref keyboardPaused) && gate.Owner != TriggerSource.Keyboard
            ? default : TriggerState.Digital((GetAsyncKeyState(inputSettings.KeyboardVirtualKey) & 0x8000) != 0);
        var command = gate.Poll(keyboard, vrStatus.Trigger, now, TimeSpan.FromSeconds(inputSettings.MaxRecordingSeconds));
        switch (command)
        {
            case TriggerCommand.Start:
                StopTest();
                InterruptSpeech();
                subtitles.ClearOutgoing();
                long generation = speechGeneration;
                activeSpeechGeneration = generation;
                PrepareSpeech(inputSettings, generation);
                SetStatus(s => s with { Error = "", TranslationNotice = "" });
                active = new RecordingSession(inputSettings, asr, translate, log.Write,
                    selection => QueueSpeech(selection, inputSettings, generation));
                log.Write(gate.Owner == TriggerSource.Vr ? "手柄触发录音。" : "键盘触发录音。");
                break;
            case TriggerCommand.Submit:
                active?.Submit();
                log.Write("已停止采集，等待最终识别。");
                break;
            case TriggerCommand.Cancel:
                active?.Cancel();
                ReportError(new ServiceException("SteamVR 手柄断开或动作变为不可用，已取消录音。"), "录音");
                break;
        }

        if (active is { Work.IsCompleted: true }) CompleteTake(current);
        UpdateSpeech();
        UpdateIncoming(current.Subtitles, vrStatus.Connected);
        UpdateOverlay(current.Subtitles, vrStatus.Connected);
        var phase = active?.Phase ?? VoicePhase.Idle;
        if (status.Phase != phase && phase == VoicePhase.Idle) log.Write("已恢复待机。");
        if (status.Phase != phase || status.Vr != vrText || status.Binding != vrStatus)
            SetStatus(s => s with { Phase = phase, Vr = vrText, Binding = vrStatus });
    }

    private void PrepareSpeech(Settings snapshot, long generation)
    {
        DiscardPreparedSpeech();
        preparedSpeech = tts.Prepare(snapshot);
        preparedSpeechGeneration = generation;
    }

    private PreparedSocket? TakePreparedSpeech(long generation)
    {
        if (preparedSpeech is null || preparedSpeechGeneration != generation) return null;
        var socket = preparedSpeech;
        preparedSpeech = null;
        return socket;
    }

    private void DiscardPreparedSpeech(long? generation = null)
    {
        if (preparedSpeech is null || (generation is { } target && preparedSpeechGeneration != target)) return;
        preparedSpeech.Dispose();
        preparedSpeech = null;
    }

    private void QueueSpeech(SpeechSelection selection, Settings snapshot, long generation)
    {
        long readyAt = Stopwatch.GetTimestamp();
        Post(() =>
        {
            var prepared = TakePreparedSpeech(generation);
            if (generation != speechGeneration || !Current.Speech.Enabled)
            {
                prepared?.Dispose();
                return;
            }
            if (selection.Text is not null) StartSpeech(selection, snapshot, readyAt, "正式播报", prepared);
            else
            {
                prepared?.Dispose();
                if (selection.SkipReason is { } reason)
                {
                    SetSpeechStatus(reason, false);
                    log.Write(reason);
                }
            }
        });
    }

    private void StartSpeech(SpeechSelection selection, Settings snapshot, long readyAt, string source, PreparedSocket? prepared = null)
    {
        log.Write($"{source} · {snapshot.Speech.SelectMode().Language} · 语气 {selection.Tag?.Label ?? "原声"} · {snapshot.Speech.Rate:0.00}×");
        playback = new SpeechPlayback(tts, selection, snapshot, readyAt, log.Write, prepared);
        SetSpeechStatus("正在合成", true);
    }

    private void InterruptSpeech(string? state = null)
    {
        speechGeneration++;
        DiscardPreparedSpeech();
        if (playback is { } previous)
        {
            playback = null;
            previous.Stop();
            retiringSpeech.Add(DisposeInterruptedAsync(previous));
            state ??= "已停止";
        }
        if (state is not null) SetSpeechStatus(state, false);
    }

    private static async Task DisposeInterruptedAsync(SpeechPlayback previous)
    {
        using (previous)
        {
            try { await previous.Work.ConfigureAwait(false); }
            catch (Exception) { /* Interrupted jobs no longer publish status or audio. */ }
        }
    }

    private void UpdateSpeech()
    {
        retiringSpeech.RemoveAll(static task => task.IsCompleted);
        if (playback is not { } job) return;
        if (!job.Work.IsCompleted) { SetSpeechStatus(job.Status, true); return; }
        playback = null;
        using (job)
        {
            try
            {
                job.Work.GetAwaiter().GetResult();
                SetSpeechStatus(job.CompletionStatus, false);
            }
            catch (Exception error)
            {
                string reason = error is ServiceException ? error.Message : $"{error.GetType().Name} / 0x{error.HResult:X8}";
                SetSpeechStatus($"播报失败 · {reason}", false);
                log.Write($"语音播报：{reason}");
            }
        }
    }

    private void SetSpeechStatus(string detail, bool enabled)
    {
        if (status.SpeechActive != enabled || status.SpeechStatus != detail)
            SetStatus(s => s with { SpeechActive = enabled, SpeechStatus = detail });
    }

    private void StartIncoming(string? playbackDeviceId, bool strongerNoiseFilter)
    {
        if (incoming is not null) return;
        ReapStoppedIncoming();
        if (stoppingIncoming is { Work.IsCompleted: false })
        {
            SetIncomingStatus(false, "正在停止，请稍后开启");
            return;
        }
        try
        {
            var current = Current with { IncomingPlaybackDeviceId = playbackDeviceId, IncomingStrongerNoiseFilter = strongerNoiseFilter };
            incoming = IncomingListener.Start(current, asr, translate, log.Write, () => Current);
            SetIncomingStatus(true, "正在启动");
            log.Write("已开启来话翻译。");
        }
        catch (Exception error) { SetIncomingError(error); }
    }

    private void StopIncoming(string reason)
    {
        if (incoming is not { } listener) return;
        stoppingIncoming = listener;
        incoming = null;
        subtitles.ClearIncoming();
        listener.Stop();
        SetIncomingStatus(false, reason);
        log.Write($"已停止来话翻译（{reason}）。");
    }

    private void ReapStoppedIncoming()
    {
        if (stoppingIncoming is { Work.IsCompleted: true } retired)
        {
            WaitForShutdown(retired.Work);
            retired.Dispose();
            stoppingIncoming = null;
            if (status.IncomingStatus == "正在停止，请稍后开启") SetIncomingStatus(false, "已关闭");
        }
    }

    private void UpdateIncoming(SubtitleSettings current, bool connected)
    {
        ReapStoppedIncoming();
        if (incoming is not { } listener) return;
        if (listener.Work.IsCompleted)
        {
            try
            {
                listener.Work.GetAwaiter().GetResult();
                StopIncoming("已关闭 · VRChat 已退出");
            }
            catch (Exception error)
            {
                StopIncoming("已关闭");
                SetIncomingError(error);
            }
            return;
        }
        if (!current.Enabled) { StopIncoming("已关闭 · 头显字幕已停用"); return; }

        string detail = listener.Status;
        if (!connected) detail += " · SteamVR 未连接";
        SetIncomingStatus(true, detail);
        if (listener.TakeNext() is { } translation && connected)
            subtitles.ReplaceIncoming(translation.Text, clock.Elapsed - Stopwatch.GetElapsedTime(translation.ReadyAt),
                current.DurationSeconds, translation.RecognizedAt);
    }

    private void SetIncomingStatus(bool enabled, string detail)
    {
        if (status.IncomingActive != enabled || status.IncomingStatus != detail)
            SetStatus(s => s with { IncomingActive = enabled, IncomingStatus = detail });
    }

    private void SetIncomingError(Exception error)
    {
        string detail = error is ServiceException ? error.Message : $"{error.GetType().Name} / 0x{error.HResult:X8}";
        SetIncomingStatus(false, $"错误 · {detail}");
        ReportError(error, "来话翻译");
    }

    private void CompleteTake(Settings current)
    {
        using var finished = active!;
        active = null;
        gate.Complete();
        try
        {
            var take = finished.Work.GetAwaiter().GetResult();
            ShowTranslationNotices(take.Notices);
            if (take.Errors.Count > 0)
            {
                string error = string.Join("  ", take.Errors);
                SetStatus(s => s with { Error = error });
                log.Write(error);
            }
            if (take.Result is not { } result)
            {
                DiscardPreparedSpeech(activeSpeechGeneration);
                log.Write("识别结果为空。");
                return;
            }
            if (current.ChatboxEnabled) SendChatbox(() => Chatbox.Format(result), current.OscPort);
            PresentOutgoing(result, current.Subtitles);
        }
        catch (OperationCanceledException) { DiscardPreparedSpeech(activeSpeechGeneration); log.Write("录音已取消。"); }
        catch (Exception error) { DiscardPreparedSpeech(activeSpeechGeneration); ReportError(error, "语音处理"); }
    }

    private void PresentOutgoing(VoiceResult result, SubtitleSettings options)
    {
        if (!options.Enabled || Volatile.Read(ref preview) is not null) return;
        if (vr.Connected) subtitles.Replace(result, clock.Elapsed + TimeSpan.FromSeconds(options.DurationSeconds));
        else if (result.Translations.Count > 0)
            ReportError(new ServiceException("SteamVR 未连接，无法显示本次字幕。"), "VR 字幕");
    }

    private void ShowTranslationNotices(IReadOnlyList<string> notices)
    {
        string message = string.Join("  ", notices);
        SetStatus(s => s with { TranslationNotice = message });
        if (message.Length != 0) log.Write(message);
    }

    private bool SendChatbox(Func<string> format, int port)
    {
        try
        {
            string message = format();
            if (string.IsNullOrWhiteSpace(message)) return false;
            Chatbox.Send(message, port);
            SetStatus(s => s with { ChatboxState = "OSC 已发送（本机）", ChatboxText = message });
            log.Write("OSC 已发送（本机）。");
            return true;
        }
        catch (Exception error)
        {
            SetStatus(s => s with { ChatboxState = "OSC 发送失败" });
            ReportError(error, "Chatbox");
            return false;
        }
    }

    private void UpdateOverlay(SubtitleSettings current, bool connected)
    {
        var draft = Volatile.Read(ref preview);
        if (wasPreviewing != (draft is not null))
        {
            if (draft is not null) subtitles.Clear();
            wasPreviewing = draft is not null;
        }
        if (!connected)
        {
            overlayRetryMs = 0;
            if (draft is not null && lastOverlayError != "请先连接 SteamVR 再预览字幕。")
            {
                lastOverlayError = "请先连接 SteamVR 再预览字幕。";
                ReportError(new ServiceException(lastOverlayError), "VR 字幕");
            }
            else if (draft is null && lastOverlayError is not null)
            {
                ClearOverlayError();
            }
            return;
        }
        var now = clock.Elapsed;
        if (overlayRetryMs > 0 && now < nextOverlayAttempt) return;
        try
        {
            if (overlayRetryMs > 0) vr.ResetOverlay();
            string? text = draft is null ? subtitles.TextAt(clock.Elapsed, current)
                : draft.ShowOriginal ? PreviewWithOriginal : PreviewTranslations;
            if (text is not null)
            {
                vr.Show(text, draft ?? current);
                if (draft is null && subtitles.MarkShown(clock.Elapsed, current.DurationSeconds) is long recognizedAt)
                    log.Write($"来话识别至字幕：{Stopwatch.GetElapsedTime(recognizedAt).TotalMilliseconds:F0} 毫秒");
                if (lastOverlayError is not null)
                {
                    log.Write("SteamVR 字幕已恢复显示。");
                    ClearOverlayError();
                }
            }
            else vr.Hide();
            overlayRetryMs = 0;
        }
        catch (Exception error)
        {
            if (lastOverlayError != error.Message) ReportError(error, "VR 字幕");
            lastOverlayError = error.Message;
            overlayRetryMs = overlayRetryMs == 0 ? 250 : Math.Min(overlayRetryMs * 2, 2000);
            nextOverlayAttempt = clock.Elapsed + TimeSpan.FromMilliseconds(overlayRetryMs);
        }
    }

    private void ClearOverlayError()
    {
        if (lastOverlayError is not null && status.Error == $"VR 字幕：{lastOverlayError}")
            SetStatus(s => s with { Error = "" });
        lastOverlayError = null;
    }

    private void StopTest()
    {
        micTestStop?.Cancel();
        Volatile.Write(ref meter, 0);
    }

    private void ReportError(Exception exception, string operation)
    {
        string detail = exception is ServiceException ? exception.Message : $"{exception.GetType().Name} / 0x{exception.HResult:X8}";
        string message = $"{operation}：{detail}";
        SetStatus(s => s with { Error = message });
        log.Write(message);
    }

    private void SetStatus(Func<DesktopStatus, DesktopStatus> change) => Volatile.Write(ref status, change(status));

    private static void WaitForShutdown(Task? task)
    {
        try { task?.GetAwaiter().GetResult(); }
        catch (Exception) { /* A cancelled or faulted activity is already stopping. */ }
    }

    public void Dispose()
    {
        exiting.Cancel();
        worker.Join();
        controlSignal.Dispose();
        exiting.Dispose();
        http.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
