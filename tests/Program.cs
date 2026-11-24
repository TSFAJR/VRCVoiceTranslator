using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VrcTranslator;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Two-hand PTT edge and ownership", TriggerTransitions),
    ("PTT cap, busy edges and disconnect", TriggerRecovery),
    ("Oculus Touch default left trigger and right grip", DefaultTouchBindings),
    ("Message ASR startup buffer, streaming, final events", AsrStreaming),
    ("Message ASR blank and auto language", AsrBlank),
    ("ASR hotword payload respects saved words and the switch", AsrHotwords),
    ("ASR context and final correction reach translation and parallel speech", AsrTerminology),
    ("Message ASR startup / final timeout and service failure", AsrErrors),
    ("Incoming ASR emits each final sentence before finish", IncomingAsrEvents),
    ("Incoming speech keeps the lead, closes on silence and resumes", IncomingSilenceAndResume),
    ("Missing loopback packets and explicit silence produce identical ASR audio", IncomingPacketGap),
    ("Incoming filtering ignores noise spikes and rolls over continuous sound", IncomingNoiseAndLimit),
    ("Incoming speech drops queued older sentences and late results after stop", IncomingQueueAndStop),
    ("DeepL parallel target requests and configured order", DeepLOrdering),
    ("DeepL shared deadline, partial failure and ignored late response", DeepLDeadline),
    ("DeepL total failure and auto language filtering", DeepLFallback),
    ("Qwen-MT provider, regional HTTPS, language mapping and partial errors", QwenMtRequests),
    ("Qwen-MT target callback and translation deadline", QwenMtDeadline),
    ("Incoming translation uses the task snapshot and suppresses repeats", IncomingQwenTranslation),
    ("Translation terms are bounded, matched per target, and omitted for DeepL", TranslationTerminology),
    ("Literal corrections match whole English words and never cascade", TerminologyMatching),
    ("Voice library restores old IDs and filters system voices", SpeechVoiceLibrary),
    ("Speech keyword priority, boundaries, fixed tags and plain mode", SpeechKeywordMatching),
    ("Speech tags stay out of translations, chatbox and subtitles", SpeechTagIsolation),
    ("Live short utterance and preview share the ASMR synthesis instruction", LiveSpeechDirection),
    ("TTS WebSocket modes, event sequence and streaming PCM", TtsProtocol),
    ("TTS service failure, timeout and discarded late PCM", TtsErrorsAndCancel),
    ("Original TTS precedes and survives DeepL failure", SpeechOriginalParallel),
    ("Translated TTS starts on target completion and reports skips", SpeechTranslationSelection),
    ("Chatbox sample via real UDP and Unicode clipping", ChatboxPacket),
    ("Manual Chatbox text preserves line breaks and room for translations", ManualChatboxPacket),
    ("Subtitle replacement, new expiry and empty translation", SubtitleTiming),
    ("Overlay retry preserves fresh subtitles and discards expired ones", SubtitleRetryTiming),
    ("Incoming subtitle keeps the latest complete sentence and respects outgoing priority", IncomingSubtitleTiming),
    ("Settings defaults, save, external edit and malformed preservation", ConfigRoundTrip),
    ("Window resize persists independently and preserves external settings", WindowLayoutPersistence),
    ("Hotword text import validates terms and weights", HotwordImport),
    ("Bailian Workspace ID endpoints and legacy migration", WorkspaceMigration)
};
int failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}\n{error}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static Task DefaultTouchBindings()
{
    using var app = Read("VRCVoiceTranslator.vrmanifest");
    var metadata = app.RootElement.GetProperty("applications")[0];
    using var actions = Read(metadata.GetProperty("action_manifest_path").GetString()!);
    var touch = actions.RootElement.GetProperty("default_bindings").EnumerateArray()
        .Single(profile => profile.GetProperty("controller_type").GetString() == "oculus_touch");
    using var preset = Read(touch.GetProperty("binding_url").GetString()!);
    Equal(metadata.GetProperty("app_key").GetString(), preset.RootElement.GetProperty("app_key").GetString());
    Equal("oculus_touch", preset.RootElement.GetProperty("controller_type").GetString());

    const string actionSet = "/actions/translator";
    string[] buttons = ["/user/hand/left/input/trigger", "/user/hand/right/input/grip"];
    var group = preset.RootElement.GetProperty("bindings").GetProperty(actionSet);
    var chord = group.GetProperty("chords").EnumerateArray().Single();
    Equal(actionSet + "/in/PushToTalk", chord.GetProperty("output").GetString());
    var chordInputs = chord.GetProperty("inputs");
    var sources = group.GetProperty("sources");
    Equal(buttons.Length, chordInputs.GetArrayLength());
    Equal(buttons.Length, sources.GetArrayLength());
    for (int i = 0; i < buttons.Length; i++)
    {
        Equal(buttons[i], chordInputs[i][0].GetString());
        Equal("click", chordInputs[i][1].GetString());
        Equal(buttons[i], sources[i].GetProperty("path").GetString());
        Equal("button", sources[i].GetProperty("mode").GetString());
        Equal(actionSet + "/in/PushToTalk" + (i == 0 ? "A" : "B"),
            sources[i].GetProperty("inputs").GetProperty("click").GetProperty("output").GetString());
    }
    return Task.CompletedTask;

    static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)));
}

static Task TriggerTransitions()
{
    foreach (bool aFirst in new[] { true, false })
    {
        var gate = new TriggerGate();
        TriggerCommand Poll(bool a, bool b, bool key = false) => gate.Poll(TriggerState.Digital(key),
            TriggerState.Pair(true, a, b), TimeSpan.Zero, TimeSpan.FromSeconds(12));
        Equal(TriggerCommand.None, Poll(false, false));
        Equal(TriggerCommand.None, Poll(aFirst, !aFirst));
        Equal(TriggerCommand.Start, Poll(true, true));
        Equal(TriggerSource.Vr, gate.Owner);
        Equal(TriggerCommand.None, Poll(true, true, key: true));
        Equal(TriggerCommand.None, Poll(true, true));
        Equal(TriggerCommand.Submit, Poll(!aFirst, aFirst));
        Equal(TriggerCommand.None, Poll(false, false));
        gate.Complete();
        Equal(TriggerCommand.Start, Poll(false, false, key: true));
        Equal(TriggerSource.Keyboard, gate.Owner);
        Equal(TriggerCommand.None, Poll(true, true, key: true));
        Equal(TriggerCommand.None, Poll(false, false, key: true));
        Equal(TriggerCommand.Submit, Poll(false, false));
    }
    return Task.CompletedTask;
}

static Task TriggerRecovery()
{
    var gate = new TriggerGate();
    TriggerCommand Poll(double seconds, bool key, bool available, bool a, bool b) => gate.Poll(TriggerState.Digital(key),
        TriggerState.Pair(available, a, b), TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(12));
    Equal(TriggerCommand.None, Poll(0, false, true, false, false));
    Equal(TriggerCommand.Start, Poll(1, false, true, true, true));
    Equal(TriggerCommand.None, Poll(12.99, false, true, true, true));
    Equal(TriggerCommand.Submit, Poll(13, false, true, true, true));
    Equal(TriggerCommand.None, Poll(14, true, true, true, true));
    gate.Complete();
    Equal(TriggerCommand.None, Poll(15, true, true, true, true));
    Equal(TriggerCommand.None, Poll(16, false, true, false, false));
    Equal(TriggerCommand.Start, Poll(17, false, true, true, true));
    Equal(TriggerCommand.Cancel, Poll(18, false, false, true, false));
    gate.Complete();
    Equal(TriggerCommand.None, Poll(19, false, true, true, false));
    Equal(TriggerCommand.None, Poll(20, false, true, true, true));
    Equal(TriggerCommand.None, Poll(21, false, true, false, false));
    Equal(TriggerCommand.Start, Poll(22, false, true, true, true));
    Equal(TriggerCommand.Submit, Poll(23, false, true, false, true));
    Equal(TriggerCommand.None, Poll(24, false, false, false, false));
    return Task.CompletedTask;
}

static async Task AsrStreaming()
{
    using var socket = new FakeSocket();
    var incoming = Channel.CreateUnbounded<byte[]>();
    var connection = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new BailianClient((_, cancel) => connection.Task.WaitAsync(cancel));
    var work = client.RecognizeAsync(CloudSettings(), incoming.Reader.ReadAllAsync(), CancellationToken.None);
    byte[] beginning = Enumerable.Range(0, 3200).Select(n => (byte)(n % 251)).ToArray();
    incoming.Writer.TryWrite(beginning);
    connection.SetResult(socket);
    using var run = JsonDocument.Parse(await socket.SentText());
    var header = run.RootElement.GetProperty("header");
    var payload = run.RootElement.GetProperty("payload");
    Equal("run-task", header.GetProperty("action").GetString());
    Equal("duplex", header.GetProperty("streaming").GetString());
    string id = header.GetProperty("task_id").GetString()!;
    True(Guid.TryParse(id, out _));
    Equal(BailianClient.Model, payload.GetProperty("model").GetString());
    var parameters = payload.GetProperty("parameters");
    Equal("pcm", parameters.GetProperty("format").GetString());
    Equal(16000, parameters.GetProperty("sample_rate").GetInt32());
    Equal("zh", parameters.GetProperty("language_hints")[0].GetString());
    True(!parameters.GetProperty("intermediate_result_enabled").GetBoolean());
    True(!parameters.GetProperty("disfluency_removal_enabled").GetBoolean());
    True(!parameters.TryGetProperty("max_sentence_silence", out _));
    True(!parameters.TryGetProperty("speech_noise_threshold", out _));
    await Task.Delay(60);
    True(!socket.HasOutgoing);
    socket.Event("task-started", id, split: true);
    Equal((WebSocketMessageType.Binary, Convert.ToHexString(beginning)), await socket.SentHex());

    socket.Sentence(id, 2, "先收到的最终句。", end: true);
    socket.Sentence(id, 1, "中间结果", end: false);
    socket.Sentence(id, 0, "heartbeat", end: true, heartbeat: true);
    socket.Sentence("other", 9, "另一任务", end: true);
    socket.Sentence(id, 1, "第一句。", end: true);
    socket.Sentence(id, 2, "第二句。", end: true);
    socket.Sentence(id, 3, "  ", end: true);
    incoming.Writer.TryWrite([0xAB, 0xCD, 0x12, 0x34]);
    incoming.Writer.TryComplete();
    Equal((WebSocketMessageType.Binary, "ABCD1234"), await socket.SentHex());
    using var finish = JsonDocument.Parse(await socket.SentText());
    Equal("finish-task", finish.RootElement.GetProperty("header").GetProperty("action").GetString());
    Equal(id, finish.RootElement.GetProperty("header").GetProperty("task_id").GetString());
    True(!work.IsCompleted);
    socket.Event("task-finished", id);
    Equal("第一句。第二句。", await work);
}

