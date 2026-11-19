using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed record CaptureDevice(string? Id, string Name)
{
    public override string ToString() => Name;
    public static List<CaptureDevice> PlaybackDevices() => Active(DataFlow.Render, "Windows 默认播放设备");

    public static List<CaptureDevice> Active(DataFlow flow, string defaultName)
    {
        using var devices = new MMDeviceEnumerator();
        var list = new List<CaptureDevice> { new(null, defaultName) };
        foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) list.Add(new(device.ID, device.FriendlyName));
        }
        return list;
    }
}

internal sealed class Microphone : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Microphone(string? deviceId, Action<byte[], int> receive)
    {
        try
        {
            device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
            try
            {
                if (device.State != DeviceState.Active) throw new ServiceException("所选麦克风不可用，请检查连接或重新选择。");
                // Shared-mode WASAPI performs the channel conversion and resampling for this format.
                capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 20)
                {
                    ShareMode = AudioClientShareMode.Shared,
                    WaveFormat = new WaveFormat(16000, 16, 1)
                };
                capture.DataAvailable += (_, e) => receive(e.Buffer, e.BytesRecorded);
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is null) stopped.TrySetResult();
                    else stopped.TrySetException(e.Exception);
                };
            }
            catch { device.Dispose(); throw; }
        }
        catch { enumerator.Dispose(); throw; }
    }

    public Task Stopped => stopped.Task;
    public void Start() => capture.StartRecording();
    public void Stop() => capture.StopRecording();

    public void Dispose()
    {
        capture.Dispose();
        device.Dispose();
        enumerator.Dispose();
    }

    public static List<CaptureDevice> Devices() => CaptureDevice.Active(DataFlow.Capture, "Windows 默认麦克风");

    public static int Peak(byte[] buffer, int length)
    {
        int peak = 0;
        for (int i = 0; i + 1 < length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2))));
        return peak * 100 / 32768;
    }
}
