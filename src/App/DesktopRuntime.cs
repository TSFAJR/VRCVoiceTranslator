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
}

internal sealed partial class DesktopRuntime : IDisposable
{
    internal static readonly VoiceResult PreviewExample = new("一起去看海吗？",
        [new("EN-US", "Want to go see the ocean?"), new("JA", "海を見に行かない？")]);
    private static readonly string PreviewTranslations = SubtitlePresentation.Text(PreviewExample, showOriginal: false);
    private static readonly string PreviewWithOriginal = SubtitlePresentation.Text(PreviewExample, showOriginal: true);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource exiting = new();
    private readonly ConcurrentQueue<Action> controls = new();
    private readonly TriggerGate gate = new();
    private readonly SubtitlePresentation subtitles = new();
    private readonly BailianClient asr = new();
    private readonly BailianTtsClient tts = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DeepLClient translate;
    private readonly ActivityLog log;
    private readonly SteamVr vr;
    private readonly Thread worker;
    private Settings settings;
    private Settings? observedInput;
    private SubtitleSettings? preview;
    private bool keyboardPaused;
    private bool wasPreviewing;
    private string? lastOverlayError;
    private RecordingSession? active;
    private SpeechPlayback? playback;
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
        translate = new(http);
        vr = new(log.Write);
        worker = new Thread(Run) { IsBackground = true, Name = "VRC SteamVR/Input" };
        worker.Start();
    }

    public Settings Current => Volatile.Read(ref settings);
    public DesktopStatus Status => Volatile.Read(ref status);
    public int Meter => Volatile.Read(ref meter);
    public bool MicTestActive => Volatile.Read(ref micTest) is { IsCompleted: false };
    public void Apply(Settings value)
    {
        var previous = Interlocked.Exchange(ref settings, value);
        if (previous.Speech.Enabled && !value.Speech.Enabled) controls.Enqueue(() => InterruptSpeech("自动播报已关闭"));
    }
    public void Preview(SubtitleSettings? value) => Volatile.Write(ref preview, value);
    public void SetKeyboardPaused(bool paused) => Volatile.Write(ref keyboardPaused, paused);
    public void OpenBindings() => controls.Enqueue(vr.OpenBindings);
    public void StopMicTest() => controls.Enqueue(StopTest);
    public void StopSpeech() => controls.Enqueue(() => InterruptSpeech("已停止"));
    public void PreviewSpeech(SpeechSettings options) => controls.Enqueue(() =>
    {
        if (gate.Capturing) { SetSpeechStatus("请在本次录音结束后试听", false); return; }
        InterruptSpeech();
        StartSpeech(options.SelectMode().SampleText, Current with { Speech = options }, Stopwatch.GetTimestamp());
    });
    public void SetIncoming(bool enabled, string? playbackDeviceId, bool strongerNoiseFilter) => controls.Enqueue(() =>
    {
        if (enabled) StartIncoming(playbackDeviceId, strongerNoiseFilter);
        else StopIncoming("已关闭");
    });

    public void TestChatbox(int port) => controls.Enqueue(() => SendChatbox(() => Chatbox.Format(Chatbox.Example), port));

    public async Task<bool> SendManualChatboxAsync(string text, int port)
    {
        string original = Chatbox.NormalizeManualText(text);
        if (original.Length == 0) return false;
        var cancellation = exiting.Token;
        var snapshot = Current;
        TranslationBatch batch;
        try
        {
            long? generation = snapshot.Speech.Enabled ? await ReserveManualSpeechAsync(cancellation).ConfigureAwait(false) : null;
            batch = generation is { } ticket
                ? await TakeTranslation.TranslateAsync(translate, original, snapshot,
                    selection => QueueSpeech(selection, snapshot, ticket), cancellation).ConfigureAwait(false)
                : await translate.TranslateAsync(original, snapshot, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            batch = new([], [$"翻译请求失败（{error.GetType().Name}）。"]);
        }
        cancellation.ThrowIfCancellationRequested();

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controls.Enqueue(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                result.TrySetCanceled(cancellation);
                return;
            }
            if (batch.Errors.Count > 0)
                ReportError(new ServiceException(string.Join("  ", batch.Errors)), "文字翻译");
            result.SetResult(SendChatbox(() => Chatbox.FormatManual(original, batch.Results), port));
        });
        return await result.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private Task<long?> ReserveManualSpeechAsync(CancellationToken cancellation)
    {
        var reservation = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        controls.Enqueue(() =>
        {
            if (gate.Capturing || cancellation.IsCancellationRequested || !Current.Speech.Enabled)
            {
                reservation.SetResult(null);
                return;
            }
            InterruptSpeech();
            reservation.SetResult(speechGeneration);
        });
        return reservation.Task.WaitAsync(cancellation);
    }

    public void TestMicrophone(string? deviceId) => controls.Enqueue(() =>
    {
        if (active is not null) throw new ServiceException("请等本次语音处理完成后再测试麦克风。");
        StopTest();
        if (micTest is { IsCompleted: false }) throw new ServiceException("请等待上一次麦克风测试停止。");
        micTestStop?.Dispose();
        micTestStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = micTestStop.Token;
        micTest = Task.Run(async () =>
        {
            try
            {
                using var capture = new Microphone(deviceId, (buffer, count) => Volatile.Write(ref meter, Microphone.Peak(buffer, count)));
                capture.Start();
                await capture.Stopped.WaitAsync(token).ConfigureAwait(false);
                if (!token.IsCancellationRequested) throw new ServiceException("麦克风测试意外停止。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { controls.Enqueue(() => ReportError(error, "麦克风测试")); }
            finally { Volatile.Write(ref meter, 0); }
        });
    });

    private void Run()
    {
        log.Write("托盘工具已启动。");
        try
        {
            while (!exiting.IsCancellationRequested)
            {
                while (controls.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception error) { ReportError(error, "设置操作"); }
                }
                try { Tick(); }
                catch (Exception error) { ReportError(error, "后台任务"); }
                exiting.Token.WaitHandle.WaitOne(12);
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
                long generation = speechGeneration;
                SetStatus(s => s with { Error = "" });
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

    private void QueueSpeech(SpeechSelection selection, Settings snapshot, long generation)
    {
        long readyAt = Stopwatch.GetTimestamp();
        controls.Enqueue(() =>
        {
            if (generation != speechGeneration || !Current.Speech.Enabled) return;
            if (selection.Text is { } text) StartSpeech(text, snapshot, readyAt);
            else if (selection.SkipReason is { } reason)
            {
                SetSpeechStatus(reason, false);
                log.Write(reason);
            }
        });
    }

    private void StartSpeech(string text, Settings snapshot, long readyAt)
    {
        playback = new SpeechPlayback(tts, text, snapshot, readyAt, log.Write);
        SetSpeechStatus("正在合成", true);
    }

    private void InterruptSpeech(string? state = null)
    {
        speechGeneration++;
        if (playback is { } previous)
        {
            playback = null;
            previous.Stop();
            _ = DisposeInterruptedAsync(previous);
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
            incoming = IncomingListener.Start(current, asr, translate, log.Write);
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
        var translation = listener.TakeLatest();
        if (connected && translation is not null)
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
            if (take.Errors.Count > 0)
            {
                string error = string.Join("  ", take.Errors);
                SetStatus(s => s with { Error = error });
                log.Write(error);
            }
            if (take.Result is not { } result)
            {
                log.Write("识别结果为空。");
                return;
            }
            if (current.ChatboxEnabled) SendChatbox(() => Chatbox.Format(result), current.OscPort);
            if (current.Subtitles.Enabled && result.Translations.Count > 0 && Volatile.Read(ref preview) is null)
            {
                if (vr.Connected) subtitles.Replace(result);
                else ReportError(new ServiceException("SteamVR 未连接，无法显示本次字幕。"), "VR 字幕");
            }
        }
        catch (OperationCanceledException) { log.Write("录音已取消。"); }
        catch (Exception error) { ReportError(error, "语音处理"); }
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
            if (draft is not null && lastOverlayError != "请先连接 SteamVR 再预览字幕。")
            {
                lastOverlayError = "请先连接 SteamVR 再预览字幕。";
                ReportError(new ServiceException(lastOverlayError), "VR 字幕");
            }
            else if (draft is null) lastOverlayError = null;
            return;
        }
        try
        {
            if (draft is not null) vr.Show(draft.ShowOriginal ? PreviewWithOriginal : PreviewTranslations, draft);
            else if (subtitles.TextAt(clock.Elapsed, current) is { } text)
            {
                vr.Show(text, current);
                if (subtitles.MarkShown(clock.Elapsed, current.DurationSeconds) is long recognizedAt)
                    log.Write($"来话识别至字幕：{Stopwatch.GetElapsedTime(recognizedAt).TotalMilliseconds:F0} 毫秒");
            }
            else vr.Hide();
            lastOverlayError = null;
        }
        catch (Exception error)
        {
            if (lastOverlayError != error.Message) ReportError(error, "VR 字幕");
            lastOverlayError = error.Message;
            subtitles.Clear();
        }
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
        if (status.Error.Contains(message, StringComparison.Ordinal)) return;
        SetStatus(s => s with { Error = s.Error.Length == 0 ? message : s.Error + "  " + message });
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
        exiting.Dispose();
        http.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
