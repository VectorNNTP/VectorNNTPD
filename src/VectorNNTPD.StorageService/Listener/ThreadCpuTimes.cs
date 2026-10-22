using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// Current-thread CPU time. Elapsed waits do not increase this. Windows only; other platforms return zeros.
    /// </summary>
    internal static class ThreadCpuTimes
    {
        /// <summary>Gets kernel and user time for the calling thread, in 100 ns units.</summary>
        internal static (long Kernel, long User) Capture()
        {
            if (!OperatingSystem.IsWindows())
            {
                return (0, 0);
            }

            return CaptureWindows();
        }

        /// <summary>Calls Win32 <c>GetThreadTimes</c> on the calling thread.</summary>
        [SupportedOSPlatform("windows")]
        private static (long Kernel, long User) CaptureWindows()
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            {
                return (0, 0);
            }

            return (kernel, user);
        }

        /// <summary>Gets the operating-system thread id, or zero when this is not Windows.</summary>
        internal static int CurrentThreadId => OperatingSystem.IsWindows() ? CurrentThreadIdWindows() : 0;

        /// <summary>Calls Win32 <c>GetCurrentThreadId</c>.</summary>
        [SupportedOSPlatform("windows")]
        private static int CurrentThreadIdWindows() => GetCurrentThreadId();

        /// <summary>Returns the calling thread's id. The value matches <see cref="CurrentThreadId"/>.</summary>
        [DllImport("kernel32.dll")]
        [SupportedOSPlatform("windows")]
        private static extern int GetCurrentThreadId();

        /// <summary>Returns a pseudo-handle for the calling thread. The caller does not close it.</summary>
        [DllImport("kernel32.dll")]
        [SupportedOSPlatform("windows")]
        private static extern nint GetCurrentThread();

        /// <summary>Reads creation, exit, kernel, and user times. Kernel and user are 100 ns units.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SupportedOSPlatform("windows")]
        private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
    }
}
