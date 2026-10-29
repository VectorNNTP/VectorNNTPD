using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.Ninpaths
{
    /// <summary>
    /// Rotation-hook handoff: opens the completed Path-survey file and enqueues it
    /// for background ninpaths processing. Does not parse the file.
    /// </summary>
    /// <remarks>
    /// The stream is opened with <see cref="FileShare.ReadWrite"/> and
    /// <see cref="FileShare.Delete"/> so gzip and deletion of the completed file can proceed
    /// while the worker still reads. Failures are logged and never thrown to rotation.
    /// </remarks>
    public sealed class NinpathsCompletedFileHandler : ICompletedPathSurveyFileHandler
    {
        private readonly NinpathsProcessingService _processor;
        private readonly IOptions<NntpdOptions> _options;
        private readonly ILogger<NinpathsCompletedFileHandler> _logger;

        /// <summary>Initializes a handler that enqueues completed files onto <paramref name="processor"/>.</summary>
        public NinpathsCompletedFileHandler(
            NinpathsProcessingService processor,
            IOptions<NntpdOptions> options,
            ILogger<NinpathsCompletedFileHandler> logger)
        {
            ArgumentNullException.ThrowIfNull(processor);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _processor = processor;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public void OnCompletedFile(string completedFilePath)
        {
            if (string.IsNullOrWhiteSpace(completedFilePath))
            {
                return;
            }

            if (!NinpathsTop1000.IsEnabled(_options.Value))
            {
                NinpathsLogMessages.Disabled(_logger, completedFilePath);
                return;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(
                    completedFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    NinpathsConstants.ReadBufferSize,
                    FileOptions.SequentialScan);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                NinpathsLogMessages.OpenFailed(_logger, ex, completedFilePath);
                return;
            }

            // codeql[cs/missed-using-statement] On successful TryEnqueue ownership transfers to NinpathsProcessingService; using would dispose an enqueued file. finally disposes only when enqueue fails or throws.
            var file = new NinpathsCompletedFile(completedFilePath, stream);
            var enqueued = false;
            try
            {
                enqueued = _processor.TryEnqueue(file);
            }
            finally
            {
                if (!enqueued)
                {
                    file.Dispose();
                }
            }
        }
    }
}
