using System.Globalization;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>Formats effective client identity and redacts secrets for NNTP command RX/TX logs.</summary>
    internal static class NntpCommandLogFormat
    {
        /// <summary>Formats <c>src-ip:src-port</c> from the session's effective client identity.</summary>
        public static string Client(NntpSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{session.ClientAddress}:{session.ClientPort}");
        }

        /// <summary>
        /// Returns whether per-command RX logging should be skipped for <paramref name="verb"/>.
        /// </summary>
        /// <remarks>
        /// Previously suppressed <see cref="NntpVerb.TakeThis"/> because console Information
        /// could not keep up. File logging is now Debug/Verbose; TAKETHIS diagnostics are restored.
        /// </remarks>
        public static bool SuppressHotPathCommand(NntpVerb verb)
        {
            _ = verb;
            return false;
        }

        /// <summary>
        /// Logging-boundary representation of a command. AUTHINFO secrets are redacted.
        /// </summary>
        /// <remarks>
        /// <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> /
        /// <see cref="Microsoft.Extensions.Logging.ILogger"/> cannot accept
        /// <see cref="ReadOnlySpan{T}"/> of bytes, and <c>byte[]</c> is not a useful operational
        /// log representation. This ASCII conversion is the explicit logging string boundary.
        /// Callers must check <see cref="Microsoft.Extensions.Logging.ILogger.IsEnabled"/> before
        /// invoking this method.
        /// </remarks>
        public static string RedactRxCommand(NntpCommand command, ReadOnlySpan<byte> line)
        {
            if (command.Verb == NntpVerb.AuthInfo && command.Qualifier == NntpVerb.Pass)
            {
                return "AUTHINFO PASS <redacted>";
            }

            if (command.Verb == NntpVerb.AuthInfo && command.Qualifier == NntpVerb.Sasl)
            {
                return "AUTHINFO SASL <redacted>";
            }

            return System.Text.Encoding.ASCII.GetString(line);
        }

        /// <summary>
        /// Returns a log-safe RX command line. Passwords and SASL credential material are redacted.
        /// </summary>
        public static string RedactRxLine(string rawLine)
        {
            ArgumentNullException.ThrowIfNull(rawLine);
            if (rawLine.Length == 0)
            {
                return rawLine;
            }

            // AUTHINFO PASS <secret> → AUTHINFO PASS <redacted>
            if (StartsWithCommand(rawLine, "AUTHINFO PASS"))
            {
                return "AUTHINFO PASS <redacted>";
            }

            // AUTHINFO SASL may carry an initial response with credentials.
            if (StartsWithCommand(rawLine, "AUTHINFO SASL"))
            {
                return "AUTHINFO SASL <redacted>";
            }

            return rawLine;
        }

        /// <summary>Extracts a display command name from a raw line when resolution is unavailable.</summary>
        public static string DisplayNameFromRawLine(string rawLine)
        {
            ArgumentNullException.ThrowIfNull(rawLine);
            if (rawLine.Length == 0)
            {
                return "INVALID";
            }

            if (StartsWithCommand(rawLine, "AUTHINFO PASS"))
            {
                return "AUTHINFO PASS";
            }

            if (StartsWithCommand(rawLine, "AUTHINFO SASL"))
            {
                return "AUTHINFO SASL";
            }

            if (StartsWithCommand(rawLine, "AUTHINFO USER"))
            {
                return "AUTHINFO USER";
            }

            if (StartsWithCommand(rawLine, "MODE READER"))
            {
                return "MODE READER";
            }

            if (StartsWithCommand(rawLine, "MODE STREAM"))
            {
                return "MODE STREAM";
            }

            if (StartsWithCommand(rawLine, "COMPRESS"))
            {
                return "COMPRESS";
            }

            var span = rawLine.AsSpan().Trim();
            var space = span.IndexOf(' ');
            return space <= 0 ? span.ToString().ToUpperInvariant() : span[..space].ToString().ToUpperInvariant();
        }

        /// <summary>
        /// Returns whether per-command RX/TX INFO traffic logging should be suppressed for this
        /// command name or raw command line.
        /// </summary>
        /// <remarks>
        /// Previously suppressed <c>TAKETHIS</c> so feed volume did not flood the console.
        /// Console is now Information-only; TAKETHIS Debug/Trace is restored for the file sink.
        /// </remarks>
        public static bool SuppressHotPathCommandLog(string commandOrRawLine)
        {
            ArgumentNullException.ThrowIfNull(commandOrRawLine);
            return false;
        }

        private static bool StartsWithCommand(string line, string command)
        {
            if (line.Length < command.Length)
            {
                return false;
            }

            if (!line.AsSpan(0, command.Length).Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return line.Length == command.Length || char.IsWhiteSpace(line[command.Length]);
        }
    }
}