static async Task AsrBlank()
{
    using var socket = new FakeSocket();
    var client = new BailianClient((_, _) => Task.FromResult<WebSocket>(socket));
    var work = client.RecognizeAsync(CloudSettings() with { SourceLanguage = SourceLanguage.Auto }, AsyncEnumerable.Empty<byte[]>(), CancellationToken.None);
    using var run = JsonDocument.Parse(await socket.SentText());
    string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    True(!run.RootElement.GetProperty("payload").GetProperty("parameters").TryGetProperty("language_hints", out _));
    socket.Event("task-started", id);
    _ = await socket.SentText();
    socket.Sentence(id, 1, "  ", end: true);
    socket.Event("task-finished", id);
    Equal("", await work);
}

static async Task AsrHotwords()
{
    var configured = CloudSettings() with
    {
        RecognitionHotwords = RecognitionVocabulary.ParseLines(["桔梗", "The Black Cat\t2", "Kipfel\t50"])
    };

    foreach (var settings in new[] { CloudSettings(), configured, configured with { RecognitionHotwordsEnabled = false } })
    {
        using var socket = new FakeSocket();
        var logs = new List<string>();
        var client = new BailianClient((_, _) => Task.FromResult<WebSocket>(socket), log: logs.Add);
        var work = client.RecognizeAsync(settings, AsyncEnumerable.Empty<byte[]>(), CancellationToken.None);
        using var run = JsonDocument.Parse(await socket.SentText());
        string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
        var payload = run.RootElement.GetProperty("payload");
        Equal("{}", payload.GetProperty("input").GetRawText());
        var parameters = payload.GetProperty("parameters");
        bool hasHotwords = parameters.TryGetProperty("vocabulary", out var hotwords);
        Equal(settings.RecognitionHotwordsEnabled && settings.RecognitionHotwords.Count > 0, hasHotwords);
        if (hasHotwords)
        {
            Equal(4, hotwords.GetProperty("桔梗").GetInt32());
            Equal(2, hotwords.GetProperty("The Black Cat").GetInt32());
            Equal(50, hotwords.GetProperty("Kipfel").GetInt32());
        }
        Equal(0, logs.Count);
        socket.Event("task-started", id);
        _ = await socket.SentText();
        socket.Event("task-finished", id);
        Equal("", await work);
        Equal(hasHotwords ? "麦克风识别任务已启动 · 请求附带热词 3 条（加强 1 条），上下文无。"
            : "麦克风识别任务已启动 · 请求附带热词 0 条（加强 0 条），上下文无。", logs.Single());
    }
}

static async Task AsrTerminology()
{
    using var socket = new FakeSocket();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    const string context = "在 VRChat 里，全追是全身追踪的简称。";
    const string corrected = "你用的是什么全追？";
    var settings = CloudSettings() with
    {
        TargetLanguages = ["EN-US"], TranslationProvider = TranslationProvider.QwenMtFlash,
        Speech = new() { Enabled = true },
        Terminology = new(context, [new("什么全锥", "什么全追")], [new("全追", "EN-US", "full-body tracking")])
    };
    int count = 0;
    int connections = 0;
    var events = new List<string>();
    var asr = new BailianClient((_, _) => { connections++; return Task.FromResult<WebSocket>(socket); }, log: events.Add);
    var work = asr.RecognizeAsync(settings, AsyncEnumerable.Empty<byte[]>(), stop.Token, hits => count = hits);
    using var run = JsonDocument.Parse(await socket.SentText());
    string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    var hint = run.RootElement.GetProperty("payload").GetProperty("input").GetProperty("context").EnumerateArray().Single();
    Equal("user", hint.GetProperty("role").GetString());
    var content = hint.GetProperty("content").EnumerateArray().Single();
    Equal("input_text", content.GetProperty("type").GetString());
    Equal(context, content.GetProperty("text").GetString());
    socket.Event("task-started", id);
    using var finish = JsonDocument.Parse(await socket.SentText());
    Equal("finish-task", finish.RootElement.GetProperty("header").GetProperty("action").GetString());
    Equal("{}", finish.RootElement.GetProperty("payload").GetProperty("input").GetRawText());
    socket.Sentence(id, 1, "这条中间结果不应使用", end: false);
    socket.Sentence(id, 2, "全锥？", end: true);
    socket.Sentence(id, 1, "你用的是什么", end: true);
    socket.Event("task-finished", id);
    string original = await work;
    Equal(corrected, original);
    Equal(1, count);
    Equal(1, connections);
    True(events.Single().EndsWith("上下文已附带。") && !events.Single().Contains(context));

    var requests = Channel.CreateUnbounded<string>();
    var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = FakeHttp(async (request, token) =>
    {
        await requests.Writer.WriteAsync(await request.Content!.ReadAsStringAsync(token), token);
        return await pending.Task.WaitAsync(token);
    });
    var speech = Channel.CreateUnbounded<SpeechSelection>();
    var translation = TakeTranslation.TranslateAsync(new(http), original, settings, selection => speech.Writer.TryWrite(selection), stop.Token);
    Equal(new SpeechSelection(corrected, null), await speech.Reader.ReadAsync(stop.Token));
    using var body = JsonDocument.Parse(await requests.Reader.ReadAsync(stop.Token));
    Equal(corrected, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    Equal("full-body tracking", body.RootElement.GetProperty("translation_options").GetProperty("terms")[0].GetProperty("target").GetString());
    True(!translation.IsCompleted);
    pending.SetResult(MtResponse("Which full-body tracking setup do you use?"));
    Equal(1, (await translation).RequestedTermCount);
    True(!requests.Reader.TryRead(out _));
}

static async Task AsrErrors()
{
    using var neverStarts = new FakeSocket();
    var client = new BailianClient((_, _) => Task.FromResult<WebSocket>(neverStarts), TimeSpan.FromMilliseconds(100));
    var timeout = await ThrowsAsync<ServiceException>(() => client.RecognizeAsync(CloudSettings(), AsyncEnumerable.Empty<byte[]>(), CancellationToken.None));
    True(timeout.Message.Contains("启动超时"));

    using var fails = new FakeSocket();
    client = new BailianClient((_, _) => Task.FromResult<WebSocket>(fails));
    var work = client.RecognizeAsync(CloudSettings(), AsyncEnumerable.Empty<byte[]>(), CancellationToken.None);
    string id = await fails.TaskId();
    fails.Event("task-failed", id, code: "UNSUPPORTED_FORMAT");
    True((await ThrowsAsync<ServiceException>(() => work)).Message.Contains("UNSUPPORTED_FORMAT"));

    using var unfinished = new FakeSocket();
    using var cancel = new CancellationTokenSource();
    client = new BailianClient((_, _) => Task.FromResult<WebSocket>(unfinished));
    work = client.RecognizeAsync(CloudSettings(), AsyncEnumerable.Empty<byte[]>(), cancel.Token);
    id = await unfinished.TaskId();
    unfinished.Event("task-started", id);
    _ = await unfinished.SentText();
    cancel.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
}

static async Task IncomingAsrEvents()
{
    using var socket = new FakeSocket();
    using var stop = new CancellationTokenSource();
    var pcm = Channel.CreateUnbounded<byte[]>();
    var results = Channel.CreateUnbounded<RecognizedSentence>();
    var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(socket));
    var work = asr.StreamSentencesAsync(CloudSettings(), pcm.Reader.ReadAllAsync(),
        sentence => results.Writer.TryWrite(sentence), stop.Token);
    string id = await socket.TaskId();
    socket.Event("task-started", id);
    socket.Sentence(id, 1, "intermediate", end: false);
    socket.Sentence(id, 7, "heartbeat", end: true, heartbeat: true);
    socket.Sentence(id, 1, "First sentence", end: true, endTime: 530);
    socket.Sentence(id, 1, "Duplicate", end: true);
    socket.Sentence(id, 3, "Second sentence", end: true);
    socket.Sentence(id, 2, "Stale final", end: true);
    Equal(new RecognizedSentence("First sentence", 530), await results.Reader.ReadAsync());
    Equal(new RecognizedSentence("Second sentence", null), await results.Reader.ReadAsync());
    True(!work.IsCompleted && !socket.HasOutgoing && !results.Reader.TryRead(out _));
    stop.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
}

