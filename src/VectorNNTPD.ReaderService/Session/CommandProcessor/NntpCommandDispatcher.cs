using System.Diagnostics;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Rejects invalid syntax, applies session/authorization gates, then switch-dispatches the handler.
    /// </summary>
    /// <remarks>
    /// Validation order: parse (caller) → syntax reject → authentication → authorization → mode → handler.
    /// Command RX/TX completion records are owned by the session (RX) and command modules (TX), not this type.
    /// </remarks>
    public sealed class NntpCommandDispatcher
    {
        private readonly ILogger _unknownCommandLogger;

        /// <summary>Initializes a new instance of the <see cref="NntpCommandDispatcher"/> class.</summary>
        public NntpCommandDispatcher(ILoggerFactory? loggerFactory = null)
        {
            NntpCommandLoggers.Configure(loggerFactory);
            _unknownCommandLogger = NntpCommandLoggers.For(typeof(NntpCommandExecution));
        }

        /// <summary>Dispatches one already-parsed command. Invalid syntax never reaches a handler.</summary>
        public async ValueTask DispatchAsync(
            NntpSession session,
            NntpCommand command,
            ReadOnlyMemory<byte> line,
            NntpResponseWriter response,
            CancellationToken cancellationToken,
            NntpMultilineReadResult? preReadArticle = null)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(response);

            if (!command.IsValid)
            {
                await RejectInvalidAsync(session, command, response, preReadArticle, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var access = DefaultNntpCommandCatalog.GetAccess(command.Verb, command.Qualifier);
            var display = DefaultNntpCommandCatalog.DisplayName(command.Verb, command.Qualifier);
            var logger = LoggerFor(command);
            var authz = session.Authorization;
            var gateStarted = Stopwatch.GetTimestamp();

            // Role isolation: CAPABILITIES is authoritative; opposite-role commands are unavailable.
            if (session.ServerRole == NntpServerRole.Reader
                && (access.HasFlag(NntpCommandAccess.RequiresTransit)
                    || access.HasFlag(NntpCommandAccess.RequiresStreaming)))
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.CommandNotAvailable,
                        NntpResponseStatus.CommandNotAvailable,
                        "command not available on this server",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (session.ServerRole == NntpServerRole.Transit
                && access.HasFlag(NntpCommandAccess.RequiresReader))
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.CommandNotAvailable,
                        NntpResponseStatus.CommandNotAvailable,
                        "command not available on this server",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresAuthentication) && !authz.IsAuthenticated)
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.AuthenticationRequired,
                        NntpResponseStatus.AuthenticationRequired,
                        "authentication required",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresReader) && !authz.AuthorizedReader)
            {
                var wire = authz.IsAuthenticated
                    ? NntpResponses.PermissionDenied
                    : NntpResponses.AuthenticationRequired;
                var status = authz.IsAuthenticated
                    ? NntpResponseStatus.PermissionDenied
                    : NntpResponseStatus.AuthenticationRequired;
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        wire,
                        status,
                        authz.IsAuthenticated ? "permission denied" : "authentication required",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresTransit) && !authz.AuthorizedTransit)
            {
                var wire = authz.IsAuthenticated
                    ? NntpResponses.PermissionDenied
                    : NntpResponses.AuthenticationRequired;
                var status = authz.IsAuthenticated
                    ? NntpResponseStatus.PermissionDenied
                    : NntpResponseStatus.AuthenticationRequired;
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        wire,
                        status,
                        authz.IsAuthenticated ? "permission denied" : "authentication required",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresPosting) && !authz.PostingPermitted)
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.PostingNotPermitted,
                        NntpResponseStatus.PostingNotPermitted,
                        "posting not permitted",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresStreaming) && !authz.StreamingPermitted)
            {
                var wire = authz.IsAuthenticated
                    ? NntpResponses.StreamingNotPermitted
                    : NntpResponses.AuthenticationRequired;
                var status = authz.IsAuthenticated
                    ? NntpResponseStatus.StreamingNotPermitted
                    : NntpResponseStatus.AuthenticationRequired;
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        wire,
                        status,
                        authz.IsAuthenticated ? "streaming not permitted" : "authentication required",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresReaderMode) && session.Mode != NntpSessionMode.Reader)
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.NotInReaderMode,
                        NntpResponseStatus.NotInReaderMode,
                        "not in reader mode",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (access.HasFlag(NntpCommandAccess.RequiresStreamMode) && session.Mode != NntpSessionMode.Stream)
            {
                await WriteGatedResponseAsync(
                        response,
                        logger,
                        session,
                        display,
                        NntpResponses.NotInStreamMode,
                        NntpResponseStatus.NotInStreamMode,
                        "not in stream mode",
                        gateStarted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var context = new NntpCommandContext(session, command, line, response, preReadArticle);
            try
            {
                await InvokeHandlerAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                if (session.Connection.IsCompleted || session.Connection.ConnectionClosed.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await response
                        .WriteLineAsync(NntpResponses.CommandFailed, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Output writer completed between the check and the write.
                }
            }
        }

        private async ValueTask RejectInvalidAsync(
            NntpSession session,
            NntpCommand command,
            NntpResponseWriter response,
            NntpMultilineReadResult? preReadArticle,
            CancellationToken cancellationToken)
        {
            if (command.Verb == NntpVerb.TakeThis && preReadArticle is null)
            {
                try
                {
                    NntpArticlePolicyCapture.Capture(
                        session,
                        session.ArticleIngestion.MaxArticleBytes,
                        out var maxArticleBytes,
                        out _);
                    _ = await NntpMultilineDataReader
                        .ReadArticleAsync(session.Connection.Input, maxArticleBytes, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested
                    || session.Connection.ConnectionClosed.IsCancellationRequested)
                {
                    return;
                }
            }

            var started = Stopwatch.GetTimestamp();
            var display = command.Status is NntpParseStatus.Empty
                or NntpParseStatus.UnknownVerb
                or NntpParseStatus.CommandLineTooLong
                ? "INVALID"
                : DefaultNntpCommandCatalog.DisplayName(command.Verb, command.Qualifier);
            var (wire, detail, statusLine) = Rejection(command);
            await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
            session.LogCommandRejected(command, detail);
            if (_unknownCommandLogger.IsEnabled(LogLevel.Debug))
            {
                NntpCommandExecution.WriteCompletion(
                    _unknownCommandLogger,
                    session,
                    display,
                    Stopwatch.GetElapsedTime(started),
                    detail,
                    statusLine);
            }
        }

        private static async ValueTask WriteGatedResponseAsync(
            NntpResponseWriter response,
            ILogger logger,
            NntpSession session,
            string display,
            ReadOnlyMemory<byte> wire,
            string statusLine,
            string detail,
            long startedTimestamp,
            CancellationToken cancellationToken)
        {
            await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                NntpCommandExecution.WriteCompletion(
                    logger,
                    session,
                    display,
                    Stopwatch.GetElapsedTime(startedTimestamp),
                    detail,
                    statusLine);
            }
        }

        private static (ReadOnlyMemory<byte> Wire, string Detail, string StatusLine) Rejection(NntpCommand command)
        {
            return command.Status switch
            {
                NntpParseStatus.CommandLineTooLong =>
                    (NntpResponses.SyntaxError, "syntax error", NntpResponseStatus.SyntaxError),
                NntpParseStatus.UnknownVerb =>
                    (NntpResponses.UnknownCommand, "unknown command", NntpResponseStatus.UnknownCommand),
                NntpParseStatus.UnknownQualifier =>
                    (NntpResponses.UnknownCommandVariant, "unknown variant", NntpResponseStatus.UnknownCommandVariant),
                NntpParseStatus.MissingArgument when command.Verb == NntpVerb.AuthInfo
                    && command.Qualifier == NntpVerb.User =>
                    (NntpResponses.AuthinfoUserRequiresUsername, "syntax error", NntpResponseStatus.AuthinfoUserRequiresUsername),
                NntpParseStatus.MissingArgument when command.Verb == NntpVerb.AuthInfo
                    && command.Qualifier == NntpVerb.Pass =>
                    (NntpResponses.AuthinfoPassRequiresPassword, "syntax error", NntpResponseStatus.AuthinfoPassRequiresPassword),
                NntpParseStatus.MissingArgument when command.Verb == NntpVerb.AuthInfo
                    && command.Qualifier == NntpVerb.Sasl =>
                    (NntpResponses.SaslMechanismRequired, "syntax error", NntpResponseStatus.SaslMechanismRequired),
                NntpParseStatus.MissingArgument when command.Verb == NntpVerb.Compress =>
                    (NntpResponses.CompressRequiresAlgorithm, "syntax error", NntpResponseStatus.CompressRequiresAlgorithm),
                NntpParseStatus.InvalidArgument when command.Verb == NntpVerb.Compress =>
                    (NntpResponses.CompressAlgorithmSyntaxInvalid, "syntax error", NntpResponseStatus.CompressAlgorithmSyntaxInvalid),
                NntpParseStatus.ExtraArgument when command.Verb == NntpVerb.Compress =>
                    (NntpResponses.CompressRequiresAlgorithm, "syntax error", NntpResponseStatus.CompressRequiresAlgorithm),
                _ => (NntpResponses.SyntaxError, "syntax error", NntpResponseStatus.SyntaxError),
            };
        }

        private static ILogger LoggerFor(NntpCommand command)
        {
            var type = command.Verb switch
            {
                NntpVerb.Check => typeof(Check),
                NntpVerb.TakeThis => typeof(TakeThis),
                NntpVerb.Mode => typeof(Mode),
                NntpVerb.AuthInfo => typeof(AuthInfo),
                NntpVerb.Quit => typeof(Quit),
                NntpVerb.Help => typeof(Help),
                NntpVerb.Date => typeof(Date),
                NntpVerb.Capabilities => typeof(Capabilities),
                NntpVerb.StartTls => typeof(StartTls),
                NntpVerb.Compress => typeof(Compress),
                NntpVerb.List => typeof(List),
                NntpVerb.ListGroup => typeof(ListGroup),
                NntpVerb.Group => typeof(Group),
                NntpVerb.Article => typeof(Article),
                NntpVerb.Head => typeof(Article),
                NntpVerb.Body => typeof(Article),
                NntpVerb.Stat => typeof(Article),
                NntpVerb.Last => typeof(Last),
                NntpVerb.Next => typeof(Next),
                NntpVerb.Over => typeof(Over),
                NntpVerb.Hdr => typeof(Hdr),
                NntpVerb.Post => typeof(Post),
                NntpVerb.Ihave => typeof(IHave),
                NntpVerb.BenchIt => typeof(BenchIt),
                NntpVerb.SpeedTest => typeof(Commands.SpeedTest),
                _ => typeof(NntpCommandExecution),
            };

            return NntpCommandLoggers.For(type);
        }

        private static ValueTask InvokeHandlerAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            return (context.Command.Verb, context.Command.Qualifier) switch
            {
                (NntpVerb.Capabilities, _) => Capabilities.HandleAsync(context, cancellationToken),
                (NntpVerb.Mode, NntpVerb.Reader) => Mode.HandleReaderAsync(context, cancellationToken),
                (NntpVerb.Mode, NntpVerb.Stream) => Mode.HandleStreamAsync(context, cancellationToken),
                (NntpVerb.Help, _) => Help.HandleAsync(context, cancellationToken),
                (NntpVerb.Date, _) => Date.HandleAsync(context, cancellationToken),
                (NntpVerb.Quit, _) => Quit.HandleAsync(context, cancellationToken),
                (NntpVerb.StartTls, _) => StartTls.HandleAsync(context, context.Session.CertificateProvider, cancellationToken),
                (NntpVerb.Compress, _) => Compress.HandleAsync(context, cancellationToken),
                (NntpVerb.BenchIt, _) => BenchIt.HandleAsync(context, cancellationToken),
                (NntpVerb.SpeedTest, _) => Commands.SpeedTest.HandleAsync(context, cancellationToken),
                (NntpVerb.AuthInfo, NntpVerb.User) => AuthInfo.HandleUserAsync(
                    context, context.Session.AuthenticationProvider, cancellationToken),
                (NntpVerb.AuthInfo, NntpVerb.Pass) => AuthInfo.HandlePassAsync(
                    context, context.Session.AuthenticationProvider, cancellationToken),
                (NntpVerb.AuthInfo, NntpVerb.Sasl) => AuthInfo.HandleSaslAsync(context, cancellationToken),
                (NntpVerb.List, _) => List.HandleAsync(context, cancellationToken),
                (NntpVerb.Group, _) => Group.HandleAsync(context, cancellationToken),
                (NntpVerb.ListGroup, _) => ListGroup.HandleAsync(context, cancellationToken),
                (NntpVerb.Article, _) => Article.HandleArticleAsync(context, cancellationToken),
                (NntpVerb.Head, _) => Article.HandleHeadAsync(context, cancellationToken),
                (NntpVerb.Body, _) => Article.HandleBodyAsync(context, cancellationToken),
                (NntpVerb.Stat, _) => Article.HandleStatAsync(context, cancellationToken),
                (NntpVerb.Last, _) => Last.HandleAsync(context, cancellationToken),
                (NntpVerb.Next, _) => Next.HandleAsync(context, cancellationToken),
                (NntpVerb.Over, _) => Over.HandleAsync(context, cancellationToken),
                (NntpVerb.Hdr, _) => Hdr.HandleAsync(context, cancellationToken),
                (NntpVerb.Post, _) => Post.HandleAsync(context, cancellationToken),
                (NntpVerb.Ihave, _) => IHave.HandleAsync(context, cancellationToken),
                (NntpVerb.Check, _) => Check.HandleAsync(context, cancellationToken),
                (NntpVerb.TakeThis, _) => TakeThis.HandleAsync(context, cancellationToken),
                _ => NntpCommandNotImplemented.HandleAsync(
                    context,
                    NntpCommandLoggers.For(typeof(NntpCommandExecution)),
                    cancellationToken),
            };
        }
    }
}
