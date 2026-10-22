using System.Runtime.InteropServices;

namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Creates one directory with <c>CreateDirectoryW</c> or <c>mkdir</c>.
    /// An existing directory is <see cref="SpoolDirectoryCreateResult.AlreadyPresent"/> without a prior existence probe.
    /// </summary>
    internal sealed class FileSpoolShardDirectorySystem : ISpoolShardDirectorySystem
    {
        private const int ErrorAlreadyExists = 183;
        private const int ErrnoEexist = 17;
        private const uint FileAttributeDirectory = 0x10;
        private const uint InvalidFileAttributes = 0xFFFF_FFFF;
        private const uint MkdirMode = 0x1FF;

        /// <inheritdoc />
        public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateWindows(fullPath);
            }

            if (OperatingSystem.IsLinux())
            {
                return CreateLinux(fullPath);
            }

            throw new PlatformNotSupportedException("Spool shard directories are created on Windows and Linux.");
        }

        /// <summary>Creates a directory with <c>CreateDirectoryW</c> and captures the Win32 error immediately.</summary>
        private static SpoolDirectoryCreateResult CreateWindows(string fullPath)
        {
            if (CreateDirectoryW(fullPath, IntPtr.Zero))
            {
                return SpoolDirectoryCreateResult.Created;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists && IsDirectoryWindows(fullPath))
            {
                return SpoolDirectoryCreateResult.AlreadyPresent;
            }

            throw new IOException("CreateDirectoryW failed for '" + fullPath + "': Win32 error " + error + ".");
        }

        /// <summary>Creates a directory with <c>mkdir</c>. <c>EEXIST</c> on a directory is success.</summary>
        private static SpoolDirectoryCreateResult CreateLinux(string fullPath)
        {
            if (mkdir(fullPath, MkdirMode) == 0)
            {
                return SpoolDirectoryCreateResult.Created;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrnoEexist && IsDirectory(fullPath))
            {
                return SpoolDirectoryCreateResult.AlreadyPresent;
            }

            throw new IOException("mkdir failed for '" + fullPath + "': errno " + error + ".");
        }

        private static bool IsDirectoryWindows(string fullPath)
        {
            var attributes = GetFileAttributesW(fullPath);
            return attributes != InvalidFileAttributes && (attributes & FileAttributeDirectory) != 0;
        }

        private static bool IsDirectory(string fullPath)
        {
            try
            {
                return (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Creates one directory. Failure leaves the Win32 error for the caller to read immediately.</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
        private static extern bool CreateDirectoryW(string path, IntPtr security);

        /// <summary>Reads file attributes. <c>FILE_ATTRIBUTE_DIRECTORY</c> distinguishes an existing directory from a file.</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
        private static extern uint GetFileAttributesW(string path);

        /// <summary>Creates one directory. <c>EEXIST</c> is success only when the path is already a directory.</summary>
        [DllImport("libc", SetLastError = true)]
        private static extern int mkdir(string path, uint mode);
    }
}
