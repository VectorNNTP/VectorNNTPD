using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>Filesystem outbound email spool settings nested under <see cref="EmailOptions"/>.</summary>
    public sealed class EmailSpoolOptions
    {
        /// <summary>Default application-relative spool directory.</summary>
        public const string DefaultDirectory = "spool/smtp";

        /// <summary>
        /// Gets or sets the spool directory. Relative paths resolve through Common
        /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
        /// <see cref="AppContext.BaseDirectory"/>. Default <see cref="DefaultDirectory"/>.
        /// Created automatically when email is enabled.
        /// </summary>
        public string Directory { get; set; } = DefaultDirectory;

        /// <summary>
        /// Gets or sets how long shutdown waits for an in-flight SMTP operation.
        /// Default 15 seconds. Pending <c>.eml</c> files are left on disk.
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Gets or sets how often the worker rescans the spool when idle.
        /// Default 1 second. A write also wakes the worker.
        /// </summary>
        public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}