static async Task IncomingSilenceAndResume()
{
    using var first = new FakeSocket();
    using var second = new FakeSocket();
    using var stop = new CancellationTokenSource();
    using var http = FakeHttp(async (request, token) =>
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Equal("ZH-HANS", json.RootElement.GetProperty("target_lang").GetString());
        True(!json.RootElement.TryGetProperty("source_lang", out _));
        return TranslationResponse("JA", "你好！");
    });
    int connections = 0;
    var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(Interlocked.Increment(ref connections) == 1 ? first : second));
    var logs = new List<string>();
    var current = CloudSettings() with { RecognitionHotwords = RecognitionVocabulary.ParseLines(["桔梗"]), Terminology = new("初始语料") };
    var speech = new IncomingSpeech(current, asr, new TranslationClient(http), logs.Add, () => Volatile.Read(ref current));
    var feed = new IncomingAudio();
    var work = speech.RunAsync(feed, stop.Token);
    var quiet = PcmFrame(35);
    var audible = PcmFrame(2000);
    long frameStep = Stopwatch.Frequency * IncomingAudio.FrameMilliseconds / 1000;
    long capturedAt = Stopwatch.GetTimestamp() - 273 * frameStep;
    void QueuePcm(byte[] pcm, int count)
    {
        for (int i = 0; i < count; i++) feed.Push(pcm, capturedAt += frameStep);
    }
    QueuePcm(quiet, 20);
    True(!first.HasOutgoing);
    QueuePcm(audible, 3);
    // These are contiguous capture frames, even if the simulated translator runs slowly.
    QueuePcm(quiet, 250);
    using var run = JsonDocument.Parse(await first.SentText());
    string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    var parameters = run.RootElement.GetProperty("payload").GetProperty("parameters");
    True(!parameters.TryGetProperty("language_hints", out _));
    Equal(300, parameters.GetProperty("max_sentence_silence").GetInt32());
    True(!parameters.TryGetProperty("speech_noise_threshold", out _));
    Equal(4, parameters.GetProperty("vocabulary").GetProperty("桔梗").GetInt32());
    Equal("初始语料", run.RootElement.GetProperty("payload").GetProperty("input").GetProperty("context")[0]
        .GetProperty("content")[0].GetProperty("text").GetString());
    first.Event("task-started", id);
    string quietPacket = Convert.ToHexString(quiet.Concat(quiet).Concat(quiet).Concat(quiet).Concat(quiet).ToArray());
    for (int i = 0; i < 3; i++) Equal((WebSocketMessageType.Binary, quietPacket), await first.SentHex());
    first.Sentence(id, 1, "こんにちは！", end: true, endTime: 320);
    IncomingTranslation? translated = null;
    while ((translated = speech.TakeNext()) is null) await Task.Delay(10);
    Equal("你好！", translated.Text);
    True(logs.Single().StartsWith("来话句末至识别"));
    byte[] lastVoice = [.. audible, .. audible, .. audible, .. quiet, .. quiet];
    Equal((WebSocketMessageType.Binary, Convert.ToHexString(lastVoice)), await first.SentHex());
    Equal((WebSocketMessageType.Binary, quietPacket), await first.SentHex());
    byte[] filteredTail = [.. quiet, .. new byte[4 * IncomingAudio.FrameBytes]];
    True((await first.SentHex()) == (WebSocketMessageType.Binary, Convert.ToHexString(filteredTail)));
    await FinishIncoming(first, id);
    Volatile.Write(ref current, current with
    {
        RecognitionHotwords = RecognitionVocabulary.ParseLines(["The Black Cat\t3"]),
        IncomingSentenceSilenceMs = 200, Terminology = new("新的语料")
    });
    capturedAt = Stopwatch.GetTimestamp() - 13 * frameStep;
    QueuePcm(quiet, 10);
    QueuePcm(audible, 3);
    using var nextRun = JsonDocument.Parse(await second.SentText());
    string nextId = nextRun.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    var nextParameters = nextRun.RootElement.GetProperty("payload").GetProperty("parameters");
    Equal(200, nextParameters.GetProperty("max_sentence_silence").GetInt32());
    var nextVocabulary = nextParameters.GetProperty("vocabulary");
    Equal(3, nextVocabulary.GetProperty("The Black Cat").GetInt32());
    True(!nextVocabulary.TryGetProperty("桔梗", out _));
    Equal("新的语料", nextRun.RootElement.GetProperty("payload").GetProperty("input").GetProperty("context")[0]
        .GetProperty("content")[0].GetProperty("text").GetString());
    True(nextId != id);
    Equal(2, connections);
    second.Event("task-started", nextId);
    // The Windows endpoint can stop delivering packets during a natural pause.
    Equal((10 + 3 + 250) * IncomingAudio.FrameBytes, await FinishIncoming(second, nextId).WaitAsync(TimeSpan.FromSeconds(7)));
    stop.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
}

static async Task IncomingPacketGap()
{
    var firstVoice = PcmFrame(2000);
    var secondVoice = PcmFrame(4000);
    var silence = PcmFrame(0);
    const int idleFrames = 309; // 6 s plus the 160 ms tail and one frame.
    foreach (string delivery in new[] { "silence packets", "buffered gap", "live gap" })
    {
        using var socket = new FakeSocket();
        using var stop = new CancellationTokenSource();
        using var http = FakeHttp((_, _) => throw new InvalidOperationException("Blank ASR should not translate."));
        var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(socket));
        var speech = new IncomingSpeech(CloudSettings() with { IncomingSentenceSilenceMs = 6000 }, asr, new TranslationClient(http), _ => { });
        var feed = new IncomingAudio();
        long step = Stopwatch.Frequency * IncomingAudio.FrameMilliseconds / 1000;
        long at = Stopwatch.GetTimestamp() - 5 * step;
        void Push(byte[] pcm, int count)
        {
            for (int i = 0; i < count; i++) feed.Push(pcm, at += step);
        }
        Push(firstVoice, 5);
        var work = speech.RunAsync(feed, stop.Token);
        string id = await socket.TaskId();
        socket.Event("task-started", id);
        using var received = new MemoryStream();
        int gapFrames = 75;
        if (delivery == "live gap")
        {
            // Observe actual silence being streamed, past the former 1.2 s cutoff, then resume.
            while (received.Length < (5 + 75) * IncomingAudio.FrameBytes)
            {
                var (type, hex) = await socket.SentHex();
                Equal(WebSocketMessageType.Binary, type);
                received.Write(Convert.FromHexString(hex));
            }
            gapFrames = (int)((Stopwatch.GetTimestamp() - at) / step);
        }
        if (delivery == "silence packets") Push(silence, gapFrames);
        else at += gapFrames * step;
        Push(secondVoice, 5);
        Push(silence, idleFrames);
        while (true)
        {
            var (type, hex) = await socket.SentHex();
            if (type == WebSocketMessageType.Binary) { received.Write(Convert.FromHexString(hex)); continue; }
            using var command = JsonDocument.Parse(Convert.FromHexString(hex));
            Equal("finish-task", command.RootElement.GetProperty("header").GetProperty("action").GetString());
            break;
        }
        var expected = Enumerable.Repeat(firstVoice, 5).Concat(Enumerable.Repeat(silence, gapFrames))
            .Concat(Enumerable.Repeat(secondVoice, 5)).Concat(Enumerable.Repeat(silence, idleFrames))
            .SelectMany(frame => frame).ToArray();
        True(expected.AsSpan().SequenceEqual(received.ToArray()));
        socket.Event("task-finished", id);
        stop.Cancel();
        _ = await ThrowsAsync<OperationCanceledException>(() => work);
    }
}

static async Task IncomingNoiseAndLimit()
{
    using var first = new FakeSocket();
    using var second = new FakeSocket();
    using var stop = new CancellationTokenSource();
    using var http = FakeHttp((_, _) => throw new InvalidOperationException("Blank ASR should not translate."));
    int connections = 0;
    var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(Interlocked.Increment(ref connections) == 1 ? first : second));
    var speech = new IncomingSpeech(CloudSettings() with { IncomingStrongerNoiseFilter = true }, asr, new TranslationClient(http), _ => { });
    var feed = new IncomingAudio(strongerNoiseFilter: true);
    var work = speech.RunAsync(feed, stop.Token);
    var noise = PcmFrame(180);
    var voice = PcmFrame(3000);
    PushIncomingFrames(feed, noise, 20);
    PushIncomingFrames(feed, voice, 2);
    PushIncomingFrames(feed, noise, 2);
    await Task.Delay(30);
    Equal(0, Volatile.Read(ref connections));

    PushIncomingFrames(feed, voice, 3);
    using var run = JsonDocument.Parse(await first.SentText());
    string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    Equal(0.1, run.RootElement.GetProperty("payload").GetProperty("parameters").GetProperty("speech_noise_threshold").GetDouble());
    first.Event("task-started", id);
    PushIncomingFrames(feed, voice, 482);
    Equal(500 * IncomingAudio.FrameBytes, await FinishIncoming(first, id));
    PushIncomingFrames(feed, voice, 3);
    True(await second.TaskId() != id);
    True(speech.Status.Contains("未识别到完整语句"));
    stop.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
}

static async Task IncomingQueueAndStop()
{
    using var first = new FakeSocket();
    using var second = new FakeSocket();
    using var stop = new CancellationTokenSource();
    var requests = Channel.CreateUnbounded<string>();
    var slow = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = FakeHttp(async (request, token) =>
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        string text = json.RootElement.GetProperty("text")[0].GetString()!;
        requests.Writer.TryWrite(text);
        return await (text == "First" ? slow : late).Task;
    });
    int connections = 0;
    var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(++connections == 1 ? first : second));
    var speech = new IncomingSpeech(CloudSettings(), asr, new TranslationClient(http), _ => { });
    var feed = new IncomingAudio();
    var work = speech.RunAsync(feed, stop.Token);
    PushIncomingFrames(feed, PcmFrame(2000), 3);
    string id = await first.TaskId();
    first.Event("task-started", id);
    first.Sentence(id, 1, "First", end: true);
    Equal("First", await requests.Reader.ReadAsync());
    first.Sentence(id, 2, "Old pending", end: true);
    first.Sentence(id, 3, "Newest", end: true);
    var quiet = PcmFrame(0);
    for (int i = 0; i < 250; i++) feed.Push(quiet, Stopwatch.GetTimestamp());
    await FinishIncoming(first, id);
    PushIncomingFrames(feed, PcmFrame(2000), 3);
    _ = await second.TaskId();
    slow.SetResult(TranslationResponse("EN", "第一句"));
    Equal("Newest", await requests.Reader.ReadAsync());
    Equal("第一句", speech.TakeNext()?.Text);
    stop.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
    late.SetResult(TranslationResponse("EN", "迟到的译文"));
    Equal<IncomingTranslation?>(null, speech.TakeNext());
    True(!requests.Reader.TryRead(out _));
}

static void PushIncomingFrames(IncomingAudio feed, byte[] pcm, int count)
{
    for (int i = 0; i < count; i++) feed.Push(pcm, Stopwatch.GetTimestamp());
}

static byte[] PcmFrame(short sample)
{
    var data = new byte[IncomingAudio.FrameBytes];
    for (int i = 0; i < data.Length; i += sizeof(short))
    {
        data[i] = (byte)sample;
        data[i + 1] = (byte)(sample >> 8);
    }
    return data;
}

static async Task<int> FinishIncoming(FakeSocket socket, string id)
{
    int bytes = 0;
    while (true)
    {
        var (type, hex) = await socket.SentHex();
        if (type == WebSocketMessageType.Binary) { bytes += hex.Length / 2; continue; }
        using var json = JsonDocument.Parse(Convert.FromHexString(hex));
        var header = json.RootElement.GetProperty("header");
        Equal("finish-task", header.GetProperty("action").GetString());
        Equal(id, header.GetProperty("task_id").GetString());
        socket.Event("task-finished", id);
        return bytes;
    }
}

