using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VrcTranslator;

public sealed class ServiceException(string message) : Exception(message);
internal sealed class BailianConnectionException(string message, int status) : Exception(message)
{
    public bool CanRetry => status is not (400 or 401 or 403 or 404);
}

public readonly record struct RecognizedSentence(string Text, int? EndMilliseconds);

public sealed class BailianClient
{
    public const string Model = "qwen-audio-3.1-asr-flash-message";
    private const int FrameBytes = 3200;
    private readonly Func<Settings, CancellationToken, Task<WebSocket>> connect;
    private readonly TimeSpan startupTimeout;

    public BailianClient(Func<Settings, CancellationToken, Task<WebSocket>>? connect = null, TimeSpan? startupTimeout = null)
    {
        this.connect = connect ?? ConnectAsync;
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(8);
    }

    public Task<string> RecognizeAsync(Settings settings, IAsyncEnumerable<byte[]> audio, CancellationToken cancellation) =>
        RecognizeCoreAsync(settings, audio, onSentence: null, onStarted: null, cancellation);

    public async Task StreamSentencesAsync(Settings settings, IAsyncEnumerable<byte[]> audio,
        Action<RecognizedSentence> onSentence, CancellationToken cancellation, Action? onStarted = null) =>
        _ = await RecognizeCoreAsync(settings, audio, onSentence, onStarted, cancellation).ConfigureAwait(false);

