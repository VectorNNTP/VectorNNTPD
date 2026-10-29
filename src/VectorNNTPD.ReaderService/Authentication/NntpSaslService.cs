using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>AUTHINFO SASL exchange: PLAIN, LOGIN, CRAM-MD5, and SCRAM-SHA-256.</summary>
    public sealed class NntpSaslService
    {
        private readonly INntpAuthenticationProvider _passwordProvider;
        private readonly MySqlNntpCredentialValidator? _mysql;
        private readonly string _fqdn;
        private readonly ScramDummyVerifier _dummyScram;

        /// <summary>Initializes a SASL service. SCRAM/CRAM require <paramref name="mysql"/>.</summary>
        public NntpSaslService(
            INntpAuthenticationProvider passwordProvider,
            MySqlNntpCredentialValidator? mysql = null,
            IOptions<NntpdOptions>? nntpd = null)
            : this(passwordProvider, mysql, nntpd, dummyScram: null)
        {
        }

        /// <summary>Initializes a SASL service with an injected dummy SCRAM verifier (tests).</summary>
        internal NntpSaslService(
            INntpAuthenticationProvider passwordProvider,
            MySqlNntpCredentialValidator? mysql,
            IOptions<NntpdOptions>? nntpd,
            ScramDummyVerifier? dummyScram)
        {
            ArgumentNullException.ThrowIfNull(passwordProvider);
            _passwordProvider = passwordProvider;
            _mysql = mysql;
            _fqdn = ResolveFqdn(nntpd?.Value);
            _dummyScram = dummyScram ?? ScramDummyVerifier.Create();
        }

        /// <summary>Starts AUTHINFO SASL. <paramref name="initialResponse"/> is the optional raw token.</summary>
        internal async ValueTask<NntpSaslReply> StartAsync(
            string mechanism,
            string initialResponse,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mechanism);
            ArgumentNullException.ThrowIfNull(initialResponse);
            ArgumentNullException.ThrowIfNull(clientIp);

            if (EqualsMechanism(mechanism, NntpAuthMechanisms.WirePlain))
            {
                if (IsEmptyInitialResponse(initialResponse))
                {
                    var exchange = new NntpSaslExchange(NntpAuthMechanisms.WirePlain) { WaitingPlainCredentials = true };
                    return ContinueReply(NntpResponses.SaslEmptyChallenge, NntpResponseStatus.SaslEmptyChallenge, completed: false, exchange);
                }

                if (!TryDecodeBase64(initialResponse, out var decoded))
                {
                    return FailReply(NntpResponses.SaslInvalidBase64, NntpResponseStatus.SaslInvalidBase64);
                }

                return await CompletePlainAsync(decoded, clientIp, cancellationToken).ConfigureAwait(false);
            }

            if (EqualsMechanism(mechanism, NntpAuthMechanisms.WireLogin))
            {
                if (!IsEmptyInitialResponse(initialResponse))
                {
                    return FailReply(NntpResponses.SaslProtocolError, NntpResponseStatus.SaslProtocolError);
                }

                var exchange = new NntpSaslExchange(NntpAuthMechanisms.WireLogin);
                return ContinueReply(
                    NntpResponses.SaslLoginUsernameChallenge,
                    NntpResponseStatus.SaslLoginUsernameChallenge,
                    completed: false,
                    exchange);
            }

            if (EqualsMechanism(mechanism, NntpAuthMechanisms.WireCramMd5))
            {
                if (_mysql is null)
                {
                    return FailReply(NntpResponses.SaslCramUnavailable, NntpResponseStatus.SaslCramUnavailable);
                }

                if (!IsEmptyInitialResponse(initialResponse))
                {
                    return FailReply(NntpResponses.SaslProtocolError, NntpResponseStatus.SaslProtocolError);
                }

                var nativeChallenge = CramMd5Mechanism.CreateChallenge(_fqdn);
                var exchange = new NntpSaslExchange(NntpAuthMechanisms.WireCramMd5) { CramChallenge = nativeChallenge };
                var wire = EncodeLine(383, Convert.ToBase64String(Encoding.ASCII.GetBytes(nativeChallenge)));
                return new NntpSaslReply(wire, NntpResponseStatus.SaslChallenge, completed: false, exchange);
            }

            if (EqualsMechanism(mechanism, NntpAuthMechanisms.WireScramSha256))
            {
                if (_mysql is null)
                {
                    return FailReply(NntpResponses.SaslScramUnavailable, NntpResponseStatus.SaslScramUnavailable);
                }

                if (IsEmptyInitialResponse(initialResponse))
                {
                    return FailReply(NntpResponses.SaslScramUnavailable, NntpResponseStatus.SaslScramUnavailable);
                }

                if (!TryDecodeBase64(initialResponse, out var clientFirst))
                {
                    return FailReply(NntpResponses.SaslInvalidBase64, NntpResponseStatus.SaslInvalidBase64);
                }

                return await BeginScramAsync(clientFirst, clientIp, cancellationToken).ConfigureAwait(false);
            }

            return FailReply(NntpResponses.SaslMechanismNotSupported, NntpResponseStatus.SaslMechanismNotSupported);
        }

        /// <summary>Continues an in-progress SASL exchange. Payload <c>*</c> cancels.</summary>
        internal async ValueTask<NntpSaslReply> ContinueAsync(
            NntpSaslExchange exchange,
            string line,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            ArgumentNullException.ThrowIfNull(line);
            ArgumentNullException.ThrowIfNull(clientIp);
            var payload = line.Trim();
            if (payload == "*")
            {
                return FailReply(NntpResponses.AuthenticationCancelled, NntpResponseStatus.AuthenticationCancelled);
            }

            if (!TryDecodeBase64(payload, out var decoded))
            {
                return FailReply(NntpResponses.SaslInvalidBase64, NntpResponseStatus.SaslInvalidBase64);
            }

            if (exchange.WaitingPlainCredentials)
            {
                return await CompletePlainAsync(decoded, clientIp, cancellationToken).ConfigureAwait(false);
            }

            if (EqualsMechanism(exchange.Mechanism, NntpAuthMechanisms.WireLogin))
            {
                return await ContinueLoginAsync(exchange, decoded, clientIp, cancellationToken).ConfigureAwait(false);
            }

            if (EqualsMechanism(exchange.Mechanism, NntpAuthMechanisms.WireCramMd5))
            {
                return await ContinueCramAsync(exchange, decoded, clientIp, cancellationToken).ConfigureAwait(false);
            }

            if (EqualsMechanism(exchange.Mechanism, NntpAuthMechanisms.WireScramSha256))
            {
                return await FinishScramAsync(exchange, decoded, clientIp, cancellationToken).ConfigureAwait(false);
            }

            return FailReply(NntpResponses.SaslContinuationNotSupported, NntpResponseStatus.SaslContinuationNotSupported);
        }

        private async ValueTask<NntpSaslReply> ContinueLoginAsync(
            NntpSaslExchange exchange,
            string decoded,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            if (exchange.LoginUsername is null)
            {
                exchange.LoginUsername = decoded;
                return ContinueReply(
                    NntpResponses.SaslLoginPasswordChallenge,
                    NntpResponseStatus.SaslLoginPasswordChallenge,
                    completed: false,
                    exchange);
            }

            var result = await _passwordProvider
                .AuthenticateAsync(exchange.LoginUsername, decoded, clientIp, cancellationToken)
                .ConfigureAwait(false);
            return FromPasswordResult(result);
        }

        private async ValueTask<NntpSaslReply> CompletePlainAsync(
            string decoded,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            var parts = decoded.Split('\0');
            if (parts.Length < 3 || string.IsNullOrEmpty(parts[1]))
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var result = await _passwordProvider
                .AuthenticateAsync(parts[1], parts[2], clientIp, cancellationToken)
                .ConfigureAwait(false);
            return FromPasswordResult(result);
        }

        private async ValueTask<NntpSaslReply> ContinueCramAsync(
            NntpSaslExchange exchange,
            string response,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            if (_mysql is null || exchange.CramChallenge is null)
            {
                return FailReply(NntpResponses.SaslCramUnavailable, NntpResponseStatus.SaslCramUnavailable);
            }

            var space = response.IndexOf(' ');
            if (space <= 0)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var username = response[..space];
            NntpUserRecord? record;
            try
            {
                record = await _mysql.TryGetCramRecordAsync(username, cancellationToken).ConfigureAwait(false);
            }
            catch (NntpDbUnavailableException)
            {
                return FailReply(NntpResponses.TemporaryAuthenticationFailure, NntpResponseStatus.TemporaryAuthenticationFailure);
            }

            if (record is null)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var secret = CramMd5Mechanism.SecretFromPassword(record.AccountPassword);
            if (!CramMd5Mechanism.Verify(username, response, exchange.CramChallenge, secret))
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var result = await _mysql
                .CompleteSaslAccountAsync(NntpAuthMechanisms.SaslCramMd5, username, clientIp, requireScram: false, cancellationToken)
                .ConfigureAwait(false);
            return FromSaslResult(result, NntpResponses.AuthenticationAccepted, NntpResponseStatus.AuthenticationAccepted);
        }

        private async ValueTask<NntpSaslReply> BeginScramAsync(
            string clientFirst,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            _ = clientIp;
            if (!ScramMechanism.TryGetUsername(clientFirst, out var username) || _mysql is null)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            NntpUserRecord? record;
            try
            {
                record = await _mysql.TryLoadUserAsync(username, cancellationToken).ConfigureAwait(false);
            }
            catch (NntpDbUnavailableException)
            {
                return FailReply(NntpResponses.TemporaryAuthenticationFailure, NntpResponseStatus.TemporaryAuthenticationFailure);
            }

            var useReal = ScramStoredCredential.TryGetAuthorized(record, out var credential);
            if (!useReal)
            {
                credential = _dummyScram.Credential;
            }

            try
            {
                var (state, serverFirst) = ScramMechanism.Begin(clientFirst, credential);
                var exchange = new NntpSaslExchange(NntpAuthMechanisms.WireScramSha256)
                {
                    Scram = state,
                    ScramUsername = username,
                    ScramUsedDummy = !useReal,
                };
                var wire = EncodeLine(383, Convert.ToBase64String(Encoding.UTF8.GetBytes(serverFirst)));
                return new NntpSaslReply(wire, NntpResponseStatus.SaslChallenge, completed: false, exchange);
            }
            catch (ArgumentException)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }
        }

        private async ValueTask<NntpSaslReply> FinishScramAsync(
            NntpSaslExchange exchange,
            string clientFinal,
            IPAddress clientIp,
            CancellationToken cancellationToken)
        {
            if (exchange.Scram is null || exchange.ScramUsername is null || _mysql is null)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var serverFinal = exchange.Scram.TryFinish(clientFinal);
            if (serverFinal is null || exchange.ScramUsedDummy)
            {
                return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
            }

            var result = await _mysql
                .CompleteSaslAccountAsync(
                    NntpAuthMechanisms.SaslScramSha256,
                    exchange.ScramUsername,
                    clientIp,
                    requireScram: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return FromPasswordResult(result);
            }

            var wire = EncodeLine(283, Convert.ToBase64String(Encoding.UTF8.GetBytes(serverFinal)));
            return new NntpSaslReply(wire, NntpResponseStatus.SaslSuccessWithData, completed: true, authentication: result);
        }

        private static NntpSaslReply FromPasswordResult(NntpAuthenticationResult result)
        {
            if (result.Succeeded)
            {
                return new NntpSaslReply(
                    NntpResponses.AuthenticationAccepted,
                    NntpResponseStatus.AuthenticationAccepted,
                    completed: true,
                    authentication: result);
            }

            if (result.Failure == NntpAuthenticationFailureKind.TransientFailure)
            {
                return FailReply(NntpResponses.TemporaryAuthenticationFailure, NntpResponseStatus.TemporaryAuthenticationFailure);
            }

            return FailReply(NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed);
        }

        private static NntpSaslReply FromSaslResult(
            NntpAuthenticationResult result,
            ReadOnlyMemory<byte> successWire,
            string successStatus)
        {
            if (result.Succeeded)
            {
                return new NntpSaslReply(successWire, successStatus, completed: true, authentication: result);
            }

            return FromPasswordResult(result);
        }

        private static NntpSaslReply ContinueReply(
            ReadOnlyMemory<byte> wire,
            string status,
            bool completed,
            NntpSaslExchange exchange) =>
            new(wire, status, completed, exchange);

        private static NntpSaslReply FailReply(ReadOnlyMemory<byte> wire, string status) =>
            new(wire, status, completed: true);

        internal static bool EqualsMechanism(string actual, string expected) =>
            actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

        internal static bool IsEmptyInitialResponse(string token)
        {
            ArgumentNullException.ThrowIfNull(token);
            var text = token.Trim();
            return text.Length == 0 || text == "=";
        }

        /// <summary>
        /// Decodes a SASL token. RFC 4643 <c>=</c> is an empty payload. Invalid BASE64 is rejected
        /// (504) rather than treated as plaintext.
        /// </summary>
        internal static bool TryDecodeBase64(string token, out string decoded)
        {
            ArgumentNullException.ThrowIfNull(token);
            var text = token.Trim();
            if (text.Length == 0 || text == "=")
            {
                decoded = string.Empty;
                return true;
            }

            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                return true;
            }
            catch (FormatException)
            {
                decoded = string.Empty;
                return false;
            }
        }

        private static string ResolveFqdn(NntpdOptions? options)
        {
            if (options is not null && !string.IsNullOrWhiteSpace(options.Fqdn))
            {
                return options.Fqdn;
            }

            return NntpdOptions.FormatFqdn(1, "usenet.ninja");
        }

        private static ReadOnlyMemory<byte> EncodeLine(int code, string text)
        {
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{code} {text}\r\n");
            return Encoding.ASCII.GetBytes(line);
        }
    }
}