static async Task DeepLOrdering()
{
    var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var requested = Channel.CreateUnbounded<string>();
    using var http = FakeHttp(async (request, token) =>
    {
        Equal("https://api-free.deepl.com/v2/translate", request.RequestUri!.AbsoluteUri);
        Equal(HttpMethod.Post, request.Method);
        Equal("DeepL-Auth-Key", request.Headers.Authorization!.Scheme);
        Equal("test:fx", request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Equal("ZH", body.RootElement.GetProperty("source_lang").GetString());
        Equal("原文", body.RootElement.GetProperty("text")[0].GetString());
        string code = body.RootElement.GetProperty("target_lang").GetString()!;
        requested.Writer.TryWrite(code);
        return code == "EN-US" ? await first.Task.WaitAsync(token) : TranslationResponse("ZH", "日本語");
    });
    var work = new TranslationClient(http).TranslateAsync("原文", CloudSettings() with { TargetLanguages = ["EN-US", "ZH-HANS", "JA"] }, CancellationToken.None);
    Equal("EN-US", await requested.Reader.ReadAsync());
    Equal("JA", await requested.Reader.ReadAsync());
    True(!requested.Reader.TryRead(out _));
    first.SetResult(TranslationResponse("ZH", "English"));
    var result = await work;
    Equal("EN-US,JA", string.Join(',', result.Results.Select(r => r.Language)));
    Equal(0, result.Errors.Count);
}

static async Task DeepLDeadline()
{
    var slow = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = FakeHttp(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return body.RootElement.GetProperty("target_lang").GetString() switch
        {
            "EN-US" => TranslationResponse("ZH", "English"),
            "DE" => new(HttpStatusCode.Forbidden),
            _ => await slow.Task
        };
    });
    var result = await new TranslationClient(http, TimeSpan.FromMilliseconds(200)).TranslateAsync("原文",
        CloudSettings() with { TargetLanguages = ["JA", "EN-US", "DE"] }, CancellationToken.None);
    Equal("EN-US", result.Results.Single().Language);
    True(result.Errors[0].Contains("JA") && result.Errors[0].Contains("超时"));
    True(result.Errors[1].Contains("403"));
    slow.SetResult(TranslationResponse("ZH", "遅い"));
    await Task.Delay(30);
    Equal(1, result.Results.Count);
}

static async Task DeepLFallback()
{
    using var http = FakeHttp(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        True(!body.RootElement.TryGetProperty("source_lang", out _));
        return body.RootElement.GetProperty("target_lang").GetString() == "EN-US"
            ? TranslationResponse("EN", "original") : new((HttpStatusCode)456);
    });
    var batch = await new TranslationClient(http).TranslateAsync("original", CloudSettings() with { SourceLanguage = SourceLanguage.Auto }, CancellationToken.None);
    Equal(0, batch.Results.Count);
    Equal(1, batch.Errors.Count);
    True(batch.Errors[0].Contains("456"));
    Equal("original", Chatbox.Format(new("original", batch.Results)));
}

static async Task QwenMtRequests()
{
    const string source = "一起去看海吗？";
    foreach (var (provider, region, regionId, model) in new[]
    {
        (TranslationProvider.QwenMtFlash, BailianRegion.Beijing, "cn-beijing", "qwen-mt-flash"),
        (TranslationProvider.QwenMtLite, BailianRegion.Singapore, "ap-southeast-1", "qwen-mt-lite")
    })
    {
        var requested = Channel.CreateUnbounded<string>();
        using var http = FakeHttp(async (request, cancellation) =>
        {
            Equal(HttpMethod.Post, request.Method);
            Equal($"https://ws-example.{regionId}.maas.aliyuncs.com/compatible-mode/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Equal("Bearer", request.Headers.Authorization?.Scheme);
            Equal("fake", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            var json = body.RootElement;
            Equal(model, json.GetProperty("model").GetString());
            var message = json.GetProperty("messages").EnumerateArray().Single();
            Equal("user", message.GetProperty("role").GetString());
            Equal(source, message.GetProperty("content").GetString());
            var options = json.GetProperty("translation_options");
            Equal("zh", options.GetProperty("source_lang").GetString());
            string target = options.GetProperty("target_lang").GetString()!;
            requested.Writer.TryWrite(target);
            return target switch
            {
                "ja" => new(HttpStatusCode.TooManyRequests),
                "lo" => MtResponse("长译文", "length"),
                _ => MtResponse(" " + target + "  ")
            };
        });
        var settings = CloudSettings() with
        {
            TranslationProvider = provider, BailianRegion = region, DeepLApiKey = "",
            TargetLanguages = ["EN-US", "JA", "ZH-HANS", "ZH-HANT", "LO", "PT-BR"]
        };
        var batch = await new TranslationClient(http).TranslateAsync(source, settings, CancellationToken.None);
        Equal("EN-US:en,ZH-HANT:zh_tw,PT-BR:pt", string.Join(',', batch.Results.Select(item => $"{item.Language}:{item.Text}")));
        True(batch.Errors[0].StartsWith("JA ") && batch.Errors[0].Contains("429"));
        True(batch.Errors[1].StartsWith("LO ") && batch.Errors[1].Contains(provider == TranslationProvider.QwenMtLite ? "不受" : "完整"));
        Equal(0, batch.Notices.Count);
        var sent = new List<string>();
        while (requested.Reader.TryRead(out var target)) sent.Add(target);
        Equal(provider == TranslationProvider.QwenMtLite ? "en,ja,pt,zh_tw" : "en,ja,lo,pt,zh_tw",
            string.Join(',', sent.Order(StringComparer.Ordinal)));
    }
}

static async Task QwenMtDeadline()
{
    var waiting = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = FakeHttp(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return body.RootElement.GetProperty("translation_options").GetProperty("target_lang").GetString() == "ja"
            ? MtResponse("こんにちは") : await waiting.Task;
    });
    var settings = CloudSettings() with
    {
        TranslationProvider = TranslationProvider.QwenMtFlash,
        Speech = new() { Enabled = true, Mode = SpeechMode.JapaneseTranslation }
    };
    var announced = Channel.CreateUnbounded<SpeechSelection>();
    try
    {
        var work = TakeTranslation.TranslateAsync(new(http, TimeSpan.FromSeconds(1)), "你好", settings,
            value => announced.Writer.TryWrite(value), CancellationToken.None);
        Equal(new SpeechSelection("こんにちは", null), await announced.Reader.ReadAsync());
        True(!work.IsCompleted);
        var batch = await work;
        Equal("JA", batch.Results.Single().Language);
        True(batch.Errors.Single().Contains("EN-US 翻译超时"));
        True(!announced.Reader.TryRead(out _));
    }
    finally { waiting.TrySetResult(MtResponse("Hello")); }
}

static async Task IncomingQwenTranslation()
{
    using var socket = new FakeSocket();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    using var http = FakeHttp(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Equal("qwen-mt-lite", body.RootElement.GetProperty("model").GetString());
        var options = body.RootElement.GetProperty("translation_options");
        Equal("auto", options.GetProperty("source_lang").GetString());
        Equal("zh", options.GetProperty("target_lang").GetString());
        string? input = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        if (input == "早上好")
        {
            True(!options.TryGetProperty("terms", out _));
            return MtResponse("早上好");
        }
        Equal("Hello", input);
        Equal("你好", options.GetProperty("terms")[0].GetProperty("target").GetString());
        return MtResponse("你好");
    });
    Settings current = CloudSettings() with
    {
        DeepLApiKey = "", TranslationProvider = TranslationProvider.QwenMtLite,
        Terminology = new(corrections: [new("Hullo", "Hello")], terms: [new("Hello", "ZH-HANS", "你好")])
    };
    var asr = new BailianClient((_, _) => Task.FromResult<WebSocket>(socket));
    var speech = new IncomingSpeech(current, asr, new TranslationClient(http), _ => { }, () => Volatile.Read(ref current));
    var feed = new IncomingAudio();
    var work = speech.RunAsync(feed, stop.Token);
    PushIncomingFrames(feed, PcmFrame(2400), 3);
    string id = await socket.TaskId();
    socket.Event("task-started", id);
    Volatile.Write(ref current, current with { TranslationProvider = TranslationProvider.QwenMtFlash, Terminology = new() });
    socket.Sentence(id, 1, "早上好", end: true);
    while (!speech.Status.Contains("译文与原文相同")) await Task.Delay(10, stop.Token);
    Equal<IncomingTranslation?>(null, speech.TakeNext());
    socket.Sentence(id, 2, "Hullo", end: true);
    IncomingTranslation? translation;
    while ((translation = speech.TakeNext()) is null) await Task.Delay(10, stop.Token);
    Equal("你好", translation.Text);
    stop.Cancel();
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
}

static async Task TranslationTerminology()
{
    var terms = Enumerable.Range(0, 10).Select(index => new TranslationTerm($"术语{index}", "EN-US", $"term {index}")).ToList();
    terms.AddRange([new("全追", "JA", "フルトラ"), new("没出现", "JA", "未使用"), new("全追", "EN-GB", "not US")]);
    string input = "全追" + string.Join(' ', terms.Take(10).Select(term => term.Source));
    foreach (var provider in new[] { TranslationProvider.QwenMtFlash, TranslationProvider.QwenMtLite, TranslationProvider.DeepL })
    {
        using var http = FakeHttp(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (provider == TranslationProvider.DeepL)
            {
                Equal(input, json.RootElement.GetProperty("text")[0].GetString());
                True(!json.RootElement.TryGetProperty("terms", out _));
                Equal(3, json.RootElement.EnumerateObject().Count());
                return TranslationResponse("ZH", "DeepL translation");
            }
            var options = json.RootElement.GetProperty("translation_options");
            var sent = options.GetProperty("terms").EnumerateArray().ToArray();
            if (options.GetProperty("target_lang").GetString() == "ja")
            {
                Equal("全追", sent.Single().GetProperty("source").GetString());
                Equal("フルトラ", sent.Single().GetProperty("target").GetString());
            }
            else
            {
                Equal(8, sent.Length);
                True(sent.All(item => item.GetProperty("source").GetString()!.StartsWith("术语")));
            }
            return MtResponse("Qwen translation");
        });
        var batch = await new TranslationClient(http).TranslateAsync(input,
            CloudSettings() with { Terminology = new(terms: terms), TranslationProvider = provider }, CancellationToken.None);
        Equal(0, batch.Errors.Count);
        Equal(2, batch.Results.Count);
        Equal(provider == TranslationProvider.DeepL ? 0 : 9, batch.RequestedTermCount);
    }
}

