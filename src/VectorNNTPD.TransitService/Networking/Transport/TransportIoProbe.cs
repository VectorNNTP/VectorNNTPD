namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// DIAGNOSTIC-ONLY. Off-by-default ConnectionByteTransport socket/Pipe cadence sampler.
    /// </summary>
    /// <remarks>
    /// Disabled unless <c>VECTORNNTP_TRANSPORT_IO</c> is set or <see cref="Enable"/> is called.
    /// When disabled, no session state is allocated and transport I/O is unchanged.
    /// This is not a production architecture change.
    /// </remarks>
    internal static class TransportIoProbe
    {
        /// <summary>Environment variable that enables transport I/O cadence sampling.</summary>
        public const string EnvironmentVariableName = "VECTORNNTP_TRANSPORT_IO";

        private static ProbeState? _state;

        static TransportIoProbe()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var dir = value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                      value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? DefaultDirectory()
                : value;
            Enable(dir);
        }

        /// <summary>Gets whether cadence timestamps should be recorded.</summary>
        public static bool IsEnabled => Volatile.Read(ref _state) is not null;

        /// <summary>Gets the directory that receives connection dumps, or <see langword="null"/>.</summary>
        public static string? OutputDirectory => Volatile.Read(ref _state)?.Directory;

        /// <summary>Enables sampling and writes dumps under <paramref name="outputDirectory"/>.</summary>
        public static void Enable(string outputDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Volatile.Write(ref _state, new ProbeState(outputDirectory));
        }

        /// <summary>Disables sampling (tests).</summary>
        public static void Disable() => Volatile.Write(ref _state, null);

        /// <summary>Creates a per-connection log when enabled; otherwise <see langword="null"/>.</summary>
        public static TransportIoSession? CreateSession() =>
            IsEnabled ? new TransportIoSession() : null;

        /// <summary>
        /// Repo-local diagnostic dump directory.
        /// </summary>
        /// <remarks>
        /// Deliberately walks from the process working directory toward a folder that
        /// contains <c>VectorNNTPD.TransitService.sln</c> or <c>.artifacts/</c>. This is
        /// development-only output, off unless <see cref="EnvironmentVariableName"/> is set.
        /// It is not application-local runtime state and must not be used for certs/logs.
        /// </remarks>
        internal static string DefaultDirectory()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.TransitService.sln")) ||
                    Directory.Exists(Path.Join(dir.FullName, ".artifacts")))
                {
                    return Path.Join(dir.FullName, ".artifacts", "transport-io");
                }

                dir = dir.Parent;
            }

            return Path.Join(Directory.GetCurrentDirectory(), ".artifacts", "transport-io");
        }

        private sealed class ProbeState(string directory)
        {
            public string Directory { get; } = directory;
        }
    }
}
