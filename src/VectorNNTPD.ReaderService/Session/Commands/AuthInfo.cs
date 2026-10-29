using System.Text;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// AUTHINFO USER / PASS (RFC 4643, Section 2.3) and AUTHINFO SASL (RFC 4643, Section 2.4).
    /// </summary>
    /// <remarks>
    /// USER caches a username. PASS selects the credential authority from
    /// <see cref="NntpSession.AuthenticationAuthority"/> (MODE), never from source IP.
    /// Transit authority uses <see cref="ITransitPeerAuthenticator"/> only.
    /// Reader authority uses <see cref="INntpAuthenticationProvider"/> only (newsmaster then MySQL).
    /// There is no Transit ↔ Reader fall-through. SASL is reader-authority only.
    /// </remarks>
    internal static class AuthInfo
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(AuthInfo));

        /// <summary>Handles <c>AUTHINFO USER</c>.</summary>
        public static ValueTask HandleUserAsync(
            NntpCommandContext context,
            INntpAuthenticationProvider authenticationProvider,
            CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "AUTHINFO USER",
                (ctx, ct) => ExecuteUserAsync(ctx, authenticationProvider, ct),
                cancellationToken);

        /// <summary>Handles <c>AUTHINFO PASS</c>.</summary>
        public static ValueTask HandlePassAsync(
            NntpCommandContext context,
            INntpAuthenticationProvider authenticationProvider,
            CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "AUTHINFO PASS",
                (ctx, ct) => ExecutePassAsync(ctx, authenticationProvider, ct),
                cancellationToken);

        /// <summary>Handles <c>AUTHINFO SASL</c>.</summary>
        public static ValueTask HandleSaslAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "AUTHINFO SASL",
                ExecuteSaslAsync,
                cancellationToken);

        private static async ValueTask ExecuteUserAsync(
            NntpCommandContext context,
            INntpAuthenticationProvider authenticationProvider,
            CancellationToken cancellationToken)
        {
            _ = authenticationProvider;
            if (!TryBeginAuthCommand(context, cancellationToken, out var blocked))
            {
                await blocked.ConfigureAwait(false);
                return;
            }

            context.Session.AbandonSaslExchange();
            var username = Encoding.ASCII.GetString(context.ArgumentSpan);
            context.Session.SetPendingAuthUsername(username);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PasswordRequired,
                    NntpResponseStatus.PasswordRequired,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async ValueTask ExecutePassAsync(
            NntpCommandContext context,
            INntpAuthenticationProvider authenticationProvider,
            CancellationToken cancellationToken)
        {
            if (!TryBeginAuthCommand(context, cancellationToken, out var blocked))
            {
                await blocked.ConfigureAwait(false);
                return;
            }

            var pending = context.Session.PendingAuthUsername;
            if (pending is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.AuthenticationOutOfSequence,
                        NntpResponseStatus.AuthenticationOutOfSequence,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var password = Encoding.ASCII.GetString(context.ArgumentSpan);
            NntpAuthenticationResult result;
            try
            {
                result = await AuthenticatePassAsync(
                        context.Session,
                        authenticationProvider,
                        pending,
                        password,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // codeql[cs/useless-assignment-to-local] Clear password reference after use; assignment is intentional.
                password = null!;
            }

            await CompleteAuthenticationAsync(context, result, cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask ExecuteSaslAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken)
        {
            if (!TryBeginAuthCommand(context, cancellationToken, out var blocked))
            {
                await blocked.ConfigureAwait(false);
                return;
            }

            if (context.Session.AuthenticationAuthority == NntpAuthenticationAuthority.Transit)
            {
                context.Session.ApplyFailedAuthentication();
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.AuthenticationFailed,
                        NntpResponseStatus.AuthenticationFailed,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var sasl = context.Session.SaslService;
            if (sasl is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.AuthinfoSaslNotImplemented,
                        NntpResponseStatus.AuthinfoSaslNotImplemented,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var argument = context.ArgumentSpan;
            if (argument.IsEmpty)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.SaslMechanismRequired,
                        NntpResponseStatus.SaslMechanismRequired,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            SplitMechanism(argument, out var mechanism, out var initial);
            context.Session.AbandonSaslExchange();
            var reply = await sasl
                .StartAsync(mechanism, Encoding.ASCII.GetString(initial), context.Session.ClientAddress, cancellationToken)
                .ConfigureAwait(false);
            await ApplySaslReplyAsync(context, reply, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Handles a SASL continuation line (base64, <c>=</c>, or <c>*</c>).</summary>
        internal static async ValueTask ContinueSaslAsync(
            NntpSession session,
            NntpResponseWriter response,
            ReadOnlyMemory<byte> line,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(response);
            var sasl = session.SaslService;
            var exchange = session.CurrentSaslExchange;
            if (sasl is null || exchange is null)
            {
                session.AbandonSaslExchange();
                await response.WriteLineAsync(NntpResponses.NoSaslExchangeInProgress, cancellationToken).ConfigureAwait(false);
                return;
            }

            var reply = await sasl
                .ContinueAsync(exchange, Encoding.ASCII.GetString(line.Span), session.ClientAddress, cancellationToken)
                .ConfigureAwait(false);
            await ApplySaslReplyCoreAsync(session, response, context: null, reply, cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask<NntpAuthenticationResult> AuthenticatePassAsync(
            NntpSession session,
            INntpAuthenticationProvider authenticationProvider,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            if (session.AuthenticationAuthority == NntpAuthenticationAuthority.Transit)
            {
                return ValueTask.FromResult(
                    session.TransitAuthenticator.Authenticate(
                        session.Authorization.TransitPeerPolicy,
                        username,
                        password,
                        session.Authorization));
            }

            return authenticationProvider.AuthenticateAsync(
                username,
                password,
                session.ClientAddress,
                cancellationToken);
        }

        private static bool TryBeginAuthCommand(
            NntpCommandContext context,
            CancellationToken cancellationToken,
            out ValueTask blocked)
        {
            if (context.Session.Authentication.IsAuthenticated)
            {
                blocked = NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AlreadyAuthenticated,
                    NntpResponseStatus.AlreadyAuthenticated,
                    cancellationToken);
                return false;
            }

            if (context.Connection.IsCompressed)
            {
                blocked = NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.DeflateAlreadyActive,
                    NntpResponseStatus.DeflateAlreadyActive,
                    cancellationToken);
                return false;
            }

            if (!context.Session.IsAuthinfoPassPermitted)
            {
                blocked = NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PrivacyRequired,
                    NntpResponseStatus.PrivacyRequired,
                    cancellationToken);
                return false;
            }

            blocked = default;
            return true;
        }

        private static ValueTask ApplySaslReplyAsync(
            NntpCommandContext context,
            NntpSaslReply reply,
            CancellationToken cancellationToken) =>
            ApplySaslReplyCoreAsync(context.Session, context.Response, context, reply, cancellationToken);

        private static async ValueTask ApplySaslReplyCoreAsync(
            NntpSession session,
            NntpResponseWriter response,
            NntpCommandContext? context,
            NntpSaslReply reply,
            CancellationToken cancellationToken)
        {
            if (!reply.Completed && reply.Exchange is not null)
            {
                session.SetSaslExchange(reply.Exchange);
                await WriteAsync(context, response, reply.Wire, reply.StatusLine, cancellationToken).ConfigureAwait(false);
                return;
            }

            session.AbandonSaslExchange();
            if (reply.Authentication is { Succeeded: true } result)
            {
                await CompleteAuthenticationCoreAsync(
                        session,
                        response,
                        context,
                        result,
                        reply.Wire,
                        reply.StatusLine,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (reply.Authentication is { } failed)
            {
                await CompleteAuthenticationCoreAsync(
                        session,
                        response,
                        context,
                        failed,
                        successWire: default,
                        successStatus: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            session.ApplyFailedAuthentication();
            await WriteAsync(context, response, reply.Wire, reply.StatusLine, cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask CompleteAuthenticationAsync(
            NntpCommandContext context,
            NntpAuthenticationResult result,
            CancellationToken cancellationToken) =>
            CompleteAuthenticationCoreAsync(
                context.Session,
                context.Response,
                context,
                result,
                NntpResponses.AuthenticationAccepted,
                NntpResponseStatus.AuthenticationAccepted,
                cancellationToken);

        private static async ValueTask CompleteAuthenticationCoreAsync(
            NntpSession session,
            NntpResponseWriter response,
            NntpCommandContext? context,
            NntpAuthenticationResult result,
            ReadOnlyMemory<byte> successWire,
            string? successStatus,
            CancellationToken cancellationToken)
        {
            if (!result.Succeeded || result.Username is null || result.Authorization is null)
            {
                session.ApplyFailedAuthentication();
                var (wire, status) = MapFailure(result.Failure);
                await WriteAsync(context, response, wire, status, cancellationToken).ConfigureAwait(false);
                return;
            }

            var admitted = await session.AdmitAuthenticatedSessionAsync(result.Policy, cancellationToken)
                .ConfigureAwait(false);
            if (!admitted.Succeeded)
            {
                session.ApplyFailedAuthentication();
                var (wire, status) = MapFailure(admitted.Failure);
                if (admitted.Failure is NntpAuthenticationFailureKind.TooManySessions
                    or NntpAuthenticationFailureKind.TooManySourceAddresses
                    or NntpAuthenticationFailureKind.TransientFailure)
                {
                    AuthenticationLogMessages.AdmissionRejected(
                        Logger,
                        result.Username,
                        admitted.Failure.ToString(),
                        session.ClientAddress.ToString());
                }

                await WriteAsync(context, response, wire, status, cancellationToken).ConfigureAwait(false);
                return;
            }

            session.ApplySuccessfulAuthentication(result.Username, result.Authorization, result.Policy);
            await session.ReleaseTransitAdmissionWhenNotTransitAsync().ConfigureAwait(false);
            await session.AttachByteAccountingAsync(response, cancellationToken).ConfigureAwait(false);
            var wireSuccess = successWire.IsEmpty ? NntpResponses.AuthenticationAccepted : successWire;
            var statusSuccess = successStatus ?? NntpResponseStatus.AuthenticationAccepted;
            await WriteAsync(context, response, wireSuccess, statusSuccess, cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask WriteAsync(
            NntpCommandContext? context,
            NntpResponseWriter response,
            ReadOnlyMemory<byte> wire,
            string status,
            CancellationToken cancellationToken)
        {
            if (context is not null)
            {
                return NntpCommandReply.WriteAsync(context, Logger, wire, status, cancellationToken);
            }

            return response.WriteLineAsync(wire, cancellationToken);
        }

        private static (ReadOnlyMemory<byte> Wire, string Status) MapFailure(NntpAuthenticationFailureKind failure) =>
            failure switch
            {
                NntpAuthenticationFailureKind.TransientFailure =>
                    (NntpResponses.TemporaryAuthenticationFailure, NntpResponseStatus.TemporaryAuthenticationFailure),
                NntpAuthenticationFailureKind.TooManySessions =>
                    (NntpResponses.TooManySessions, NntpResponseStatus.TooManySessions),
                NntpAuthenticationFailureKind.TooManySourceAddresses =>
                    (NntpResponses.TooManySourceAddresses, NntpResponseStatus.TooManySourceAddresses),
                _ => (NntpResponses.AuthenticationFailed, NntpResponseStatus.AuthenticationFailed),
            };

        private static void SplitMechanism(ReadOnlySpan<byte> argument, out string mechanism, out ReadOnlySpan<byte> initial)
        {
            var end = NntpAscii.SkipToken(argument, 0);
            mechanism = Encoding.ASCII.GetString(argument[..end]);
            var next = NntpAscii.SkipWhitespace(argument, end);
            initial = next >= argument.Length ? ReadOnlySpan<byte>.Empty : argument[next..];
        }
    }
}
