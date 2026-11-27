using System.Net.WebSockets;
using System.Text.Json;
using static VrcTranslator.BailianClient;

namespace VrcTranslator;

public sealed class PreparedSocket : IDisposable
{
    private readonly object guard = new();
    private readonly CancellationTokenSource cancel = new();
    private readonly Task<WebSocket> connection;
    private bool taken;
    private bool disposed;

    internal PreparedSocket(Func<CancellationToken, Task<WebSocket>> connect)
    {
        connection = Task.Run(() => connect(cancel.Token));
    }

    public async Task<WebSocket?> TryTakeAsync(CancellationToken cancellation)
    {
        lock (guard)
        {
            if (disposed || taken) return null;
        }
        try
        {
            var socket = await connection.WaitAsync(cancellation).ConfigureAwait(false);
            lock (guard)
            {
                if (disposed || taken) return null;
                taken = true;
            }
            if (socket.State == WebSocketState.Open) return socket;
            socket.Dispose();
            return null;
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (guard)
        {
            if (disposed) return;
            disposed = true;
        }
        cancel.Cancel();
        _ = CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        try
        {
            var socket = await connection.ConfigureAwait(false);
            bool disposeSocket;
            lock (guard)
            {
                disposeSocket = !taken;
                taken = true;
            }
            if (disposeSocket)
            {
                socket.Abort();
                socket.Dispose();
            }
        }
        catch (Exception) { /* Abandoned or failed pre-connection. */ }
        finally { cancel.Dispose(); }
    }
}

public sealed class BailianTtsClient(Func<Settings, CancellationToken, Task<WebSocket>>? connect = null,
    TimeSpan? startupTimeout = null, TimeSpan? responseTimeout = null)
{
    public const string Model = "qwen-audio-3.1-tts-flash";
    public const int SampleRate = 24000;
    public const int MaxTextLength = 20000;
    private readonly Func<Settings, CancellationToken, Task<WebSocket>> open = connect ?? ConnectAsync;
    private readonly TimeSpan startupLimit = startupTimeout ?? TimeSpan.FromSeconds(8);
    private readonly TimeSpan responseLimit = responseTimeout ?? TimeSpan.FromSeconds(15);

    public PreparedSocket? Prepare(Settings settings)
    {
        if (!settings.Speech.Enabled || BailianEndpoints.ConfigurationError(settings) is not null ||
            string.IsNullOrWhiteSpace(settings.Speech.SelectMode().Voice)) return null;
        return new PreparedSocket(cancellation => open(settings, cancellation));
    }

    public async Task SynthesizeAsync(string text, Settings settings, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive,
        CancellationToken cancellation, SpeechTag? tag = null, PreparedSocket? prepared = null, Action<bool>? onConnected = null)
    {
        if (BailianEndpoints.ConfigurationError(settings) is { } configurationError) throw new ServiceException(configurationError);
        var mode = settings.Speech.SelectMode();
        if (string.IsNullOrWhiteSpace(mode.Voice)) throw new ServiceException("请填写当前播报模式的音色 ID。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) throw new ServiceException($"待播报文本为空或超过 {MaxTextLength} 字符。");

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var response = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(startupLimit);
        WebSocket? socket = null;
        bool usedPrepared = false;
        bool retriedPrepared = false;
        while (true)
        {
            bool started = false;
            try
            {
                if (!retriedPrepared && prepared is not null)
                {
                    socket = await prepared.TryTakeAsync(startup.Token).ConfigureAwait(false);
                    usedPrepared = socket is not null;
                }
                else usedPrepared = false;
                socket ??= await open(settings, startup.Token).ConfigureAwait(false);
                onConnected?.Invoke(usedPrepared);
                string id = Guid.NewGuid().ToString();
                var parameters = new Dictionary<string, object>
                {
                    ["text_type"] = "PlainText", ["voice"] = mode.Voice, ["format"] = "pcm", ["sample_rate"] = SampleRate,
                    ["rate"] = settings.Speech.Rate, ["language_hints"] = new[] { mode.Language }
                };
                if (tag?.Instruction(mode.Language) is { } instruction) parameters["instruction"] = instruction;
                await SendJsonAsync(socket, "run-task", id, new
                {
                    task_group = "audio", task = "tts", function = "SpeechSynthesizer", model = Model,
                    parameters,
                    input = new { }
                }, startup.Token).ConfigureAwait(false);

                var bytes = new byte[8193];
                using var jsonMessage = new MemoryStream();
                bool gotAudio = false;
                int carry = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (started) response.CancelAfter(responseLimit);
                    var frame = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, carry, bytes.Length - carry),
                        started ? response.Token : startup.Token).ConfigureAwait(false);
                    response.CancelAfter(Timeout.InfiniteTimeSpan);
                    cancellation.ThrowIfCancellationRequested();
                    if (frame.MessageType == WebSocketMessageType.Binary)
                    {
                        if (!started) throw new ServiceException("百炼 TTS 在任务启动前返回了音频。");
                        int count = carry + frame.Count;
                        int aligned = count & ~1;
                        if (aligned != 0)
                        {
                            gotAudio = true;
                            await receive(bytes.AsMemory(0, aligned), cancellation).ConfigureAwait(false);
                        }
                        carry = count - aligned;
                        if (carry != 0) bytes[0] = bytes[aligned];
                        continue;
                    }
                    if (frame.MessageType != WebSocketMessageType.Text) throw new ServiceException("百炼 TTS 连接提前关闭。");
                    jsonMessage.Write(bytes, carry, frame.Count);
                    if (jsonMessage.Length > 1024 * 1024) throw new ServiceException("百炼 TTS 返回的事件过大。");
                    if (!frame.EndOfMessage) continue;

                    using var json = JsonDocument.Parse(jsonMessage.GetBuffer().AsMemory(0, (int)jsonMessage.Length));
                    jsonMessage.SetLength(0);
                    if (!Property(json.RootElement, "header", out var header) || !Text(header, "task_id", out string taskId) ||
                        taskId != id || !Text(header, "event", out string type)) continue;
                    switch (type)
                    {
                        case "task-started" when !started:
                            response.CancelAfter(responseLimit);
                            await SendJsonAsync(socket, "continue-task", id, new { input = new { text } }, response.Token).ConfigureAwait(false);
                            await SendJsonAsync(socket, "finish-task", id, new { input = new { } }, response.Token).ConfigureAwait(false);
                            startup.CancelAfter(Timeout.InfiniteTimeSpan);
                            started = true;
                            break;
                        case "task-failed":
                            Text(header, "error_code", out string code);
                            Text(header, "error_message", out string detail);
                            string hint = detail.Contains("voice", StringComparison.OrdinalIgnoreCase) || detail.Contains("音色", StringComparison.Ordinal)
                                ? " 请检查音色 ID 和百炼工作空间权限。" : "";
                            throw new ServiceException($"百炼 TTS 失败（{SafeCode(code)}）。{hint}");
                        case "task-finished":
                            if (!gotAudio || carry != 0) throw new ServiceException("百炼 TTS 返回的 PCM 音频为空或不完整。");
                            return;
                    }
                }
            }
            catch (Exception error) when (usedPrepared && !retriedPrepared && !started && !cancellation.IsCancellationRequested &&
                !startup.IsCancellationRequested && (error is WebSocketException || error is ServiceException { Message: "百炼 TTS 连接提前关闭。" }))
            {
                retriedPrepared = true;
                continue;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && startup.IsCancellationRequested)
            { throw new ServiceException("百炼 TTS 连接或任务启动超时。"); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && response.IsCancellationRequested)
            { throw new ServiceException("等待百炼 TTS 音频或任务结束超时。"); }
            catch (BailianConnectionException error) { throw new ServiceException($"TTS · {error.Message}"); }
            catch (WebSocketException error) when (!cancellation.IsCancellationRequested)
            { throw new ServiceException($"百炼 TTS WebSocket 失败（{error.WebSocketErrorCode}{HttpStatus(error)}）。"); }
            catch (JsonException) { throw new ServiceException("百炼 TTS 返回了无法解析的事件。"); }
            finally
            {
                socket?.Abort();
                socket?.Dispose();
                socket = null;
            }
        }
    }
}
