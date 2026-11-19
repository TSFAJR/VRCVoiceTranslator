using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SpeechRenderOutput
{
    private readonly object startGate = new();
    private readonly TaskCompletionSource<string> endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true });
    private readonly CancellationToken cancellation;
    private bool started;
    private int playing;

    public SpeechRenderOutput(string? selectedDevice, int volume, CancellationToken cancellation,
        Action onStarted, Task<string>? skipDevice = null)
    {
        this.cancellation = cancellation;
        Work = Task.Factory.StartNew(() => Render(selectedDevice, volume, skipDevice, onStarted), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task Work { get; }
    public Task<string> Endpoint => endpoint.Task;
    public ChannelWriter<byte[]> Input => audio.Writer;
    public bool Playing => Volatile.Read(ref playing) != 0;

    public void WaitUntilStopped()
    {
        lock (startGate)
        {
            if (!started) return;
        }
        try { Work.GetAwaiter().GetResult(); }
        catch (Exception) { /* The playback job owns the outcome. */ }
    }

    private void Render(string? selectedDevice, int volume, Task<string>? skipDevice, Action onStarted)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = selectedDevice is null
                ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(selectedDevice);
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                throw new ServiceException("所选播报输出设备不可用，请重新选择。");
            string deviceId = device.ID;
            endpoint.SetResult(deviceId);
            if (skipDevice is not null && string.Equals(deviceId, skipDevice.WaitAsync(cancellation).GetAwaiter().GetResult(),
                StringComparison.OrdinalIgnoreCase)) return;
            using var signal = new AutoResetEvent(false);
            using var client = device.AudioClient;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                400000, 0, new WaveFormat(BailianTtsClient.SampleRate, 16, 1), Guid.Empty);
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
            Play(client, signal, volume, onStarted);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && (error is COMException or ArgumentException))
        { throw new ServiceException($"播报输出设备打开或播放失败，请检查设备连接（0x{error.HResult:X8}）。"); }
    }

    private void Play(AudioClient client, WaitHandle signal, int volume, Action onStarted)
    {
        if (!audio.Reader.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult()) return;
        var renderer = client.AudioRenderClient;
        int capacity = client.BufferSize;
        var buffer = new byte[capacity * sizeof(short)];
        byte[]? chunk = null;
        int offset = 0;
        WaitHandle[] events = [cancellation.WaitHandle, signal];

        try
        {
            Write(capacity);
            lock (startGate)
            {
                cancellation.ThrowIfCancellationRequested();
                client.Start();
                started = true;
            }
            Volatile.Write(ref playing, 1);
            onStarted();
            while (WaitHandle.WaitAny(events, 40) != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int padding = client.CurrentPadding;
                int written = Write(capacity - padding);
                if (written == 0 && padding == 0 && audio.Reader.Completion.IsCompleted) break;
            }
        }
        finally
        {
            Volatile.Write(ref playing, 0);
            if (started)
            {
                try { client.Stop(); client.Reset(); }
                catch (COMException) when (cancellation.IsCancellationRequested) { }
            }
        }

        int Write(int frames)
        {
            int end = frames * sizeof(short);
            int filled = 0;
            while (filled < end)
            {
                if (chunk is null && !audio.Reader.TryRead(out chunk)) break;
                int size = Math.Min(end - filled, chunk.Length - offset);
                chunk.AsSpan(offset, size).CopyTo(buffer.AsSpan(filled));
                filled += size;
                offset += size;
                if (offset == chunk.Length) { chunk = null; offset = 0; }
            }
            if (filled == 0) return 0;
            cancellation.ThrowIfCancellationRequested();
            if (volume != 100)
            {
                foreach (ref short sample in MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, filled)))
                    sample = (short)(sample * volume / 100);
            }
            int count = filled / sizeof(short);
            nint destination = renderer.GetBuffer(count);
            Marshal.Copy(buffer, 0, destination, filled);
            renderer.ReleaseBuffer(count, AudioClientBufferFlags.None);
            return count;
        }
    }
}
