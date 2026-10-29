using System.Text;
using Serilog.Events;
using Serilog.Formatting;
using VectorNNTPD.TransitService.ArticleIngestion;

namespace VectorNNTPD.TransitService.Logging
{
    /// <summary>
    /// Application-owned Serilog formatter for the INN-style Path-survey line.
    /// </summary>
    /// <remarks>
    /// The Path-survey format is an application invariant. This type ignores the
    /// log event message template and any configured <c>outputTemplate</c>.
    /// Operators cannot change the <c>Path: </c> prefix, field contents, or
    /// terminator through appsettings.
    /// </remarks>
    public sealed class InnPathSurveyTextFormatter : ITextFormatter
    {
        /// <inheritdoc />
        public void Format(LogEvent logEvent, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            ArgumentNullException.ThrowIfNull(output);

            var path = ReadPath(logEvent);
            var required = PathSurveyLineFormatter.RequiredLength(path.Span);
            Span<byte> buffer = required <= 1024 ? stackalloc byte[required] : new byte[required];
            var written = PathSurveyLineFormatter.Write(buffer, path.Span);
            output.Write(Encoding.Latin1.GetString(buffer[..written]));
        }

        private static ReadOnlyMemory<byte> ReadPath(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue(NntpdPathSurveyLogging.PathProperty, out var value)
                || value is not ScalarValue scalar
                || scalar.Value is null)
            {
                return default;
            }

            switch (scalar.Value)
            {
                case string text:
                    return text.Length == 0 ? default : Encoding.Latin1.GetBytes(text);
                case byte[] bytes:
                    return bytes;
                default:
                    return default;
            }
        }
    }
}
