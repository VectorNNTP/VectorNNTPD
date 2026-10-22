using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// Current-thread CPU time. Elapsed waits do not increase this. Windows only; other platforms return zeros.
    /// </summary>
    /// <remarks>
    /// Uses Win32 OS thread identity and the current-thread pseudo-handle. Managed thread ids are not
    /// interchangeable with <c>GetCurrentThreadId</c>, and a <see cref="System.Diagnostics.ProcessThread"/>
    /// handle is not equivalent to <c>GetCurrentThread</c>.
    /// </remarks>
    internal static partial class ThreadCpuTimes
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

        /// <summary>Returns the calling thread's OS thread id.</summary>
        [LibraryImport("kernel32.dll")]
        [SupportedOSPlatform("windows")]
        private static partial int GetCurrentThreadId();

        /// <summary>Returns a pseudo-handle for the calling thread. The caller does not close it.</summary>
        [LibraryImport("kernel32.dll")]
        [SupportedOSPlatform("windows")]
        private static partial nint GetCurrentThread();

        /// <summary>Reads creation, exit, kernel, and user times. Kernel and user are 100 ns units.</summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SupportedOSPlatform("windows")]
        private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
    }
}
