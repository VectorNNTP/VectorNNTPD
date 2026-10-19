using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using VectorNNTPD.BackFillerService.Nntp;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Queues <see cref="ScriptedNntpServer"/> instances and can fail, delay, or block <c>ConnectAsync</c>.
    /// Records attempts and the highest concurrent connect occupancy. Does not open real TCP connections.
    /// </summary>
    internal sealed class ScriptedNntpTransportFactory : INntpTransportFactory
    {
        /// <summary>Scripted servers handed out in FIFO order on connect.</summary>
        private readonly ConcurrentQueue<ScriptedNntpServer> _servers = new();

        /// <summary>Serializes appends to <see cref="ConnectAttempts"/> from overlapping connect calls.</summary>
        private readonly object _attemptsGate = new();

        /// <summary>Connect calls currently inside <see cref="ConnectAsync"/>.</summary>
        private int _currentConnects;

        /// <summary>Peak concurrent connect occupancy for pool limit assertions.</summary>
        private int _maxObservedConnects;

        /// <summary>Provider definitions passed to each <see cref="ConnectAsync"/> in invocation order.</summary>
        public List<BackFillerProviderDefinition> ConnectAttempts { get; } = [];

        /// <summary>When set, thrown after delay and blocking; no stream is returned.</summary>
        public Exception? ConnectException { get; set; }

        /// <summary>When set, awaited inside connect before failure or dequeuing a scripted server.</summary>
        public TimeSpan? ConnectDelay { get; set; }

        /// <summary>Completed when a connect enters after recording the attempt and updating concurrency counters.</summary>
        public TaskCompletionSource? ConnectStarted { get; set; }

        /// <summary>When set, each connect waits on this task until the test releases it.</summary>
        public TaskCompletionSource? BlockConnect { get; set; }

        /// <summary>Gets the highest concurrent <see cref="ConnectAsync"/> occupancy observed.</summary>
        internal int MaxObservedConcurrentConnects => Volatile.Read(ref _maxObservedConnects);

        /// <summary>Gets how many <see cref="ConnectAsync"/> calls are inside the method right now.</summary>
        internal int CurrentConcurrentConnects => Volatile.Read(ref _currentConnects);

        /// <summary>Queues a scripted server handed out FIFO on the next successful connect.</summary>
        /// <param name="server">In-memory peer whose client stream is returned from connect.</param>
        internal void Enqueue(ScriptedNntpServer server)
        {
            ArgumentNullException.ThrowIfNull(server);
            _servers.Enqueue(server);
        }

        /// <summary>
        /// Records the provider, tracks concurrent occupancy, optionally delays or blocks, then dequeues a scripted server.
        /// Throws when no server was enqueued or when <see cref="ConnectException"/> is configured; never opens TCP.
        /// </summary>
        /// <param name="provider">Recorded in <see cref="ConnectAttempts"/> for assertions.</param>
        /// <param name="options">Accepted for interface compatibility; not applied to the fake transport.</param>
        /// <param name="cancellationToken">Honored during delay, blocking, and before dequeuing.</param>
        /// <returns>A duplex stream wired to the dequeued <see cref="ScriptedNntpServer"/>.</returns>
        public async Task<Stream> ConnectAsync(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);
            lock (_attemptsGate)
            {
                ConnectAttempts.Add(provider);
            }
            var current = Interlocked.Increment(ref _currentConnects);
            try
            {
                while (true)
                {
                    var observed = Volatile.Read(ref _maxObservedConnects);
                    if (current <= observed
                        || Interlocked.CompareExchange(ref _maxObservedConnects, current, observed) == observed)
                    {
                        break;
                    }
                }

                ConnectStarted?.TrySetResult();
                if (BlockConnect is not null)
                {
                    await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ConnectDelay is { } delay)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (ConnectException is not null)
                {
                    throw ConnectException;
                }

                if (!_servers.TryDequeue(out var server))
                {
                    throw new InvalidOperationException("No scripted NNTP server was queued.");
                }

                return await server.AcceptClientAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConnects);
            }
        }
    }

    /// <summary>In-memory NNTP server script over duplex pipes; tests enqueue response lines and cap reads.</summary>
    internal sealed class ScriptedNntpServer
    {
        /// <summary>Server-to-client bytes (responses and greeting).</summary>
        private readonly Pipe _toClient;
        /// <summary>Client-to-server bytes (commands).</summary>
        private readonly Pipe _toServer;
        /// <summary>Raw command lines read from the client.</summary>
        private readonly List<byte[]> _commands = [];
        /// <summary>Maps uppercased command prefix to response bytes.</summary>
        private Func<string, byte[]> _onCommand;

        /// <summary>Builds a server with the given greeting and default 430 handler.</summary>
        /// <param name="greeting">Initial status line written on accept.</param>
        /// <remarks>
        /// Both pipes use <see cref="PipeScheduler.Inline"/> and a large pause threshold.
        /// Command handling then runs on the client write that flushed the command, so a busy thread pool cannot drop the handshake.
        /// </remarks>
        internal ScriptedNntpServer(string greeting = "200 posting allowed")
        {
            // Inline scheduling runs the peer on the caller's stack. A saturated thread pool
            // then cannot miss the greeting or CAPABILITIES exchange inside the command timeout.
            var options = new PipeOptions(
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.Inline,
                pauseWriterThreshold: 64 * 1024 * 1024,
                resumeWriterThreshold: 32 * 1024 * 1024,
                useSynchronizationContext: false);
            _toClient = new Pipe(options);
            _toServer = new Pipe(options);
            _onCommand = _ => "430 no such article\r\n"u8.ToArray();
            Greeting = greeting;
        }

        /// <summary>Initial status line written on accept unless <see cref="CompleteWithoutGreeting"/> is true.</summary>
        public string Greeting { get; }

        /// <summary>ASCII command lines read from the client after accept, in order.</summary>
        internal IReadOnlyList<string> Commands =>
            _commands.Select(static bytes => Encoding.ASCII.GetString(bytes)).ToArray();

        /// <summary>Reserved for TLS negotiation tests; the scripted server does not perform a real handshake.</summary>
        public bool UseTlsRequested { get; set; }

        /// <summary>When true, accept completes the outbound pipe immediately without writing a greeting.</summary>
        public bool CompleteWithoutGreeting { get; set; }

        /// <summary>When true, the command loop exits after the first response flush.</summary>
        public bool CompleteAfterResponse { get; set; }

        /// <summary>Whether the most recently read command line ended with CRLF before the line feed.</summary>
        public bool LastCommandUsedCrlf { get; private set; }

        /// <summary>Set when an ARTICLE command is read, before any optional block.</summary>
        public TaskCompletionSource? ArticleStarted { get; set; }

        /// <summary>When set, ARTICLE waits here before the scripted response is written.</summary>
        public TaskCompletionSource? BlockArticle { get; set; }

        /// <summary>
        /// When set, ARTICLE writes only the 220 status line, signals <see cref="ArticleStatusWritten"/>, then waits here before the body.
        /// AUTHINFO and other commands still use the installed responder.
        /// </summary>
        public TaskCompletionSource? HoldArticleBody { get; set; }

        /// <summary>Set after the 220 status line for a held ARTICLE body has been flushed to the client.</summary>
        public TaskCompletionSource? ArticleStatusWritten { get; set; }

        /// <summary>
        /// When set, the next client read that copies a newline waits before <see cref="DuplexPipeStream.ReadAsync"/> returns those bytes.
        /// Connect reads must finish before this is armed, or the greeting is held too.
        /// </summary>
        public TaskCompletionSource? HoldStatusLineReturn { get; set; }

        /// <summary>Set when a status line has been copied into the client buffer and <see cref="HoldStatusLineReturn"/> is waiting.</summary>
        public TaskCompletionSource? StatusLineCopied { get; set; }

        /// <summary>
        /// When set, the next non-QUIT client write waits after the pipe flush and before <see cref="DuplexPipeStream.WriteAsync"/> returns.
        /// QUIT is not held, so session disposal can finish while a command write is paused.
        /// </summary>
        public TaskCompletionSource? HoldAfterCommandWrite { get; set; }

        /// <summary>Set when a non-QUIT command has been flushed and <see cref="HoldAfterCommandWrite"/> is waiting.</summary>
        public TaskCompletionSource? CommandWriteFlushed { get; set; }

        /// <summary>
        /// When true, <see cref="HoldAfterCommandWrite"/> and <see cref="HoldStatusLineReturn"/> wait on the stream token.
        /// Leave false when a test must resume the command after disposal has already cancelled that token.
        /// </summary>
        public bool ObserveCommandHoldCancellation { get; set; }

        /// <summary>
        /// When set, the first client read waits here before touching the pipe.
        /// Used to dispose the session after transport publication and before the greeting read returns.
        /// </summary>
        public TaskCompletionSource? HoldInitialRead { get; set; }

        /// <summary>Set when the first client read has entered and <see cref="HoldInitialRead"/> is waiting.</summary>
        public TaskCompletionSource? InitialReadEntered { get; set; }

        /// <summary>
        /// When set, the first <see cref="DuplexPipeStream.DisposeAsync"/> closes the pipes and then throws <see cref="IOException"/>.
        /// Later dispose calls do not throw.
        /// </summary>
        public bool FaultClientDispose { get; set; }

        /// <summary>Zero until <see cref="TryBeginStatusLineHold"/> accepts the first completed status line.</summary>
        private int _statusLineHoldStarted;

        /// <summary>Zero until <see cref="TryBeginInitialReadHold"/> accepts the first client read.</summary>
        private int _initialReadHoldStarted;

        /// <summary>Zero until <see cref="TryConsumeDisposeFault"/> accepts the first throwing dispose.</summary>
        private int _disposeFaultUsed;

        /// <summary>Zero until <see cref="TryBeginCommandWriteHold"/> accepts the first non-QUIT write.</summary>
        private int _commandWriteHoldStarted;

        /// <summary>Set when a DATE command is read, before any optional block.</summary>
        public TaskCompletionSource? DateStarted { get; set; }

        /// <summary>When set, DATE waits here before the scripted response is written.</summary>
        public TaskCompletionSource? BlockDate { get; set; }

        /// <summary>When set, the server closes after reading DATE without writing a status.</summary>
        public bool CompleteAfterDateWithoutResponse { get; set; }

        /// <summary>
        /// When set, the client stream throws <see cref="NotSupportedException"/> on a DATE write.
        /// Connect commands are unaffected.
        /// </summary>
        public bool FaultClientDateWrite { get; set; }

        /// <summary>
        /// When set, the client stream throws <see cref="SocketException"/> on a QUIT write.
        /// Connect and other commands are unaffected.
        /// </summary>
        public bool FaultClientQuitWrite { get; set; }

        /// <summary>
        /// When set, a QUIT write waits on this gate and observes the write's cancellation token.
        /// The token is the session's remaining shutdown budget. Completing the gate lets the write continue.
        /// </summary>
        public TaskCompletionSource? HoldQuitWrite { get; set; }

        /// <summary>QUIT writes that entered <see cref="HoldQuitWrite"/>.</summary>
        private int _quitWriteStarted;

        /// <summary>
        /// When set, the first client-stream dispose throws <see cref="InvalidOperationException"/> after the pipes complete.
        /// Expected I/O failures stay on <see cref="FaultClientDispose"/>.
        /// </summary>
        public bool FaultClientDisposeUnexpected { get; set; }

        /// <summary>Zero until <see cref="TryConsumeUnexpectedDisposeFault"/> accepts the first unexpected dispose fault.</summary>
        private int _unexpectedDisposeFaultUsed;

        /// <summary>Pipe-cleanup entries. Incremented only when <see cref="DuplexPipeStream"/> completes the pipes.</summary>
        private int _clientStreamDisposals;

        /// <summary>Entries to <see cref="DuplexPipeStream.DisposeAsync"/>, including calls that skip pipe cleanup.</summary>
        private int _clientStreamDisposeAsyncInvocations;

        /// <summary>
        /// How many times the accepted client stream completed its pipes.
        /// A repeated <see cref="DuplexPipeStream.DisposeAsync"/> does not increment this.
        /// </summary>
        internal int ClientStreamDisposeCount => Volatile.Read(ref _clientStreamDisposals);

        /// <summary>
        /// How many times <see cref="DuplexPipeStream.DisposeAsync"/> was entered.
        /// This counts every session disposal call, including ones the stream's own cleanup guard ignores.
        /// </summary>
        internal int ClientStreamDisposeAsyncCount => Volatile.Read(ref _clientStreamDisposeAsyncInvocations);

        /// <summary>How many QUIT writes entered <see cref="HoldQuitWrite"/>.</summary>
        internal int QuitWriteStarted => Volatile.Read(ref _quitWriteStarted);

        /// <summary>Records one pipe cleanup of the client stream returned by <see cref="AcceptClientAsync"/>.</summary>
        internal void RecordClientStreamDisposed()
        {
            Interlocked.Increment(ref _clientStreamDisposals);
        }

        /// <summary>Records one entry to <see cref="DuplexPipeStream.DisposeAsync"/> before once-only pipe cleanup.</summary>
        internal void RecordClientStreamDisposeAsync()
        {
            Interlocked.Increment(ref _clientStreamDisposeAsyncInvocations);
        }

        /// <summary>
        /// Accepts the first client read that already copied a newline while <see cref="HoldStatusLineReturn"/> is set.
        /// </summary>
        /// <returns><see langword="true"/> when this read must wait before returning the copied bytes.</returns>
        internal bool TryBeginStatusLineHold()
        {
            return HoldStatusLineReturn is not null && Interlocked.Exchange(ref _statusLineHoldStarted, 1) == 0;
        }

        /// <summary>
        /// Accepts the first client read while <see cref="HoldInitialRead"/> is set, before the pipe is read.
        /// </summary>
        /// <returns><see langword="true"/> when this read must wait before observing greeting bytes or a closed pipe.</returns>
        internal bool TryBeginInitialReadHold()
        {
            return HoldInitialRead is not null && Interlocked.Exchange(ref _initialReadHoldStarted, 1) == 0;
        }

        /// <summary>
        /// Accepts one throwing dispose while <see cref="FaultClientDispose"/> is set.
        /// The pipes are already completed by the caller.
        /// </summary>
        /// <returns><see langword="true"/> when this dispose must throw <see cref="IOException"/>.</returns>
        internal bool TryConsumeDisposeFault()
        {
            return FaultClientDispose && Interlocked.Exchange(ref _disposeFaultUsed, 1) == 0;
        }

        /// <summary>
        /// Accepts the first non-QUIT client write while <see cref="HoldAfterCommandWrite"/> is set.
        /// </summary>
        /// <returns><see langword="true"/> when this write must wait after the pipe flush and before <see cref="DuplexPipeStream.WriteAsync"/> returns.</returns>
        internal bool TryBeginCommandWriteHold()
        {
            return HoldAfterCommandWrite is not null && Interlocked.Exchange(ref _commandWriteHoldStarted, 1) == 0;
        }

        /// <summary>
        /// Counts a QUIT write and reports whether it must wait on <see cref="HoldQuitWrite"/>.
        /// </summary>
        /// <returns><see langword="true"/> when the write must wait. The wait observes the caller's cancellation token.</returns>
        /// <remarks>Every QUIT is counted. A null gate returns false and does not increment <see cref="QuitWriteStarted"/>.</remarks>
        internal bool TryBeginQuitWriteHold()
        {
            if (HoldQuitWrite is null)
            {
                return false;
            }

            Interlocked.Increment(ref _quitWriteStarted);
            return true;
        }

        /// <summary>
        /// Accepts one unexpected dispose fault while <see cref="FaultClientDisposeUnexpected"/> is set.
        /// The pipes are already completed by the caller.
        /// </summary>
        /// <returns><see langword="true"/> when this dispose must throw <see cref="InvalidOperationException"/>.</returns>
        internal bool TryConsumeUnexpectedDisposeFault()
        {
            return FaultClientDisposeUnexpected && Interlocked.Exchange(ref _unexpectedDisposeFaultUsed, 1) == 0;
        }

        /// <summary>When set, CAPABILITIES includes this label (default STARTTLS).</summary>
        public string StartTlsCapabilityLabel { get; set; } = "STARTTLS";

        /// <summary>When true, CAPABILITIES advertises STARTTLS.</summary>
        public bool AdvertiseStartTls { get; set; }

        /// <summary>When true, CAPABILITIES advertises COMPRESS. The client must not negotiate it.</summary>
        public bool AdvertiseCompress { get; set; }

        /// <summary>Full CAPABILITIES response override, including status and terminator when complete.</summary>
        public string? CapabilitiesResponse { get; set; }

        /// <summary>When set, the server closes after writing the CAPABILITIES response.</summary>
        public bool CompleteAfterCapabilities { get; set; }

        /// <summary>STARTTLS command response. Used only when the client issues STARTTLS.</summary>
        public string StartTlsResponse { get; set; } = "382 Continue with TLS negotiation\r\n";

        /// <summary>When set, the server closes after the STARTTLS status (handshake then fails).</summary>
        public bool CompleteAfterStartTls { get; set; }

        /// <summary>Installs a string responder for commands not handled by built-in CAPABILITIES, STARTTLS, or QUIT logic.</summary>
        /// <param name="responder">Maps the full command line to a response encoded as ASCII bytes.</param>
        internal void Respond(Func<string, string> responder)
        {
            ArgumentNullException.ThrowIfNull(responder);
            _onCommand = command => Encoding.ASCII.GetBytes(responder(command));
        }

        /// <summary>Installs a byte responder for generic commands; use when the response is not plain ASCII text.</summary>
        /// <param name="responder">Maps the full command line to raw response bytes.</param>
        internal void RespondBytes(Func<string, byte[]> responder)
        {
            ArgumentNullException.ThrowIfNull(responder);
            _onCommand = responder;
        }

        /// <summary>
        /// Writes the greeting when enabled, starts the background command loop, and returns a duplex client stream.
        /// Does not listen on a network socket.
        /// </summary>
        /// <returns>A stream backed by in-memory pipes to this scripted peer.</returns>
        internal async Task<Stream> AcceptClientAsync()
        {
            if (CompleteWithoutGreeting)
            {
                await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
                return DecorateClientStream(new DuplexPipeStream(_toClient.Reader, _toServer.Writer, this));
            }

            var greeting = Encoding.ASCII.GetBytes(Greeting.EndsWith("\r\n", StringComparison.Ordinal)
                ? Greeting
                : Greeting + "\r\n");
            _toClient.Writer.Write(greeting);
            await _toClient.Writer.FlushAsync().ConfigureAwait(false);
            _ = RunServerAsync();
            return DecorateClientStream(new DuplexPipeStream(_toClient.Reader, _toServer.Writer, this));
        }

        /// <summary>
        /// When set, wraps the duplex client stream before <see cref="AcceptClientAsync"/> returns it.
        /// Null leaves the duplex stream as the session transport.
        /// </summary>
        /// <remarks>
        /// Tests install an <see cref="SslStream"/> here so a held write and a later QUIT hit the same TLS write path.
        /// The wrapper owns the duplex stream and must dispose it.
        /// </remarks>
        internal Func<Stream, Stream>? ClientStreamDecorator { get; set; }

        /// <summary>Applies <see cref="ClientStreamDecorator"/> when a test installed one.</summary>
        /// <param name="client">Duplex stream just created for the accepted client.</param>
        /// <returns>The decorator result, or <paramref name="client"/> when no decorator is set.</returns>
        private Stream DecorateClientStream(Stream client) =>
            ClientStreamDecorator is { } decorate ? decorate(client) : client;

        /// <summary>
        /// Reads CRLF-delimited commands, applies built-in handlers and the configured responder, and completes the outbound pipe on exit.
        /// Swallows exceptions so the client observes a closed pipe rather than this task faulting.
        /// </summary>
        private async Task RunServerAsync()
        {
            var reader = new NntpTestLineReader(_toServer.Reader);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    LastCommandUsedCrlf = reader.LastLineUsedCrlf;
                    _commands.Add(line);
                    var text = Encoding.ASCII.GetString(line);
                    if (text.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (text.Equals("CAPABILITIES", StringComparison.OrdinalIgnoreCase))
                    {
                        var capabilities = Encoding.ASCII.GetBytes(BuildCapabilitiesResponse());
                        _toClient.Writer.Write(capabilities);
                        var capabilitiesFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                        if (CompleteAfterCapabilities || capabilitiesFlush.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    if (text.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        var startTls = Encoding.ASCII.GetBytes(
                            StartTlsResponse.EndsWith("\r\n", StringComparison.Ordinal)
                                ? StartTlsResponse
                                : StartTlsResponse + "\r\n");
                        _toClient.Writer.Write(startTls);
                        var startTlsFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                        if (CompleteAfterStartTls || startTlsFlush.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    if (text.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase))
                    {
                        ArticleStarted?.TrySetResult();
                        if (BlockArticle is not null)
                        {
                            await BlockArticle.Task.ConfigureAwait(false);
                        }

                        if (HoldArticleBody is not null)
                        {
                            _toClient.Writer.Write("220 0 <a@b> article follows\r\n"u8);
                            var statusFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                            ArticleStatusWritten?.TrySetResult();
                            await HoldArticleBody.Task.ConfigureAwait(false);
                            _toClient.Writer.Write("From: a@b\r\n\r\nbody\r\n.\r\n"u8);
                            var bodyFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                            if (CompleteAfterResponse || statusFlush.IsCompleted || bodyFlush.IsCompleted)
                            {
                                break;
                            }

                            continue;
                        }
                    }
                    else if (text.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        DateStarted?.TrySetResult();
                        if (BlockDate is not null)
                        {
                            await BlockDate.Task.ConfigureAwait(false);
                        }

                        if (CompleteAfterDateWithoutResponse)
                        {
                            break;
                        }
                    }

                    var response = _onCommand(text);
                    _toClient.Writer.Write(response);
                    var flush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                    if (CompleteAfterResponse || flush.IsCompleted)
                    {
                        break;
                    }
                }
            }
            // codeql[cs/catch-of-all-exceptions] The scripted peer must finish the pipe after any read or command failure.
            catch (Exception)
            {
                /* The caller observes the closed pipe rather than this task. */
            }
            finally
            {
                await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Builds the CAPABILITIES multiline from flags and optional override; does not reflect live server state.</summary>
        /// <returns>Full CAPABILITIES response including status line and terminator when synthesized.</returns>
        private string BuildCapabilitiesResponse()
        {
            if (CapabilitiesResponse is not null)
            {
                return CapabilitiesResponse.EndsWith("\r\n", StringComparison.Ordinal)
                       || CapabilitiesResponse.Length == 0
                    ? CapabilitiesResponse
                    : CapabilitiesResponse + "\r\n";
            }

            var body = new StringBuilder();
            body.Append("101 Capability list follows\r\n");
            body.Append("VERSION 2\r\n");
            body.Append("READER\r\n");
            if (AdvertiseStartTls)
            {
                body.Append(StartTlsCapabilityLabel);
                body.Append("\r\n");
            }

            if (AdvertiseCompress)
            {
                body.Append("COMPRESS DEFLATE\r\n");
            }

            body.Append(".\r\n");
            return body.ToString();
        }
    }

    /// <summary>
    /// Stream adapter over PipeReader/PipeWriter for scripted NNTP server tests.
    /// </summary>

    internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer, ScriptedNntpServer? owner = null) : Stream
    {
        /// <summary>One after the first disposing call. Later calls do not complete the pipes again.</summary>
        private int _disposed;
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <summary>
        /// Completes when this stream is still open. Flushing after disposal throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        /// <param name="cancellationToken">Ignored. This flush does not write to the pipes.</param>
        /// <returns>A completed task when the stream has not been disposed.</returns>
        /// <exception cref="ObjectDisposedException">The stream was already disposed.</exception>
        /// <remarks>
        /// Command bytes are pushed in <see cref="WriteAsync"/>. The session calls this method afterward.
        /// A disposed flush fails the caller with <see cref="ObjectDisposedException"/>, which is distinct from an empty status read.
        /// </remarks>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(DuplexPipeStream));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (owner is not null && owner.TryBeginInitialReadHold())
            {
                owner.InitialReadEntered?.TrySetResult();
                await owner.HoldInitialRead!.Task.ConfigureAwait(false);
            }

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var taken = (int)Math.Min(buffer.Length, result.Buffer.Length);
                if (taken == 0)
                {
                    reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        return 0;
                    }

                    continue;
                }

                result.Buffer.Slice(0, taken).CopyTo(buffer.Span);
                reader.AdvanceTo(result.Buffer.GetPosition(taken));
                if (owner is not null
                    && buffer.Span[..taken].IndexOf((byte)'\n') >= 0
                    && owner.TryBeginStatusLineHold())
                {
                    owner.StatusLineCopied?.TrySetResult();
                    if (owner.ObserveCommandHoldCancellation)
                    {
                        await owner.HoldStatusLineReturn!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await owner.HoldStatusLineReturn!.Task.ConfigureAwait(false);
                    }
                }

                return taken;
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        /// <remarks>
        /// Honors <see cref="ScriptedNntpServer.HoldQuitWrite"/>, <see cref="ScriptedNntpServer.FaultClientDateWrite"/>, and
        /// <see cref="ScriptedNntpServer.FaultClientQuitWrite"/> on the optional owner.
        /// A held QUIT waits on that gate and on <paramref name="cancellationToken"/> before any bytes are written.
        /// A QUIT fault throws <see cref="SocketException"/> before any bytes are written.
        /// </remarks>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (owner is not null
                && buffer.Span.IndexOf("QUIT"u8) >= 0
                && owner.TryBeginQuitWriteHold())
            {
                await owner.HoldQuitWrite!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (owner?.FaultClientQuitWrite == true
                && buffer.Span.IndexOf("QUIT"u8) >= 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            if (owner?.FaultClientDateWrite == true
                && buffer.Span.IndexOf("DATE"u8) >= 0)
            {
                throw new NotSupportedException("DATE write fault");
            }

            writer.Write(buffer.Span);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (owner is not null
                && buffer.Span.IndexOf("QUIT"u8) < 0
                && owner.TryBeginCommandWriteHold())
            {
                owner.CommandWriteFlushed?.TrySetResult();
                if (owner.ObserveCommandHoldCancellation)
                {
                    await owner.HoldAfterCommandWrite!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await owner.HoldAfterCommandWrite!.Task.ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Counts every disposal entry, then lets <see cref="Dispose(bool)"/> complete the pipes once.
        /// </summary>
        /// <returns>The completed disposal task from <see cref="Stream.DisposeAsync"/>.</returns>
        public override async ValueTask DisposeAsync()
        {
            owner?.RecordClientStreamDisposeAsync();
            await base.DisposeAsync().ConfigureAwait(false);
            if (owner?.TryConsumeDisposeFault() == true)
            {
                throw new IOException("plaintext dispose failed");
            }

            if (owner?.TryConsumeUnexpectedDisposeFault() == true)
            {
                throw new InvalidOperationException("teardown fault");
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner?.RecordClientStreamDisposed();
                reader.Complete();
                writer.Complete();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Reads CRLF-delimited lines from a PipeReader for scripted NNTP responses.
    /// </summary>

    internal sealed class NntpTestLineReader(PipeReader reader)
    {
        /// <summary>Whether the line returned from the latest read ended with CRLF before the line feed.</summary>
        public bool LastLineUsedCrlf { get; private set; }

        /// <summary>Reads the next LF-terminated line from the pipe, stripping a trailing CR when present.</summary>
        /// <returns>Line bytes without the delimiter, or null when the pipe completes before a line feed.</returns>
        internal async Task<byte[]?> ReadLineAsync()
        {
            while (true)
            {
                var result = await reader.ReadAsync().ConfigureAwait(false);
                var buffer = result.Buffer;
                var position = buffer.PositionOf((byte)'\n');
                if (position is null)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        return null;
                    }

                    continue;
                }

                var line = buffer.Slice(0, position.Value);
                var bytes = line.ToArray();
                LastLineUsedCrlf = bytes.Length > 0 && bytes[^1] == (byte)'\r';
                if (LastLineUsedCrlf)
                {
                    bytes = bytes[..^1];
                }

                reader.AdvanceTo(buffer.GetPosition(1, position.Value));
                return bytes;
            }
        }
    }
}