    private async Task<string> RecognizeCoreAsync(Settings settings, IAsyncEnumerable<byte[]> audio,
        Action<RecognizedSentence>? onSentence, Action? onStarted, CancellationToken cancellation)
    {
        if (BailianEndpoints.ConfigurationError(settings) is { } error) throw new ServiceException(error);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(startupTimeout);
        WebSocket? socket = null;
        Task<string>? receiver = null;
        Task? sender = null;
        string id = Guid.NewGuid().ToString();
        try
        {
            socket = await connect(settings, startup.Token).ConfigureAwait(false);
            await SendJsonAsync(socket, "run-task", id, RunPayload(settings, incoming: onSentence is not null), startup.Token).ConfigureAwait(false);
            receiver = ReceiveResultsAsync(socket, id, ready, finishing.Task, onSentence, lifetime.Token);
            var startedOrEnded = await Task.WhenAny(ready.Task, receiver).WaitAsync(startup.Token).ConfigureAwait(false);
            if (startedOrEnded == receiver) return await receiver.ConfigureAwait(false);
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            onStarted?.Invoke();

            sender = SendAudioAsync(socket, id, audio, finishing, lifetime.Token);
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            if (first == sender)
            {
                await sender.ConfigureAwait(false);
                if (onSentence is not null)
                    return await receiver.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false);
            }
            string result = await receiver.ConfigureAwait(false);
            await sender.ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && startup.IsCancellationRequested)
        {
            throw new ServiceException("百炼连接或任务启动超时。");
        }
        catch (BailianConnectionException exception) when (onSentence is null)
        { throw new ServiceException(exception.Message); }
        catch (TimeoutException) when (onSentence is not null)
        { throw new ServiceException("百炼未及时结束来话识别任务。"); }
        catch (WebSocketException exception) when (!cancellation.IsCancellationRequested)
        {
            throw new ServiceException($"百炼 WebSocket 失败（{exception.WebSocketErrorCode}{HttpStatus(exception)}）。");
        }
        catch (JsonException) { throw new ServiceException("百炼返回了无法解析的事件。"); }
        finally
        {
            lifetime.Cancel();
            socket?.Abort();
            socket?.Dispose();
            await ObserveAsync(sender).ConfigureAwait(false);
            await ObserveAsync(receiver).ConfigureAwait(false);
        }
    }

    internal static string HttpStatus(WebSocketException exception)
    {
        if (exception.InnerException is HttpRequestException { StatusCode: { } status }) return $" / HTTP {(int)status}";
        return "";
    }

    internal static async Task<WebSocket> ConnectAsync(Settings settings, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {settings.BailianApiKey}");
            socket.Options.CollectHttpResponseDetails = true;
            await socket.ConnectAsync(BailianEndpoints.Audio(settings), cancellation).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException) when ((int)socket.HttpStatusCode > 0)
        {
            int status = (int)socket.HttpStatusCode;
            socket.Dispose();
            throw new BailianConnectionException($"百炼握手失败（HTTP {status}）。", status);
        }
        catch { socket.Dispose(); throw; }
    }

    private static object RunPayload(Settings settings, bool incoming)
    {
        var parameters = new Dictionary<string, object>
        {
            ["format"] = "pcm", ["sample_rate"] = 16000,
            ["disfluency_removal_enabled"] = false, ["intermediate_result_enabled"] = false
        };
        if (incoming)
        {
            parameters["max_sentence_silence"] = 500;
            if (settings.IncomingStrongerNoiseFilter) parameters["speech_noise_threshold"] = 0.1;
        }
        string? hint = settings.SourceLanguage switch
        {
            SourceLanguage.Chinese => "zh", SourceLanguage.English => "en",
            SourceLanguage.Japanese => "ja", SourceLanguage.Korean => "ko", _ => null
        };
        if (hint is not null) parameters["language_hints"] = new[] { hint };
        return new { task_group = "audio", task = "asr", function = "recognition", model = Model, parameters, input = new { } };
    }

    internal static Task SendJsonAsync(WebSocket socket, string action, string id, object payload, CancellationToken cancellation)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { header = new { action, task_id = id, streaming = "duplex" }, payload });
        return socket.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, cancellation);
    }

    private static async Task SendAudioAsync(WebSocket socket, string id, IAsyncEnumerable<byte[]> audio,
        TaskCompletionSource finishing, CancellationToken cancellation)
    {
        var frame = new byte[FrameBytes];
        int used = 0;
        await foreach (byte[] chunk in audio.WithCancellation(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            int offset = 0;
            while (offset < chunk.Length)
            {
                int count = Math.Min(chunk.Length - offset, frame.Length - used);
                Buffer.BlockCopy(chunk, offset, frame, used, count);
                offset += count;
                used += count;
                if (used < frame.Length) continue;
                await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancellation).ConfigureAwait(false);
                used = 0;
            }
        }
        if (used > 0)
            await socket.SendAsync(new ArraySegment<byte>(frame, 0, used), WebSocketMessageType.Binary, true, cancellation).ConfigureAwait(false);
        finishing.TrySetResult();
        await SendJsonAsync(socket, "finish-task", id, new { input = new { } }, cancellation).ConfigureAwait(false);
    }

    private static async Task<string> ReceiveResultsAsync(WebSocket socket, string id, TaskCompletionSource ready,
        Task finishing, Action<RecognizedSentence>? onSentence, CancellationToken cancellation)
    {
        var sentences = onSentence is null ? new SortedDictionary<int, string>() : null;
        int lastReported = 0;
        var bytes = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), cancellation).ConfigureAwait(false);
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new ServiceException("百炼连接提前关闭或返回了非文本事件。");
            message.Write(bytes, 0, frame.Count);
            if (message.Length > 1024 * 1024) throw new ServiceException("百炼事件过大。");
            if (!frame.EndOfMessage) continue;

            using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            message.SetLength(0);
            if (!Property(json.RootElement, "header", out var header) ||
                !Text(header, "task_id", out var taskId) || taskId != id || !Text(header, "event", out var type)) continue;
            switch (type)
            {
                case "task-started": ready.TrySetResult(); break;
                case "task-failed":
                    Text(header, "error_code", out var code);
                    throw new ServiceException($"百炼识别失败（{SafeCode(code)}）。");
                case "result-generated":
                    if (Property(json.RootElement, "payload", out var payload) &&
                        Property(payload, "output", out var output) && Property(output, "sentence", out var sentence) &&
                        !True(sentence, "heartbeat") && True(sentence, "sentence_end") &&
                        Property(sentence, "sentence_id", out var number) && number.ValueKind == JsonValueKind.Number &&
                        number.TryGetInt32(out int index) && index > 0 && Text(sentence, "text", out var content) &&
                        !string.IsNullOrWhiteSpace(content))
                    {
                        if (sentences is not null) sentences[index] = content.Trim();
                        else if (index > lastReported)
                        {
                            lastReported = index;
                            int? end = Property(sentence, "end_time", out var timestamp) &&
                                timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt32(out int milliseconds) &&
                                milliseconds >= 0 ? milliseconds : null;
                            onSentence!(new(content.Trim(), end));
                        }
                    }
                    break;
                case "task-finished":
                    if (!finishing.IsCompleted) throw new ServiceException("百炼任务在提交前结束。");
                    return sentences is null ? "" : JoinSentences(sentences.Values);
            }
        }
    }

    private static string JoinSentences(IEnumerable<string> sentences)
    {
        var text = new StringBuilder();
        foreach (string sentence in sentences)
        {
            if (text.Length > 0 && !IsEastAsian(text[^1]) && !IsEastAsian(sentence[0])) text.Append(' ');
            text.Append(sentence);
        }
        return text.ToString();
    }

    private static bool IsEastAsian(char ch) => ch is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uFF00' and <= '\uFFEF';

    internal static bool Property(JsonElement element, string name, out JsonElement child)
    {
        child = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out child);
    }

    internal static bool Text(JsonElement element, string name, out string value)
    {
        value = "";
        if (!Property(element, name, out var child) || child.ValueKind != JsonValueKind.String) return false;
        value = child.GetString()!;
        return true;
    }

    private static bool True(JsonElement element, string name) => Property(element, name, out var child) && child.ValueKind == JsonValueKind.True;

    internal static string SafeCode(string code) => code.Length is > 0 and <= 64 &&
        code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? code : "未知错误";

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The primary protocol failure is propagated by RecognizeAsync. */ }
    }
}
