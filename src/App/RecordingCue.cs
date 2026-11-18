using System.Runtime.InteropServices;

namespace VrcTranslator.Desktop;

internal sealed partial class RecordingCue : IDisposable
{
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;
    private const uint Filename = 0x00020000;
    private bool started;

    public void Begin()
    {
        started = true;
        Play("recording-start.wav");
    }

    public void Dispose()
    {
        if (!started) return;
        started = false;
        Play("recording-stop.wav");
    }

    private static void Play(string name) =>
        PlaySound(Path.Combine(AppContext.BaseDirectory, "Sounds", name), nint.Zero, Filename | Async | NoDefault);

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string sound, nint module, uint flags);
}