static Task TerminologyMatching()
{
    var options = new TerminologySettings("  示例\r\n语料  ",
        [new("全锥", "全追"), new("什么全锥", "哪种全追"), new("全追", "全身追踪"), new("Keepful", "Kipfel")],
        [new("Kipfel", "ja", "キプフェル")]);
    Equal("示例\n语料", options.Context);
    Equal("圆锥、哪种全追、全追、全身追踪。xKeepful Keepful_01，模型Kipfel。",
        options.Correct("圆锥、什么全锥、全锥、全追。xKeepful Keepful_01，模型KEEPFUL。", out int hits));
    Equal(4, hits);
    Equal(0, options.MatchTerms("Kipfel2", "JA").Count);
    Equal(1, options.MatchTerms("Kipfel2、用Kipfel吧", "JA").Count);
    Equal(0, options.MatchTerms("Kipfel", "EN-US").Count);
    Equal(200, new TerminologySettings(string.Concat(Enumerable.Repeat("😀", 200))).Context.EnumerateRunes().Count());
    _ = Throws<FormatException>(() => new TerminologySettings(new string('字', 201)));
    _ = Throws<FormatException>(() => new TerminologySettings(corrections: [new("Kip", "甲"), new("kip", "乙")]));
    _ = Throws<FormatException>(() => new TerminologySettings(terms: [new("相同", "ja", "甲"), new("相同", "JA", "乙")]));
    return Task.CompletedTask;
}

static Task SpeechVoiceLibrary()
{
    var speech = new SpeechSettings
    {
        ChineseVoice = "  my-voice  ",
        JapaneseVoice = "old-japanese",
        VoiceAliases = new Dictionary<string, string>
        {
            [" Abby_v3.1 "] = " 英文女声 ",
            ["spare-voice"] = "备用"
        }
    }.Validate();

    var library = speech.Voices!;
    Equal("my-voice", speech.SelectMode().Voice);
    Equal("", library.Single(voice => voice.Id == "my-voice").Alias);
    Equal("", library.Single(voice => voice.Id == "old-japanese").Alias);
    Equal("英文女声", library.Single(voice => voice.Id == "Abby_v3.1").Alias);
    True(library.All(voice => voice.Id != "abby_v3.1"));
    True(speech.VoiceAliases is null);

    var updated = speech with
    {
        ChineseVoice = "yuxiaoyun_v3.1",
        JapaneseVoice = "spare-voice",
        Voices = library.Where(voice => voice.Id != "yuxiaoyun_v3.1")
            .Select(voice => voice.Id == "spare-voice" ? voice with { Languages = SpeechVoiceLanguages.English } : voice)
            .ToArray()
    };
    updated = updated.Validate();
    Equal("", updated.ChineseVoice);
    Equal("", updated.JapaneseVoice);
    library = updated.Voices!;
    True(library.Any(voice => voice.Id == "my-voice"));
    True(library.All(voice => voice.Id != "yuxiaoyun_v3.1"));

    var chinese = SpeechVoiceCatalog.Choices(SpeechMode.ChineseOriginal, BailianRegion.Beijing, "", library);
    True(chinese.Any(choice => choice.Id == "anmingyuan_v3.1"));
    True(chinese.All(choice => choice.Id != "Abby_v3.1"));
    var english = SpeechVoiceCatalog.Choices(SpeechMode.EnglishTranslation, BailianRegion.Singapore, "", library);
    Equal("英文女声", english.Single(choice => choice.Id == "Abby_v3.1").Alias);
    True(english.Any(choice => choice.Id == "spare-voice"));
    True(english.All(choice => choice.Id != "loongbella_v3.1"));
    var japanese = SpeechVoiceCatalog.Choices(SpeechMode.JapaneseTranslation, BailianRegion.Singapore, "", library);
    True(japanese.Any(choice => choice.Id == SpeechSettings.DefaultJapaneseVoice));
    True(japanese.All(choice => choice.Id != "spare-voice"));
    Equal(0, (updated with { Voices = [] }).Validate().Voices!.Count);
    _ = Throws<FormatException>(() => (updated with
    {
        Voices = [new("invalid", "", SpeechVoiceLanguages.None)]
    }).Validate());
    return Task.CompletedTask;
}

static Task SpeechKeywordMatching()
{
    var options = new SpeechSettings { Keywords = [new("GO", "excited"), new("走吧", "whispers"), new("一起走吧", "giggles")] };
    string Format(string text) => SpeechExpression.Select(options, text, "spoken").Text!;
    Equal("spoken", Format("goal?"));
    Equal("[excited]spoken", Format("goal 再 go!"));
    Equal("[whispers]spoken", Format("走吧，GO"));
    Equal("[giggles]spoken", Format("Go! 走吧，我们一起走吧"));
    Equal("spoken", Format("!?"));
    Equal("spoken", SpeechExpression.Select(options with { Expression = SpeechExpression.Plain }, "走吧", "spoken").Text);
    Equal("[curious]spoken", SpeechExpression.Select(options with { Expression = "curious" }, "走吧", "spoken").Text);
    string full = new('好', BailianTtsClient.MaxTextLength);
    Equal(full, SpeechExpression.Select(options, "走吧", full).Text);
    _ = Throws<FormatException>(() => (options with { Rate = 2.01m }).Validate());
    _ = Throws<FormatException>(() => (options with { Expression = "unlisted" }).Validate());
    _ = Throws<FormatException>(() => SpeechExpression.ValidateRules([new("?!", "excited")]));
    _ = Throws<FormatException>(() => SpeechExpression.ValidateRules([new("Great", "excited"), new(" great ", "sad")]));
    _ = Throws<FormatException>(() => SpeechExpression.ValidateRules([new("great", "unlisted")]));
    return Task.CompletedTask;
}

static async Task SpeechTagIsolation()
{
    const string original = "太好了，终于连上了！";
    foreach (var mode in Enum.GetValues<SpeechMode>())
    {
        var speech = new SpeechSettings { Enabled = true, Mode = mode };
        string target = speech.SelectMode().Target ?? "EN-US";
        string translated = target == "JA" ? "やっとつながった！" : "We're finally connected!";
        int requestCount = 0;
        using var http = FakeHttp(async (request, token) =>
        {
            Interlocked.Increment(ref requestCount);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal(original, json.RootElement.GetProperty("text")[0].GetString());
            return TranslationResponse("ZH", translated);
        });
        var selections = new List<SpeechSelection>();
        var settings = CloudSettings() with { Speech = speech, TargetLanguages = [target] };
        var batch = await TakeTranslation.TranslateAsync(new(http), original, settings, selections.Add, CancellationToken.None);
        Equal(1, requestCount);
        Equal("[excited]" + (mode == SpeechMode.ChineseOriginal ? original : translated), selections.Single().Text);
        var visible = new VoiceResult(original, batch.Results);
        Equal(original + "\n" + translated, SubtitlePresentation.Text(visible, true));
        Equal(original + "\n" + translated, Chatbox.Format(visible));
        Equal(original + "\n" + translated, Chatbox.FormatManual(original, batch.Results));
    }
}

static async Task LiveSpeechDirection()
{
    var settings = CloudSettings() with { TargetLanguages = [], Speech = new() { Enabled = true, Expression = "asmr" } };
    using var http = FakeHttp((_, _) => throw new Exception("No translation targets expected"));
    var selections = new List<SpeechSelection>();
    await TakeTranslation.TranslateAsync(new(http), "中午好。", settings, selections.Add, CancellationToken.None);
    var live = selections.Single();
    Equal("[asmr]中午好。", live.Text);
    Equal(settings.Speech.PreviewSpeech().Tag, live.Tag);

    using var socket = new FakeSocket();
    var client = new BailianTtsClient((_, _) => Task.FromResult<WebSocket>(socket));
    var work = client.SynthesizeAsync(live.Text!, settings, (_, _) => ValueTask.CompletedTask, CancellationToken.None, live.Tag);
    using var run = JsonDocument.Parse(await socket.SentText());
    string instruction = run.RootElement.GetProperty("payload").GetProperty("parameters").GetProperty("instruction").GetString()!;
    True(instruction.Contains("中文") && instruction.Contains("气声"));
    string id = run.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    socket.Event("task-started", id);
    await CheckTtsSend(socket, id, live.Text!);
    socket.Binary([1, 2]);
    socket.Event("task-finished", id);
    await work;
}

static async Task TtsProtocol()
{
    foreach (var (mode, voice, lang, rate, expression, prefix) in new[]
    {
        (SpeechMode.ChineseOriginal, SpeechSettings.DefaultCustomVoice, "zh", 0.50m, SpeechExpression.Plain, ""),
        (SpeechMode.EnglishTranslation, "custom-english", "en", 1m, SpeechExpression.Auto, "[excited]"),
        (SpeechMode.JapaneseTranslation, SpeechSettings.DefaultJapaneseVoice, "ja", 2m, "giggles", "[giggles]")
    })
    {
        using var socket = new FakeSocket();
        var blocks = Channel.CreateUnbounded<byte[]>();
        var options = new SpeechSettings { Mode = mode, EnglishVoice = "custom-english", Rate = rate, Expression = expression };
        var preview = options.PreviewSpeech();
        string original = preview.Text + new string('語', 170);
        Equal(prefix + options.SelectMode().SampleText, preview.Text);
        var tts = new BailianTtsClient((_, _) => Task.FromResult<WebSocket>(socket));
        var work = tts.SynthesizeAsync(original, CloudSettings() with { Speech = options },
            (pcm, token) => blocks.Writer.WriteAsync(pcm.ToArray(), token), CancellationToken.None, preview.Tag);
        using var run = JsonDocument.Parse(await socket.SentText());
        var header = run.RootElement.GetProperty("header");
        var payload = run.RootElement.GetProperty("payload");
        var parameters = payload.GetProperty("parameters");
        string id = header.GetProperty("task_id").GetString()!;
        True(Guid.TryParse(id, out _));
        Equal("run-task", header.GetProperty("action").GetString());
        Equal("duplex", header.GetProperty("streaming").GetString());
        Equal("audio", payload.GetProperty("task_group").GetString());
        Equal("tts", payload.GetProperty("task").GetString());
        Equal("SpeechSynthesizer", payload.GetProperty("function").GetString());
        Equal(BailianTtsClient.Model, payload.GetProperty("model").GetString());
        Equal("PlainText", parameters.GetProperty("text_type").GetString());
        Equal("pcm", parameters.GetProperty("format").GetString());
        Equal(24000, parameters.GetProperty("sample_rate").GetInt32());
        Equal(rate, parameters.GetProperty("rate").GetDecimal());
        Equal(expression == SpeechExpression.Auto, parameters.TryGetProperty("instruction", out var direction));
        if (expression == SpeechExpression.Auto) True(direction.GetString()!.Contains("英语"));
        Equal(voice, parameters.GetProperty("voice").GetString());
        Equal(lang, parameters.GetProperty("language_hints")[0].GetString());
        socket.Event("task-started", "another-task");
        True(!socket.HasOutgoing);
        socket.Event("task-started", id, split: true);
        await CheckTtsSend(socket, id, original);

        socket.Binary([1, 2, 3]);
        Equal("0102", Convert.ToHexString(await blocks.Reader.ReadAsync()));
        True(!work.IsCompleted);
        socket.Event("result-generated", id);
        socket.Binary([4, 5, 6]);
        Equal("03040506", Convert.ToHexString(await blocks.Reader.ReadAsync()));
        socket.Event("task-finished", id);
        await work;
    }
}

