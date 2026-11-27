using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class ProcessExitHandle : WaitHandle
{
    private ProcessExitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;

    public static ProcessExitHandle? TryOpen(int processId, string processLabel, bool ignoreAccessDenied = false)
    {
        const uint synchronize = 0x00100000;
        var handle = OpenProcess(synchronize, false, processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == 87) return null; // ERROR_INVALID_PARAMETER: the PID no longer exists.
            if (ignoreAccessDenied && error == 5) return null; // ERROR_ACCESS_DENIED: elevated process.
            throw new ServiceException($"无法监视 {processLabel} 进程退出（Win32 {error}）。");
        }
        var wait = new ProcessExitHandle(handle);
        if (!wait.WaitOne(0)) return wait;
        wait.Dispose();
        return null;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
}
