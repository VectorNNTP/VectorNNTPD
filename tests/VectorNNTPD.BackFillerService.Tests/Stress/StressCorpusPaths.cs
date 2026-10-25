namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Fixed corpus locations for BackFiller stress Message-ID selection.
    /// </summary>
    /// <remarks>
    /// The required news-log corpus is the sibling VectorNNTP artefacts tree. Stress must not
    /// silently substitute <c>VectorNNTPD/.artifacts/newslogs</c>, generate IDs, or reuse an
    /// unprovenanced workload file.
    /// </remarks>
    public static class StressCorpusPaths
    {
        /// <summary>
        /// Required absolute news-log corpus. Source of truth for live stress Message-IDs.
        /// </summary>
        public const string RequiredNewsLogDirectory =
            @"C:\Users\chrisk\source\repos\VectorNNTP\.artifacts\newslogs";

        /// <summary>Manifest file name written beside workload partitions.</summary>
        public const string WorkloadManifestFileName = "corpus-manifest.json";

        /// <summary>Selected-ID audit file (one Message-ID per line, same order as main partition).</summary>
        public const string MainIdsAuditFileName = "main-message-ids.txt";
    }
}
