using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Resolves total physical RAM from the operating system for host and retention startup validation.
    /// </summary>
    /// <remarks>
    /// Linux reads <c>/proc/meminfo</c> <c>MemTotal</c> (kB × 1024).
    /// Windows uses <c>GlobalMemoryStatusEx</c> <c>ullTotalPhys</c>.
    /// There is no GC, available-memory, or cgroup fallback.
    /// <see cref="BackFillerOptionsValidator"/> requires at least
    /// <see cref="BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes"/>.
    /// </remarks>
    internal sealed partial class OsPhysicalMemoryProvider : IPhysicalMemoryProvider
    {
        /// <summary>Linux meminfo path passed to <see cref="ReadLinuxMemTotalBytes"/> by <see cref="GetTotalPhysicalMemoryBytes"/>.</summary>
        private const string LinuxMemInfoPath = "/proc/meminfo";

        /// <inheritdoc />
        public long GetTotalPhysicalMemoryBytes()
        {
            if (OperatingSystem.IsLinux())
            {
                return ReadLinuxMemTotalBytes(LinuxMemInfoPath);
            }

            if (OperatingSystem.IsWindows())
            {
                return ReadWindowsTotalPhysicalMemoryBytes();
            }

            throw new PlatformNotSupportedException(
                "Total physical memory detection is supported only on Linux and Windows.");
        }

        /// <summary>
        /// Reads <c>MemTotal</c> from a Linux <c>/proc/meminfo</c>-shaped file and converts kB to bytes.
        /// </summary>
        /// <param name="memInfoPath">Path to the meminfo file.</param>
        /// <returns>Total physical memory in bytes.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="memInfoPath"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="memInfoPath"/> is empty or white space.</exception>
        /// <exception cref="InvalidOperationException">
        /// The file is missing, cannot be read, access is denied, or its <c>MemTotal</c> entry cannot be parsed.
        /// Read and access failures include the original exception as the inner exception.
        /// </exception>
        internal static long ReadLinuxMemTotalBytes(string memInfoPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memInfoPath);
            if (!File.Exists(memInfoPath))
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: file does not exist.");
            }

            try
            {
                using var reader = File.OpenText(memInfoPath);
                return ParseLinuxMemTotalBytes(reader);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: file could not be read.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: access was denied.",
                    ex);
            }
        }

        /// <summary>
        /// Parses <c>MemTotal</c> from <c>/proc/meminfo</c> text. Other keys are ignored.
        /// </summary>
        /// <param name="reader">meminfo text. Not disposed by this method.</param>
        /// <returns>Total physical memory in bytes.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// No <c>MemTotal</c> line is present, its unit is not <c>kB</c>, its value is not numeric, or the kB-to-byte conversion overflows.
        /// Overflow includes the <see cref="OverflowException"/> as the inner exception. Other keys are ignored.
        /// </exception>
        internal static long ParseLinuxMemTotalBytes(TextReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !parts[2].Equals("kB", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Unable to determine total physical memory from /proc/meminfo: MemTotal entry format is invalid.");
                }

                if (!ulong.TryParse(parts[1], out var kibibytes))
                {
                    throw new InvalidOperationException(
                        "Unable to determine total physical memory from /proc/meminfo: MemTotal value is not numeric.");
                }

                try
                {
                    return ToSignedPhysicalBytes(checked(kibibytes * 1024UL), "MemTotal");
                }
                catch (OverflowException ex)
                {
                    throw new InvalidOperationException(
                        "Unable to determine total physical memory from /proc/meminfo: MemTotal value overflowed byte conversion.",
                        ex);
                }
            }

            throw new InvalidOperationException(
                "Unable to determine total physical memory from /proc/meminfo: MemTotal entry was not found.");
        }

        /// <summary>
        /// Selects Windows total physical RAM. <paramref name="ullAvailPhys"/> is accepted only so tests
        /// can prove available memory is not the source.
        /// </summary>
        /// <param name="ullTotalPhys"><c>MEMORYSTATUSEX.ullTotalPhys</c>.</param>
        /// <param name="ullAvailPhys"><c>MEMORYSTATUSEX.ullAvailPhys</c>; unused.</param>
        /// <returns>Total physical memory in bytes.</returns>
        /// <exception cref="InvalidOperationException"><paramref name="ullTotalPhys"/> is zero or greater than <see cref="long.MaxValue"/>.</exception>
        internal static long SelectWindowsTotalPhysicalMemoryBytes(ulong ullTotalPhys, ulong ullAvailPhys)
        {
            _ = ullAvailPhys;
            return ToSignedPhysicalBytes(ullTotalPhys, "ullTotalPhys");
        }

        /// <summary>Converts an unsigned physical-byte count to a positive <see cref="long"/>.</summary>
        /// <param name="bytes">Candidate byte count.</param>
        /// <param name="fieldName">Source field name written into the failure message.</param>
        /// <returns><paramref name="bytes"/> as a signed value.</returns>
        /// <exception cref="InvalidOperationException"><paramref name="bytes"/> is zero or greater than <see cref="long.MaxValue"/>.</exception>
        private static long ToSignedPhysicalBytes(ulong bytes, string fieldName)
        {
            if (bytes == 0)
            {
                throw new InvalidOperationException(
                    $"Unable to determine total physical memory: {fieldName} was zero.");
            }

            if (bytes > long.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Unable to determine total physical memory: {fieldName} exceeds the signed 64-bit byte range.");
            }

            return (long)bytes;
        }

        /// <summary>Reads <c>ullTotalPhys</c> through <c>GlobalMemoryStatusEx</c>.</summary>
        /// <returns>Total physical memory in bytes.</returns>
        /// <exception cref="InvalidOperationException">The native call returns false. The inner exception is the Win32 error.</exception>
        /// <remarks>Sets <see cref="MemoryStatusEx.dwLength"/> to the marshalled size before the call. Supported on Windows.</remarks>
        [SupportedOSPlatform("windows")]
        private static long ReadWindowsTotalPhysicalMemoryBytes()
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (!GlobalMemoryStatusEx(ref status))
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from GlobalMemoryStatusEx.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return SelectWindowsTotalPhysicalMemoryBytes(status.ullTotalPhys, status.ullAvailPhys);
        }

        /// <summary>Sequential layout matching native <c>MEMORYSTATUSEX</c>. Field order and widths are the Win32 contract.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            /// <summary>Structure size in bytes. The caller sets this before <see cref="GlobalMemoryStatusEx"/>.</summary>
            internal uint dwLength;

            /// <summary>Approximate memory load percentage filled by the API. Not read by this provider.</summary>
            private uint dwMemoryLoad;

            /// <summary>Total physical memory in bytes. This is the value the provider returns.</summary>
            internal ulong ullTotalPhys;

            /// <summary>Available physical memory in bytes. Passed through and then ignored.</summary>
            internal ulong ullAvailPhys;

            /// <summary>Total page-file bytes. Present so the marshalled size matches <c>MEMORYSTATUSEX</c>. Not read.</summary>
            private ulong ullTotalPageFile;

            /// <summary>Available page-file bytes. Present so the marshalled size matches <c>MEMORYSTATUSEX</c>. Not read.</summary>
            private ulong ullAvailPageFile;

            /// <summary>Total virtual bytes. Present so the marshalled size matches <c>MEMORYSTATUSEX</c>. Not read.</summary>
            private ulong ullTotalVirtual;

            /// <summary>Available virtual bytes. Present so the marshalled size matches <c>MEMORYSTATUSEX</c>. Not read.</summary>
            private ulong ullAvailVirtual;

            /// <summary>Available extended virtual bytes. Present so the marshalled size matches <c>MEMORYSTATUSEX</c>. Not read.</summary>
            private ulong ullAvailExtendedVirtual;
        }

        /// <summary>
        /// Calls Win32 <c>GlobalMemoryStatusEx</c>. Returns <see langword="false"/> and sets the last Win32 error on failure. Does not throw.
        /// </summary>
        /// <param name="lpBuffer">Status buffer whose <see cref="MemoryStatusEx.dwLength"/> is already the marshalled size.</param>
        /// <returns><see langword="true"/> when <paramref name="lpBuffer"/> was filled.</returns>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SupportedOSPlatform("windows")]
        private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
    }
}