static async Task CheckTtsSend(FakeSocket socket, string id, string text)
{
    using var next = JsonDocument.Parse(await socket.SentText());
    Equal("continue-task", next.RootElement.GetProperty("header").GetProperty("action").GetString());
    Equal(id, next.RootElement.GetProperty("header").GetProperty("task_id").GetString());
    Equal(text, next.RootElement.GetProperty("payload").GetProperty("input").GetProperty("text").GetString());
    using var finish = JsonDocument.Parse(await socket.SentText());
    Equal("finish-task", finish.RootElement.GetProperty("header").GetProperty("action").GetString());
    Equal(id, finish.RootElement.GetProperty("header").GetProperty("task_id").GetString());
    True(!finish.RootElement.GetProperty("payload").GetProperty("input").EnumerateObject().Any());
}

static async Task TtsErrorsAndCancel()
{
    using var failed = new FakeSocket();
    var tts = new BailianTtsClient((_, _) => Task.FromResult<WebSocket>(failed));
    var work = tts.SynthesizeAsync("sample", CloudSettings(), (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    string id = await failed.TaskId();
    failed.Event("task-failed", id, code: "InvalidVoice", detail: "voice does not exist");
    var error = await ThrowsAsync<ServiceException>(() => work);
    True(error.Message.Contains("InvalidVoice") && error.Message.Contains("音色 ID"));

    using var timeout = new FakeSocket();
    tts = new((_, _) => Task.FromResult<WebSocket>(timeout), responseTimeout: TimeSpan.FromMilliseconds(200));
    work = tts.SynthesizeAsync("sample", CloudSettings(), (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    id = await timeout.TaskId();
    timeout.Event("task-started", id);
    await CheckTtsSend(timeout, id, "sample");
    True((await ThrowsAsync<ServiceException>(() => work)).Message.Contains("超时"));

    using var late = new FakeSocket(ignoreReceiveCancellation: true);
    using var stop = new CancellationTokenSource();
    var packets = Channel.CreateUnbounded<byte[]>();
    tts = new((_, _) => Task.FromResult<WebSocket>(late));
    work = tts.SynthesizeAsync("sample", CloudSettings(), (pcm, token) => packets.Writer.WriteAsync(pcm.ToArray(), token), stop.Token);
    id = await late.TaskId();
    late.Event("task-started", id);
    await CheckTtsSend(late, id, "sample");
    late.Binary([10, 20]);
    _ = await packets.Reader.ReadAsync();
    stop.Cancel();
    late.Binary([30, 40]);
    _ = await ThrowsAsync<OperationCanceledException>(() => work);
    True(!packets.Reader.TryRead(out _));
    Equal(WebSocketState.Aborted, late.State);
}

static async Task SpeechOriginalParallel()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = FakeHttp((_, token) => blocked.Task.WaitAsync(token));
    var speech = Channel.CreateUnbounded<SpeechSelection>();
    var settings = CloudSettings() with { Speech = new() { Enabled = true } };
    string fullText = new('中', 200);
    var work = TakeTranslation.TranslateAsync(new(http), fullText, settings, selection => speech.Writer.TryWrite(selection), timeout.Token);
    Equal(new SpeechSelection(fullText, null), await speech.Reader.ReadAsync(timeout.Token));
    True(!work.IsCompleted);
    blocked.SetResult(new(HttpStatusCode.Forbidden));
    var result = await work;
    Equal(0, result.Results.Count);
    True(!speech.Reader.TryRead(out _));
    _ = await TakeTranslation.TranslateAsync(new(http), fullText, settings with { Speech = new() },
        _ => throw new InvalidOperationException("Disabled speech published"), timeout.Token);
}

static async Task SpeechTranslationSelection()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var slow = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    string fullText = new('e', 200);
    using var http = FakeHttp(async (request, token) =>
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("target_lang").GetString() == "EN-US"
            ? TranslationResponse("ZH", fullText) : await slow.Task.WaitAsync(token);
    });
    var selected = Channel.CreateUnbounded<SpeechSelection>();
    var settings = CloudSettings() with { Speech = new() { Enabled = true, Mode = SpeechMode.EnglishTranslation } };
    var work = TakeTranslation.TranslateAsync(new(http), "原文", settings, value => selected.Writer.TryWrite(value), timeout.Token);
    Equal(new SpeechSelection(fullText, null), await selected.Reader.ReadAsync(timeout.Token));
    True(!work.IsCompleted);
    slow.SetResult(new(HttpStatusCode.Forbidden));
    Equal("EN-US", (await work).Results.Single().Language);
    True(!selected.Reader.TryRead(out _));

    var japanese = settings with { Speech = settings.Speech with { Mode = SpeechMode.JapaneseTranslation } };
    await TakeTranslation.TranslateAsync(new(http), "原文", japanese, value => selected.Writer.TryWrite(value), timeout.Token);
    True((await selected.Reader.ReadAsync(timeout.Token)).SkipReason!.Contains("403"));
    await TakeTranslation.TranslateAsync(new(http), "原文", japanese with { TargetLanguages = ["EN-US"] },
        value => selected.Writer.TryWrite(value), timeout.Token);
    True((await selected.Reader.ReadAsync(timeout.Token)).SkipReason!.Contains("未配置 JA"));
    True(!selected.Reader.TryRead(out _));
}

static async Task ChatboxPacket()
{
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string sample = Chatbox.Format(Chatbox.Example);
    Chatbox.Send(sample, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var datagram = (await receiver.ReceiveAsync(deadline.Token)).Buffer;
    int offset = 0;
    Equal("/chatbox/input", ReadOsc(datagram, ref offset));
    Equal(",sTF", ReadOsc(datagram, ref offset));
    Equal("今晚要不要一起去看海？\nWant to go see the ocean together tonight?\n今夜、一緒に海を見に行かない？", ReadOsc(datagram, ref offset));
    Equal(offset, datagram.Length);

    const string family = "👩‍👩‍👧‍👦";
    string repeated = string.Concat(Enumerable.Repeat(family, 100));
    var many = new VoiceResult(repeated, Enumerable.Range(0, 14).Select(index => new Translation("JA", index + " 語\n文" + repeated)).ToArray());
    string clipped = Chatbox.Format(many);
    True(clipped.Length <= 144);
    Equal(9, clipped.Split('\n').Length);
    True(clipped.Split('\n')[8].StartsWith("7 "));
    True(clipped.Contains('…'));
    var elements = StringInfo.GetTextElementEnumerator(clipped);
    while (elements.MoveNext())
        if (elements.GetTextElement().Any(char.IsSurrogate)) Equal(family, elements.GetTextElement());
    Equal("a b c\nd", Chatbox.Format(new("a\r\nb\0\tc", [new("JA", "d")])));
    var nineLines = new VoiceResult(new string('中', 100), Enumerable.Repeat(new Translation("JA", "短"), 8).ToArray());
    string[] visibleBudget = Chatbox.Format(nineLines).Split('\n');
    Equal(9, visibleBudget.Length);
    True(visibleBudget[0].Length <= 16 && visibleBudget[0].EndsWith('…'));
}

static async Task ManualChatboxPacket()
{
    Equal("", Chatbox.FormatManual(" \t\r\n\0"));
    const string draft = "你好 VRChat！\r\n\r\nSee  you 👋";
    const string expected = "你好 VRChat！\n\nSee  you 👋";
    string message = Chatbox.FormatManual(draft);
    Equal(expected, message);
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    Chatbox.Send(message, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var datagram = (await receiver.ReceiveAsync(deadline.Token)).Buffer;
    int offset = 0;
    Equal("/chatbox/input", ReadOsc(datagram, ref offset));
    Equal(",sTF", ReadOsc(datagram, ref offset));
    Equal(expected, ReadOsc(datagram, ref offset));
    Equal(datagram.Length, offset);

    Translation[] translations = [new("JA", "こんばんは！"), new("EN-US", "Good evening!")];
    Equal(expected + "\nこんばんは！\nGood evening!", Chatbox.FormatManual(draft, translations));
    var manyOriginalLines = Chatbox.FormatManual(string.Join('\n', Enumerable.Repeat("好", 15)), translations).Split('\n');
    Equal(Chatbox.MaxLines, manyOriginalLines.Length);
    Equal("こんばんは！", manyOriginalLines[^2]);
    Equal("Good evening!", manyOriginalLines[^1]);

    const string emoji = "👩‍👩‍👧‍👦";
    string longLine = string.Concat(Enumerable.Repeat(emoji, 40));
    string clipped = Chatbox.FormatManual(longLine + "\n" + string.Join('\n', Enumerable.Repeat("短", 15)));
    True(clipped.Length <= Chatbox.MaxCharacters);
    Equal(Chatbox.MaxLines, clipped.Split('\n').Length);
    True(clipped.Contains('…'));
    var glyphs = StringInfo.GetTextElementEnumerator(clipped);
    while (glyphs.MoveNext())
        if (glyphs.GetTextElement().Any(char.IsSurrogate)) Equal(emoji, glyphs.GetTextElement());
}

static Task SubtitleTiming()
{
    var timer = new SubtitlePresentation();
    var settings = new SubtitleSettings();
    timer.Replace(Chatbox.Example);
    True(timer.TextAt(TimeSpan.Zero, settings)!.StartsWith("Want"));
    timer.MarkShown(TimeSpan.Zero, 3);
    timer.Replace(new("新原文", [new("EN-US", "New subtitle") ]));
    timer.MarkShown(TimeSpan.FromSeconds(2), 3);
    Equal("New subtitle", timer.TextAt(TimeSpan.FromSeconds(3), settings));
    Equal("新原文\nNew subtitle", timer.TextAt(TimeSpan.FromSeconds(4.99), settings with { ShowOriginal = true }));
    timer.Replace(new("空译文", []));
    Equal<string?>(null, timer.TextAt(TimeSpan.FromSeconds(4.99), settings));
    Equal<string?>(null, timer.TextAt(TimeSpan.FromSeconds(5), settings));
    timer.Replace(Chatbox.Example);
    True(timer.TextAt(TimeSpan.FromSeconds(8), settings) is not null);
    return Task.CompletedTask;
}

static Task SubtitleRetryTiming()
{
    var subtitles = new SubtitlePresentation();
    var settings = new SubtitleSettings();
    subtitles.Replace(Chatbox.Example, TimeSpan.FromSeconds(3));
    subtitles.ReplaceIncoming("等待中", TimeSpan.Zero, 3, 1);
    True(subtitles.TextAt(TimeSpan.Zero, settings)!.StartsWith("Want"));
    subtitles.ReplaceIncoming("最新", TimeSpan.FromSeconds(2), 3, 2);
    // The failed outgoing submission was never marked shown. Its expiry releases incoming priority.
    Equal("对方：最新", subtitles.TextAt(TimeSpan.FromSeconds(3), settings));
    Equal<long?>(2, subtitles.MarkShown(TimeSpan.FromSeconds(3), 3));
    Equal<string?>(null, subtitles.TextAt(TimeSpan.FromSeconds(5), settings));
    subtitles.Replace(Chatbox.Example, TimeSpan.FromSeconds(9));
    True(subtitles.TextAt(TimeSpan.FromSeconds(8), settings) is not null);
    subtitles.MarkShown(TimeSpan.FromSeconds(8), 3);
    True(subtitles.TextAt(TimeSpan.FromSeconds(10), settings) is not null);
    Equal<string?>(null, subtitles.TextAt(TimeSpan.FromSeconds(11), settings));
    return Task.CompletedTask;
}

static Task IncomingSubtitleTiming()
{
    var subtitles = new SubtitlePresentation();
    var settings = new SubtitleSettings { ShowOriginal = true };
    subtitles.Replace(new("自己的原文", [new("EN-US", "My subtitle")]));
    Equal("自己的原文\nMy subtitle", subtitles.TextAt(TimeSpan.Zero, settings));
    Equal<long?>(null, subtitles.MarkShown(TimeSpan.Zero, 3));
    subtitles.ReplaceIncoming("较早的一句", TimeSpan.FromSeconds(2), 3, 22);
    string longSentence = string.Concat(Enumerable.Repeat("这是一句很长的来话字幕，", 20)) + "\r\n结尾也要保留。";
    subtitles.ReplaceIncoming(longSentence, TimeSpan.FromSeconds(2.5), 3, 23);
    Equal("自己的原文\nMy subtitle", subtitles.TextAt(TimeSpan.FromSeconds(2.9), settings));
    Equal("对方：" + longSentence.ReplaceLineEndings("\n"), subtitles.TextAt(TimeSpan.FromSeconds(3), settings));
    Equal<long?>(23, subtitles.MarkShown(TimeSpan.FromSeconds(3), 3));
    Equal<long?>(null, subtitles.MarkShown(TimeSpan.FromSeconds(3.1), 3));
    subtitles.ReplaceIncoming("又来了一句", TimeSpan.FromSeconds(3.5), 1.5, 24);
    Equal("对方：又来了一句", subtitles.TextAt(TimeSpan.FromSeconds(3.5), settings));
    Equal<long?>(24, subtitles.MarkShown(TimeSpan.FromSeconds(3.5), 1.5));
    subtitles.ReplaceIncoming(" \r\n ", TimeSpan.FromSeconds(4), 2, 25);
    Equal("对方：又来了一句", subtitles.TextAt(TimeSpan.FromSeconds(4.1), settings));
    Equal<string?>(null, subtitles.TextAt(TimeSpan.FromSeconds(5), settings));

    subtitles.Replace(new("原文", [new("EN-US", "Longer subtitle")]));
    subtitles.MarkShown(TimeSpan.FromSeconds(6), 3);
    subtitles.ReplaceIncoming("已过期", TimeSpan.FromSeconds(6), 2, 22);
    Equal<string?>(null, subtitles.TextAt(TimeSpan.FromSeconds(9), settings));
    subtitles.ReplaceIncoming("被清除", TimeSpan.FromSeconds(10), 3, 23);
    subtitles.ClearIncoming();
    Equal<string?>(null, subtitles.TextAt(TimeSpan.FromSeconds(11), settings));
    return Task.CompletedTask;
}

static Task HotwordImport()
{
    var vocabulary = RecognitionVocabulary.ParseLines(["  桔梗  ", "", "The Black Cat\t2", "the black cat\t3"]);
    Equal(2, vocabulary.Count);
    Equal(4, vocabulary["桔梗"]);
    Equal(3, vocabulary["The Black Cat"]);
    Equal(1, RecognitionVocabulary.ParseLines([new string('地', 15)]).Count);
    Equal(1, RecognitionVocabulary.ParseLines(["one two three four five six seven"]).Count);
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines([new string('地', 16)]));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(["one two three four five six seven eight"]));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(["模型\t0"]));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(["模型\t6"]));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(["模型\t"]));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(["模型\t4\t3"]));
    Equal(50, RecognitionVocabulary.ParseLines(["Kipfel\t50"])["Kipfel"]);
    var strongTerms = Enumerable.Range(0, RecognitionVocabulary.MaxStrongTerms).Select(i => $"Avatar {i}\t50").ToArray();
    Equal(RecognitionVocabulary.MaxStrongTerms, RecognitionVocabulary.ParseLines(strongTerms.Append("avatar 0\t50")).Count);
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(strongTerms.Append("Kipfel\t50")));
    _ = Throws<FormatException>(() => RecognitionVocabulary.ParseLines(Enumerable.Range(0, RecognitionVocabulary.MaxTerms + 1)
        .Select(i => $"VRChat {i}")));
    return Task.CompletedTask;
}

