using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Moderation
{
    /// <summary>
    /// Builds a standards-defined moderator email from a proto-article.
    /// </summary>
    /// <remarks>
    /// RFC 5537 §3.5.1 / §4.1: the proto-article is encapsulated as
    /// <c>application/news-transmission; usage=moderate</c>. Injection-Info,
    /// Injection-Date, Path, and X-Trace are not added here — forwarding happens
    /// before those headers are written.
    /// </remarks>
    public sealed class ModerationEmailComposer
    {
        private readonly IOptions<EmailOptions> _emailOptions;

        /// <summary>Initializes a new composer.</summary>
        public ModerationEmailComposer(IOptions<EmailOptions> emailOptions)
        {
            ArgumentNullException.ThrowIfNull(emailOptions);
            _emailOptions = emailOptions;
        }

        /// <summary>Creates the outbound moderation email. Does not send it.</summary>
        public EmailMessage Compose(ModerationSubmission submission)
        {
            ArgumentNullException.ThrowIfNull(submission);
            if (!EmailOptionsValidator.TryValidateMailbox(submission.ModeratorAddress, out var moderator))
            {
                throw new ArgumentException("Moderator address is not a mailbox.", nameof(submission));
            }

            var options = _emailOptions.Value;
            if (!EmailOptionsValidator.TryValidateMailbox(options.DefaultFrom, out var fromMailbox)
                && !EmailOptionsValidator.TryValidateMailbox(options.EnvelopeSender, out fromMailbox))
            {
                throw new InvalidOperationException("Email:DefaultFrom must be a mailbox when composing moderation mail.");
            }

            EmailAddress? envelope = null;
            if (EmailOptionsValidator.TryValidateMailbox(options.EnvelopeSender, out var envelopeMailbox))
            {
                envelope = new EmailAddress(envelopeMailbox);
            }

            var proto = DestuffProtoArticle(submission.ProtoArticle.Span);
            var subject = submission.TargetModeratedGroup + " " + submission.MessageId;
            return new EmailMessage
            {
                From = new EmailAddress(fromMailbox),
                To = [new EmailAddress(moderator)],
                EnvelopeSender = envelope,
                Subject = subject,
                Body = proto,
                ContentType = "application/news-transmission; usage=moderate",
                Charset = "us-ascii",
                CorrelationId = submission.MessageId,
                Headers =
                [
                    new EmailHeader("X-Moderated-Newsgroup", submission.TargetModeratedGroup),
                    new EmailHeader("X-Original-Message-ID", submission.MessageId),
                    new EmailHeader(
                        "X-Submitting-Sender",
                        string.IsNullOrEmpty(submission.AuthenticatedUsername)
                            ? "unauthenticated"
                            : submission.AuthenticatedUsername),
                ],
            };
        }

        /// <summary>Destuffs NNTP proto-article wire into a complete article (CRLF lines).</summary>
        internal static byte[] DestuffProtoArticle(ReadOnlySpan<byte> stuffed)
        {
            if (stuffed.IsEmpty)
            {
                return [];
            }

            var output = new List<byte>(stuffed.Length);
            var offset = 0;
            while (offset < stuffed.Length)
            {
                var remaining = stuffed[offset..];
                var crlf = remaining.IndexOf("\r\n"u8);
                ReadOnlySpan<byte> line;
                if (crlf < 0)
                {
                    line = remaining;
                    offset = stuffed.Length;
                }
                else
                {
                    line = remaining[..crlf];
                    offset += crlf + 2;
                }

                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    line = line[1..];
                }

                output.AddRange(line.ToArray());
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            return [.. output];
        }
    }
}
