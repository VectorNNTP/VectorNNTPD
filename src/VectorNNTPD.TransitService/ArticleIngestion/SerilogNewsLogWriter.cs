using System.Text;
using Microsoft.Extensions.Configuration;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Emits already-decided news events through the dedicated Serilog <c>news</c> pipeline.
    /// </summary>
    /// <remarks>
    /// File path, rolling, retention, compression, and buffering come from
    /// <c>Serilog:News</c>. The INN line is produced only by
    /// <see cref="InnNewsTextFormatter"/>. This writer does not open, rotate, or
    /// compress files itself. Serilog host disposal flushes the pipeline.
    /// </remarks>
    public sealed class SerilogNewsLogWriter : INewsLogWriter, IDisposable
    {
        private readonly Serilog.Core.Logger _logger;

        /// <summary>Initializes a writer that owns a dedicated Serilog news logger.</summary>
        public SerilogNewsLogWriter(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _logger = NntpdNewsLogging.CreateLogger(configuration);
        }

        /// <summary>Initializes a writer that uses an already-constructed news logger (tests).</summary>
        public SerilogNewsLogWriter(Serilog.Core.Logger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <inheritdoc />
        public void Write(in NewsLogEvent evt)
        {
            var messageId = Encoding.ASCII.GetString(evt.MessageId.Span);
            var feed = evt.Feed.IsEmpty ? "?" : Encoding.ASCII.GetString(evt.Feed.Span);
            var sites = evt.Sites.IsEmpty ? string.Empty : Encoding.ASCII.GetString(evt.Sites.Span);
            var reason = evt.Reason.IsEmpty ? string.Empty : Encoding.ASCII.GetString(evt.Reason.Span);
            _logger
                .ForContext(NntpdNewsLogging.DispositionProperty, (char)evt.Disposition)
                .ForContext(NntpdNewsLogging.FeedProperty, feed)
                .ForContext(NntpdNewsLogging.MessageIdProperty, messageId)
                .ForContext(NntpdNewsLogging.SitesProperty, sites)
                .ForContext(NntpdNewsLogging.SizeProperty, evt.Size)
                .ForContext(NntpdNewsLogging.ResponseCodeProperty, evt.ResponseCode)
                .ForContext(NntpdNewsLogging.ReasonProperty, reason)
                .Information("news");
        }

        /// <inheritdoc />
        public void Flush() => _logger.Dispose();

        /// <inheritdoc />
        public void Dispose() => _logger.Dispose();
    }
}
