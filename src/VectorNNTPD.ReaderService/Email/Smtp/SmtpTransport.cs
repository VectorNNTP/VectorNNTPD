using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>
    /// Native SMTP client using <see cref="System.Net.Sockets.TcpClient"/> and
    /// <see cref="System.Net.Security.SslStream"/>. One connection per delivery.
    /// </summary>
    /// <remarks>
    /// Does not use <c>System.Net.Mail.SmtpClient</c> or a third-party SMTP library.
    /// Connection reuse across messages is supported by <see cref="SmtpConnection"/>
    /// but this transport opens a fresh session per send so the delivery worker is not
    /// coupled to a single remote connection.
    /// EHLO/HELO uses the application FQDN from <see cref="NntpdOptions.Fqdn"/>.
    /// Certificate validation is mandatory. Tests may supply extra trusted roots;
    /// there is no production bypass switch.
    /// </remarks>
    public sealed class SmtpTransport : ISmtpTransport
    {
        private readonly IOptions<EmailOptions> _options;
        private readonly IOptions<NntpdOptions> _nntpd;
        private readonly ILogger<SmtpTransport> _logger;
        private readonly IReadOnlyCollection<X509Certificate2>? _extraTrustedRoots;

        /// <summary>Initializes a new SMTP transport.</summary>
        public SmtpTransport(
            IOptions<EmailOptions> options,
            IOptions<NntpdOptions> nntpd,
            ILogger<SmtpTransport> logger)
            : this(options, nntpd, logger, extraTrustedRoots: null)
        {
        }

        /// <summary>
        /// Initializes a transport that trusts additional roots for certificate
        /// validation (tests only). Production DI uses the three-argument constructor.
        /// </summary>
        public SmtpTransport(
            IOptions<EmailOptions> options,
            IOptions<NntpdOptions> nntpd,
            ILogger<SmtpTransport> logger,
            IReadOnlyCollection<X509Certificate2>? extraTrustedRoots)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(nntpd);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _nntpd = nntpd;
            _logger = logger;
            _extraTrustedRoots = extraTrustedRoots;
        }

        /// <inheritdoc />
        public async Task<SmtpSendResult> SendAsync(EmailWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            var smtp = _options.Value.Smtp ?? new SmtpOptions();
            var ehlo = _nntpd.Value.Fqdn;
            if (string.IsNullOrWhiteSpace(ehlo))
            {
                throw new SmtpException(SmtpFailureKind.Protocol, "Application FQDN is required for SMTP EHLO.");
            }

            EmailLogMessages.SmtpSessionStarting(
                _logger,
                smtp.Host,
                smtp.Port,
                smtp.Security.ToString());

            await using var connection = new SmtpConnection(smtp, ehlo, _extraTrustedRoots);
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await connection.EhloAsync(cancellationToken).ConfigureAwait(false);

            if (smtp.Security == SmtpSecurityMode.StartTls)
            {
                await connection.StartTlsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (connection.Capabilities.MaxSize is { } maxSize
                && item.EncodedMessage.Length > maxSize)
            {
                throw new SmtpException(
                    SmtpFailureKind.Message,
                    "Encoded message exceeds the server SIZE limit.");
            }

            await connection.AuthenticateAsync(cancellationToken).ConfigureAwait(false);

            var mail = await connection
                .MailFromAsync(item.EnvelopeSender.Address, cancellationToken)
                .ConfigureAwait(false);
            if (!mail.IsPositiveCompletion)
            {
                throw SmtpConnection.Classify(mail, "MAIL FROM was rejected.");
            }

            var recipients = new List<SmtpRecipientResult>(item.Recipients.Count);
            var accepted = 0;
            var transient = 0;
            var permanent = 0;
            foreach (var recipient in item.Recipients)
            {
                var rcpt = await connection.RcptToAsync(recipient.Address, cancellationToken).ConfigureAwait(false);
                var ok = rcpt.IsPositiveCompletion;
                if (ok)
                {
                    accepted++;
                }
                else if (rcpt.IsTransientNegative)
                {
                    transient++;
                }
                else
                {
                    permanent++;
                }

                recipients.Add(new SmtpRecipientResult(recipient, ok, rcpt.Code, rcpt.Text));
            }

            if (accepted == 0)
            {
                if (transient > 0 && permanent == 0)
                {
                    throw new SmtpException(SmtpFailureKind.Transient, "All recipients were deferred (4xx).")
                    {
                        StatusCode = recipients[0].StatusCode,
                    };
                }

                throw new SmtpException(SmtpFailureKind.Permanent, "All recipients were rejected.")
                {
                    StatusCode = recipients[0].StatusCode,
                };
            }

            var data = await connection.DataAsync(item.EncodedMessage, cancellationToken).ConfigureAwait(false);
            await connection.QuitAsync(cancellationToken).ConfigureAwait(false);

            if (!data.IsPositiveCompletion)
            {
                throw SmtpConnection.Classify(data, "DATA was not accepted.");
            }

            if (accepted != item.Recipients.Count)
            {
                throw new SmtpException(
                    SmtpFailureKind.Partial,
                    "Some recipients were rejected after others were accepted.")
                {
                    StatusCode = data.Code,
                };
            }

            EmailLogMessages.SmtpSessionCompleted(
                _logger,
                smtp.Host,
                smtp.Port,
                accepted);
            return new SmtpSendResult
            {
                Succeeded = true,
                Recipients = recipients,
                DataStatusCode = data.Code,
            };
        }
    }
}
