using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal static class IncomingLoopbackCapture
{
    private const int CaptureRate = 44100;
    private const string DevicePath = @"VAD\Process_Loopback";

    public static bool UsesProcessLoopback => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public static Task RunAsync(int processId, string? playbackDeviceId, IncomingAudio audio, Action started, CancellationToken cancellation) =>
        Task.Factory.StartNew(() => Run(processId, playbackDeviceId, audio, started, cancellation), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Run(int processId, string? playbackDeviceId, IncomingAudio audio, Action started, CancellationToken cancellation)
    {
        using var ready = new AutoResetEvent(false);
        cancellation.ThrowIfCancellationRequested();
        bool processLoopback = UsesProcessLoopback;
        using var client = processLoopback ? OpenProcessClient(processId, cancellation) : OpenPlaybackClient(playbackDeviceId);
        try { RunClient(client, ready, audio, started, cancellation); }
        catch (COMException error) when (!processLoopback && !cancellation.IsCancellationRequested)
        { throw new ServiceException($"播放设备回环已失败，请检查设备连接后重试（0x{error.HResult:X8}）。"); }
    }

    private static void RunClient(AudioClient client, AutoResetEvent ready, IncomingAudio audio, Action started, CancellationToken cancellation)
    {
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback |
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            0, 0, new WaveFormat(CaptureRate, 16, 2), Guid.Empty);
        var capture = client.AudioCaptureClient; // Owned by AudioClient.
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var resampler = new PcmConverter(client.BufferSize, audio);
        WaitHandle[] events = [cancellation.WaitHandle, ready];

        cancellation.ThrowIfCancellationRequested();
        client.Start();
        try
        {
            started();
            while (WaitHandle.WaitAny(events) == 1)
            {
                while (!cancellation.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    nint buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    try { resampler.Write(buffer, frames, (flags & AudioClientBufferFlags.Silent) != 0); }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        finally
        {
            try { client.Stop(); }
            catch (COMException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static AudioClient OpenPlaybackClient(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                throw new ServiceException("所选播放设备不可用，请在「识别与翻译」重新选择。");
            return device.AudioClient;
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        { throw new ServiceException($"无法打开播放设备，请在「识别与翻译」检查选择（0x{error.HResult:X8}）。"); }
    }

    private static AudioClient OpenProcessClient(int processId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var activation = ActivateAsync(processId);
        try { return activation.WaitAsync(TimeSpan.FromSeconds(5), cancellation).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            _ = DisposeLateClientAsync(activation);
            throw;
        }
        catch (TimeoutException)
        {
            _ = DisposeLateClientAsync(activation);
            throw new ServiceException("打开 VRChat 音频采集超时。");
        }
    }

    private static async Task DisposeLateClientAsync(Task<AudioClient> activation)
    {
        try { (await activation.ConfigureAwait(false)).Dispose(); }
        catch (Exception) { /* Activation was abandoned by the capture thread. */ }
    }

    private static async Task<AudioClient> ActivateAsync(int processId)
    {
        var completion = new ActivationCompletion();
        var parameters = new ActivationParameters { Type = 1, ProcessId = (uint)processId, Mode = 0 };
        nint parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
        nint variantPointer = 0;
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            var variant = new BlobVariant { Type = (ushort)VarEnum.VT_BLOB,
                Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = parametersPointer };
            variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
            Marshal.StructureToPtr(parameters, parametersPointer, false);
            Marshal.StructureToPtr(variant, variantPointer, false);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(DevicePath, in iid, variantPointer, completion, out operation));
            return new AudioClient(await completion.Result.Task.ConfigureAwait(false));
        }
        finally
        {
            // The native activation can outlive a cancelled capture. Keep the blob and callback until it completes.
            if (operation is not null) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(variantPointer);
            Marshal.FreeHGlobal(parametersPointer);
            GC.KeepAlive(completion);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string path, in Guid iid, nint parameters,
        IActivateAudioInterfaceCompletionHandler completion, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters
    {
        public uint Type; // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
        public uint ProcessId;
        public uint Mode; // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public uint Size;
        public nint Data;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    private interface IAgileObject;

    private sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int result, out object client);
                Marshal.ThrowExceptionForHR(result);
                Result.TrySetResult((IAudioClient)client);
            }
            catch (Exception error) { Result.TrySetException(error); }
        }
    }

    private sealed class PcmConverter
    {
        private readonly WdlResampler resampler = new();
        private readonly float[] samples;
        private readonly IncomingAudio audio;
        private byte[] frame = new byte[IncomingAudio.FrameBytes];
        private int filled;

        public PcmConverter(int bufferFrames, IncomingAudio audio)
        {
            this.audio = audio;
            samples = new float[bufferFrames + 64];
            resampler.SetFeedMode(true);
            resampler.SetMode(true, 2, false);
            resampler.SetRates(CaptureRate, IncomingAudio.SampleRate);
        }

        public unsafe void Write(nint buffer, int frames, bool silent)
        {
            int inputFrames = resampler.ResamplePrepare(frames, 1, out var input, out int offset);
            if (silent) Array.Clear(input, offset, inputFrames);
            else
            {
                var stereo = new ReadOnlySpan<short>((void*)buffer, 2 * inputFrames);
                for (int i = 0; i < inputFrames; i++)
                    input[offset + i] = (stereo[2 * i] + stereo[2 * i + 1]) / 65536f;
            }

            int count = resampler.ResampleOut(samples, 0, inputFrames, samples.Length, 1);
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                int value = Math.Clamp((int)(samples[i] * 32768), short.MinValue, short.MaxValue);
                frame[filled++] = (byte)value;
                frame[filled++] = (byte)(value >> 8);
                if (filled < frame.Length) continue;
                audio.Push(frame, now - (count - i - 1) * Stopwatch.Frequency / IncomingAudio.SampleRate);
                frame = new byte[IncomingAudio.FrameBytes];
                filled = 0;
            }
        }
    }
}