static Task WindowLayoutPersistence()
{
    var directory = Directory.CreateTempSubdirectory("vrc-window-");
    var store = new SettingsStore(Path.Combine(directory.FullName, "config.json"));
    try
    {
        File.WriteAllText(store.Path, """{"oscPort":9015,"future":{"example":1}}""");
        Equal<WindowLayout?>(null, store.Load().Window);
        var layout = new WindowLayout(991, 651, true);
        store.SaveWindow(layout);
        Equal(layout, store.Load().Window);
        Equal(9015, store.Load().OscPort);
        using var saved = JsonDocument.Parse(File.ReadAllText(store.Path));
        Equal(1, saved.RootElement.GetProperty("future").GetProperty("example").GetInt32());
        const string invalid = """{"oscPort":-2}""";
        File.WriteAllText(store.Path, invalid);
        _ = Throws<FormatException>(() => store.SaveWindow(layout));
        Equal(invalid, File.ReadAllText(store.Path));
    }
    finally { File.Delete(store.Path); directory.Delete(); }
    return Task.CompletedTask;
}

static Task ConfigRoundTrip()
{
    string directory = Path.Combine(Path.GetTempPath(), "vrc-tests-" + Guid.NewGuid().ToString("N"));
    var store = new SettingsStore(Path.Combine(directory, "config.json"));
    try
    {
        Equal("", store.Load().BailianApiKey);
        var settings = store.Load();
        Equal(AppearanceMode.System, settings.Appearance);
        Equal(TranslationProvider.DeepL, settings.TranslationProvider);
        Equal(BailianRegion.Beijing, settings.BailianRegion);
        Equal("", settings.BailianWorkspaceId);
        Equal("", settings.DeepLApiKey);
        True(!settings.Speech.Enabled);
        Equal(SpeechMode.ChineseOriginal, settings.Speech.Mode);
        Equal(SpeechSettings.DefaultCustomVoice, settings.Speech.ChineseVoice);
        Equal(SpeechSettings.DefaultCustomVoice, settings.Speech.EnglishVoice);
        Equal(SpeechSettings.DefaultJapaneseVoice, settings.Speech.JapaneseVoice);
        Equal<string?>(null, settings.Speech.OutputDeviceId);
        True(!settings.Speech.MonitorEnabled);
        Equal<string?>(null, settings.Speech.MonitorDeviceId);
        Equal<string?>(null, settings.IncomingPlaybackDeviceId);
        True(!settings.IncomingStrongerNoiseFilter);
        Equal(0x77, settings.KeyboardVirtualKey);
        Equal("EN-US,JA", string.Join(',', settings.TargetLanguages));
        File.WriteAllText(store.Path, """{"oscPort":9010,"subtitles":{"distanceMeters":2.0}}""");
        settings = new SettingsStore(store.Path).Load();
        Equal(1m, settings.Speech.Rate);
        Equal(SpeechExpression.Auto, settings.Speech.Expression);
        True(settings.Speech.Keywords.SequenceEqual(SpeechExpression.Defaults));
        Equal(9010, settings.OscPort);
        Equal(3d, settings.Subtitles.DurationSeconds);
        Equal(2d, settings.Subtitles.DistanceMeters);
        Equal(AppearanceMode.System, settings.Appearance);
        True(settings.RecognitionHotwordsEnabled);
        Equal(0, settings.RecognitionHotwords.Count);
    Equal("", settings.Terminology.Context);
    Equal(0, settings.Terminology.Corrections.Count);
    Equal(0, settings.Terminology.Terms.Count);
        Equal(300, settings.IncomingSentenceSilenceMs);
        _ = Throws<FormatException>(() => (settings with { IncomingSentenceSilenceMs = 199 }).Validate());
        _ = Throws<FormatException>(() => (settings with { IncomingSentenceSilenceMs = 6001 }).Validate());
        store.Save(settings with { Appearance = AppearanceMode.Dark, BailianApiKey = "one", DeepLApiKey = "two:fx", TargetLanguages = ["ja", "en-us"],
            IncomingPlaybackDeviceId = "example-render-endpoint", IncomingStrongerNoiseFilter = true, IncomingSentenceSilenceMs = 225,
            RecognitionHotwordsEnabled = false, RecognitionHotwords = RecognitionVocabulary.ParseLines(["模型\t5", "Kipfel\t50"]),
            Terminology = new("VRChat 术语", [new("全锥", "全追")], [new("全追", "en-us", "full-body tracking")]),
            Speech = new() { Enabled = true, Mode = SpeechMode.JapaneseTranslation, VolumePercent = 35,
                Expression = "whispers", Keywords = [], Rate = 1.25m,
                ChineseVoice = "  zh-voice  ", EnglishVoice = "en-voice", JapaneseVoice = "ja-voice", OutputDeviceId = "tts-output",
                VoiceAliases = new Dictionary<string, string> { ["zh-voice"] = "本机音色", ["Emily_v3.1"] = "英式播报" },
                MonitorEnabled = true, MonitorDeviceId = "headphones" } });
        string raw = File.ReadAllText(store.Path);
        True(raw.Contains("\"one\"") && raw.Contains("\"two:fx\""));
        True(raw.Contains("\"appearance\": \"dark\""));
        Equal(AppearanceMode.Dark, store.Load().Appearance);
        File.WriteAllText(store.Path, raw.Replace("\"appearance\": \"dark\"", "\"appearance\": \"light\""));
        Equal(AppearanceMode.Light, store.Load().Appearance);
        Equal("JA,EN-US", string.Join(',', store.Load().TargetLanguages));
        Equal("example-render-endpoint", store.Load().IncomingPlaybackDeviceId);
        True(!store.Load().RecognitionHotwordsEnabled);
        Equal(5, store.Load().RecognitionHotwords["模型"]);
        Equal(50, store.Load().RecognitionHotwords["Kipfel"]);
        var terminology = store.Load().Terminology;
        Equal("VRChat 术语", terminology.Context);
        Equal("你用的是什么全追？", terminology.Correct("你用的是什么全锥？", out int corrections));
        Equal(1, corrections);
        Equal("full-body tracking", terminology.MatchTerms("全追", "EN-US").Single().Target);
        True(store.Load().IncomingStrongerNoiseFilter);
        Equal(225, store.Load().IncomingSentenceSilenceMs);
        var loadedSpeech = store.Load().Speech;
        Equal(0, loadedSpeech.Keywords.Count);
        Equal(new SpeechSettings { Enabled = true, Mode = SpeechMode.JapaneseTranslation, VolumePercent = 35,
            Expression = "whispers", Rate = 1.25m, Keywords = loadedSpeech.Keywords,
            ChineseVoice = "zh-voice", EnglishVoice = "en-voice", JapaneseVoice = "ja-voice", OutputDeviceId = "tts-output",
            MonitorEnabled = true, MonitorDeviceId = "headphones", Voices = loadedSpeech.Voices }, loadedSpeech);
        var loadedVoices = loadedSpeech.Voices!;
        Equal("本机音色", loadedVoices.Single(voice => voice.Id == "zh-voice").Alias);
        Equal("英式播报", loadedVoices.Single(voice => voice.Id == "Emily_v3.1").Alias);
        Equal(SpeechVoiceLanguages.English, loadedVoices.Single(voice => voice.Id == "Emily_v3.1").Languages);
        True(loadedVoices.Any(voice => voice.Id == "en-voice"));
        True(!raw.Contains("\"voiceAliases\""));
        store.Save(store.Load() with { Speech = loadedSpeech with { Keywords = [new(" 握手 ", "giggles")] } });
        Equal(new SpeechKeyword("握手", "giggles"), store.Load().Speech.Keywords.Single());
        Equal(1, Directory.GetFiles(directory).Length);
        const string bad = "{\"oscPort\":";
        File.WriteAllText(store.Path, bad);
        True(Throws<FormatException>(() => store.Load()).Message.Contains(store.Path));
        _ = Throws<FormatException>(() => store.Save(new()));
        Equal(bad, File.ReadAllText(store.Path));
    }
    finally
    {
        File.Delete(store.Path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
    return Task.CompletedTask;
}

static Task WorkspaceMigration()
{
    var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "vrc-workspace-" + Guid.NewGuid().ToString("N"), "config.json"));
    string directory = Path.GetDirectoryName(store.Path)!;
    Directory.CreateDirectory(directory);
    try
    {
        foreach (var (region, regionId) in new[] { (BailianRegion.Beijing, "cn-beijing"), (BailianRegion.Singapore, "ap-southeast-1") })
        {
            string address = $"wss://ws-migrate.{regionId}.maas.aliyuncs.com/api-ws/v1/inference";
            File.WriteAllText(store.Path, $$"""{"bailianEndpoint":"{{address}}","bailianApiKey":"old-key","deepLApiKey":"old-deepl","oscPort":9012}""");
            var migrated = store.Load();
            Equal("ws-migrate", migrated.BailianWorkspaceId);
            Equal(region, migrated.BailianRegion);
            Equal("old-key", migrated.BailianApiKey);
            Equal("old-deepl", migrated.DeepLApiKey);
            Equal(9012, migrated.OscPort);
            Equal(TranslationProvider.DeepL, migrated.TranslationProvider);
            Equal(address, BailianEndpoints.Audio(migrated).AbsoluteUri);
            Equal($"https://ws-migrate.{regionId}.maas.aliyuncs.com/compatible-mode/v1/chat/completions", BailianEndpoints.Translation(migrated).AbsoluteUri);
            True(File.ReadAllText(store.Path).Contains("bailianEndpoint"));
            store.Save(migrated with { TranslationProvider = TranslationProvider.QwenMtLite });
            using var saved = JsonDocument.Parse(File.ReadAllText(store.Path));
            True(!saved.RootElement.TryGetProperty("bailianEndpoint", out _));
            Equal("ws-migrate", saved.RootElement.GetProperty("bailianWorkspaceId").GetString());
            Equal("qwen-mt-lite", saved.RootElement.GetProperty("translationProvider").GetString());
            var reloaded = store.Load();
            Equal(region, reloaded.BailianRegion);
            Equal(TranslationProvider.QwenMtLite, reloaded.TranslationProvider);
            Equal("old-deepl", reloaded.DeepLApiKey);
        }
        File.WriteAllText(store.Path, """{"BailianEndpoint":"wss://other.example/api-ws/v1/inference","bailianApiKey":"old-key","oscPort":9013}""");
        var withoutId = store.Load();
        Equal("", withoutId.BailianWorkspaceId);
        Equal("old-key", withoutId.BailianApiKey);
        Equal(9013, withoutId.OscPort);
        True(BailianEndpoints.ConfigurationError(withoutId)?.Contains("Workspace ID") == true);
        True(Throws<FormatException>(() => store.Save(withoutId with { BailianWorkspaceId = "wss://example.com/" })).Message.Contains("Workspace ID"));
        File.WriteAllText(store.Path, """{"bailianEndpoint":"wss://ws-old.ap-southeast-1.maas.aliyuncs.com/api-ws/v1/inference","bailianWorkspaceId":"ws-new","bailianRegion":"beijing"}""");
        Equal("ws-new", store.Load().BailianWorkspaceId);
        Equal(BailianRegion.Beijing, store.Load().BailianRegion);
    }
    finally
    {
        File.Delete(store.Path);
        Directory.Delete(directory);
    }
    return Task.CompletedTask;
}

