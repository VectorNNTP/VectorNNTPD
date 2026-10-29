using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Emits canonical Path observations through the dedicated Serilog Path-survey pipeline.
    /// </summary>
    /// <remarks>
    /// File path, rolling, retention, compression, and buffering come from
    /// <c>Serilog:Inpaths</c>. The Path line is produced only by
    /// <see cref="InnPathSurveyTextFormatter"/>. This writer does not open, rotate, or
    /// compress files itself. Serilog host disposal flushes the pipeline. Path
    /// bytes are copied at <see cref="Write"/> so later ArtData release cannot
    /// mutate a buffered event. Observations are not retained in an in-memory
    /// collection; the Path-survey file is the persistence mechanism.
    /// </remarks>
    public sealed class SerilogPathSurveyWriter : IPathSurveyWriter, IDisposable
    {
        private readonly Serilog.Core.Logger _logger;

        /// <summary>Initializes a writer that owns a dedicated Serilog Path-survey logger.</summary>
        public SerilogPathSurveyWriter(
            IConfiguration configuration,
            ICompletedPathSurveyFileHandler? completedFileHandler = null,
            ILogger<SerilogPathSurveyWriter>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _logger = NntpdPathSurveyLogging.CreateLogger(
                configuration,
                completedFileHandler ?? NullCompletedPathSurveyFileHandler.Instance,
                logger ?? NullLogger<SerilogPathSurveyWriter>.Instance);
        }

        /// <summary>Initializes a writer that uses an already-constructed Path-survey logger (tests).</summary>
        public SerilogPathSurveyWriter(Serilog.Core.Logger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <inheritdoc />
        public void Write(ReadOnlySpan<byte> canonicalPath)
        {
            var path = canonicalPath.IsEmpty
                ? string.Empty
                : Encoding.Latin1.GetString(canonicalPath);
            _logger
                .ForContext(NntpdPathSurveyLogging.PathProperty, path)
                .Information("inpaths");
        }

        /// <inheritdoc />
        public void Flush() => _logger.Dispose();

        /// <inheritdoc />
        public void Dispose() => _logger.Dispose();
    }
}
