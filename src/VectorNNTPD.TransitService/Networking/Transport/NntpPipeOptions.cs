using System.IO.Pipelines;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// Centralized <see cref="PipeOptions"/> defaults for NNTP connection transports.
    /// </summary>
    /// <remarks>
    /// Per-connection thresholds (not global). Values are engineering defaults for bounded
    /// memory under many concurrent clients; they are not a claim about 10/40 Gbps throughput.
    /// </remarks>
    public static class NntpPipeOptions
    {
        /// <summary>Pause the pipe writer (and thus receive) when unread bytes reach this size.</summary>
        public const long PauseWriterThreshold = 64 * 1024;

        /// <summary>Resume the pipe writer after unread bytes fall to this size.</summary>
        public const long ResumeWriterThreshold = 32 * 1024;

        /// <summary>Minimum pooled segment size for pipe buffers.</summary>
        public const int MinimumSegmentSize = 4 * 1024;

        /// <summary>Creates reader/writer options for the input (RX) side of a duplex connection transport.</summary>
        public static PipeOptions Create() =>
            CreateCore(PauseWriterThreshold, ResumeWriterThreshold, PipeScheduler.ThreadPool, PipeScheduler.ThreadPool);

        /// <summary>
        /// TX output-pipe options. Same thresholds as <see cref="Create"/> unless
        /// <see cref="TxPipePauseExperiment"/> is configured. Reader and writer schedulers
        /// are <see cref="PipeScheduler.Inline"/>. Input/RX pipes must keep using
        /// <see cref="Create"/>.
        /// </summary>
        internal static PipeOptions CreateOutput()
        {
            if (TxPipePauseExperiment.TryGetEffective(out var pause, out var resume))
            {
                return CreateCore(pause, resume, PipeScheduler.Inline, PipeScheduler.Inline);
            }

            return CreateCore(PauseWriterThreshold, ResumeWriterThreshold, PipeScheduler.Inline, PipeScheduler.Inline);
        }

        private static PipeOptions CreateCore(
            long pauseWriterThreshold,
            long resumeWriterThreshold,
            PipeScheduler readerScheduler,
            PipeScheduler writerScheduler) =>
            new(
                pool: null,
                readerScheduler: readerScheduler,
                writerScheduler: writerScheduler,
                pauseWriterThreshold: pauseWriterThreshold,
                resumeWriterThreshold: resumeWriterThreshold,
                minimumSegmentSize: MinimumSegmentSize,
                useSynchronizationContext: false);
    }
}