static string ReadOsc(byte[] data, ref int offset)
{
    int end = Array.IndexOf(data, (byte)0, offset);
    string value = new UTF8Encoding(false, true).GetString(data, offset, end - offset);
    offset = (end / 4 + 1) * 4;
    True(data.AsSpan(end, offset - end).ToArray().All(value => value == 0));
    return value;
}

static Settings CloudSettings() => new()
{
    BailianWorkspaceId = "ws-example", BailianApiKey = "fake", DeepLApiKey = "test:fx"
};

static HttpClient FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new FakeHandler(send));
static HttpResponseMessage TranslationResponse(string source, string text) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { translations = new[] { new { detected_source_language = source, text } } }))
};

static HttpResponseMessage MtResponse(string text, string finish = "stop") => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = finish } } }))
};

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class FakeSocket(bool ignoreReceiveCancellation = false) : WebSocket
{
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> fromServer =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> toServer = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private WebSocketState state = WebSocketState.Open;
    public bool HasOutgoing => toServer.Reader.TryPeek(out _);

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => state;
    public override void Abort() { state = WebSocketState.Aborted; fromServer.Writer.TryComplete(); }
    public override void Dispose() => Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var next = await fromServer.Reader.ReadAsync(ignoreReceiveCancellation ? CancellationToken.None : cancellationToken);
        next.Bytes.CopyTo(buffer.Array!, buffer.Offset);
        return new(next.Bytes.Length, next.Type, next.End);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        toServer.Writer.WriteAsync((buffer.ToArray(), messageType), cancellationToken).AsTask();

    public async Task<string> SentText()
    {
        var item = await toServer.Reader.ReadAsync();
        if (item.Type != WebSocketMessageType.Text) throw new InvalidOperationException("Expected text frame.");
        return Encoding.UTF8.GetString(item.Bytes);
    }

    public async Task<(WebSocketMessageType, string)> SentHex()
    {
        var item = await toServer.Reader.ReadAsync();
        return (item.Type, Convert.ToHexString(item.Bytes));
    }

    public async Task<string> TaskId()
    {
        using var json = JsonDocument.Parse(await SentText());
        return json.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
    }

    public void Event(string type, string id, bool split = false, string code = "", string detail = "") =>
        Send(new { header = new { @event = type, task_id = id, error_code = code, error_message = detail } }, split);

    public void Binary(byte[] bytes) => fromServer.Writer.TryWrite((bytes, WebSocketMessageType.Binary, true));

    public void Sentence(string id, int number, string text, bool end, bool heartbeat = false, int? endTime = null) => Send(new
    {
        header = new { @event = "result-generated", task_id = id },
        payload = new { output = new { sentence = new { sentence_id = number, text, sentence_end = end, heartbeat, end_time = endTime } } }
    });

    private void Send(object message, bool split = false)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        int middle = bytes.Length / 2;
        if (split) fromServer.Writer.TryWrite((bytes[..middle], WebSocketMessageType.Text, false));
        fromServer.Writer.TryWrite((split ? bytes[middle..] : bytes, WebSocketMessageType.Text, true));
    }
}
