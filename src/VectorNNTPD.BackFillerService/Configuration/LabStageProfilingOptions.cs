namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Lab-only Article Work stage profiler under <c>BackFiller:LabStageProfiling</c>.
    /// Default <see cref="Enabled"/> is false — production behaviour unchanged.
    /// </summary>
    internal sealed class LabStageProfilingOptions
    {
        /// <summary>Relative section name under <see cref="BackFillerOptions.SectionName"/>.</summary>
        internal const string SectionRelativeName = "LabStageProfiling";

        /// <summary>
        /// When true, installs <see cref="Diagnostics.ArticleWorkStageProfiler"/> and records stage histograms.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Optional directory for periodic JSON snapshots. Empty disables file export (profiler still records in-memory).
        /// </summary>
        public string? ExportDirectory { get; set; }

        /// <summary>Snapshot period when <see cref="ExportDirectory"/> is set. Default 2 seconds.</summary>
        public int ExportIntervalSeconds { get; set; } = 2;
    }
}
