using System.Text;
using Serilog.Events;
using Serilog.Formatting;
using VectorNNTPD.ReaderService.ArticleIngestion;

namespace VectorNNTPD.ReaderService.Logging
{
    /// <summary>
    /// Application-owned Serilog formatter for the INN <c>news</c> line.
    /// </summary>
    /// <remarks>
    /// The INN format is an application invariant. This type ignores the log
    /// event message template and any configured <c>outputTemplate</c>. Operators
    /// cannot change field order, timestamp representation, or delimiters through
    /// appsettings.
    /// </remarks>
    public sealed class InnNewsTextFormatter : ITextFormatter
    {
        /// <inheritdoc />
        public void Format(LogEvent logEvent, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            ArgumentNullException.ThrowIfNull(output);

            var disposition = ReadDisposition(logEvent);
            var messageId = ReadUtf8Property(logEvent, NntpdNewsLogging.MessageIdProperty);
            var feed = ReadUtf8Property(logEvent, NntpdNewsLogging.FeedProperty);
            var sites = ReadUtf8Property(logEvent, NntpdNewsLogging.SitesProperty);
            var reason = ReadUtf8Property(logEvent, NntpdNewsLogging.ReasonProperty);
            var evt = new NewsLogEvent(
                disposition,
                messageId,
                feed,
                sites,
                logEvent.Timestamp,
                ReadResponseCode(logEvent),
                reason,
                ReadSize(logEvent));
            var required = NewsLogLineFormatter.RequiredLength(in evt);
            Span<byte> buffer = required <= 512 ? stackalloc byte[required] : new byte[required];
            var written = NewsLogLineFormatter.Write(buffer, in evt, logEvent.Timestamp);
            output.Write(Encoding.ASCII.GetString(buffer[..written]));
        }

        private static NewsLogDisposition ReadDisposition(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue(NntpdNewsLogging.DispositionProperty, out var value)
                || value is not ScalarValue { Value: char ch })
            {
                return NewsLogDisposition.Accepted;
            }

            return ch switch
            {
                (char)NewsLogDisposition.Junk => NewsLogDisposition.Junk,
                (char)NewsLogDisposition.Rejected => NewsLogDisposition.Rejected,
                (char)NewsLogDisposition.Moderated => NewsLogDisposition.Moderated,
                (char)NewsLogDisposition.Accepted => NewsLogDisposition.Accepted,
                _ => NewsLogDisposition.Accepted,
            };
        }

        private static int ReadResponseCode(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue(NntpdNewsLogging.ResponseCodeProperty, out var value)
                || value is not ScalarValue scalar)
            {
                return 0;
            }

            return scalar.Value switch
            {
                int code => code,
                long code => (int)code,
                _ => 0,
            };
        }

        private static int ReadSize(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue(NntpdNewsLogging.SizeProperty, out var value)
                || value is not ScalarValue scalar)
            {
                return 0;
            }

            return scalar.Value switch
            {
                int size => size,
                long size => (int)size,
                _ => 0,
            };
        }

        private static ReadOnlyMemory<byte> ReadUtf8Property(LogEvent logEvent, string name)
        {
            if (!logEvent.Properties.TryGetValue(name, out var value)
                || value is not ScalarValue { Value: string text }
                || text.Length == 0)
            {
                return default;
            }

            return Encoding.ASCII.GetBytes(text);
        }
    }
}
