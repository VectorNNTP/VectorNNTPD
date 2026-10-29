using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;

namespace VectorNNTPD.ReaderService.Networking.Transport
{
    using VectorNNTPD.Common.Core;
    /// <summary>
    /// Duplex pipe transport over a replaceable byte-stream I/O owner (<see cref="ConnectionByteTransport"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership: this type owns the accepted socket and the <see cref="ConnectionByteTransport"/> (plain
    /// <see cref="NetworkStream"/> or authenticated <see cref="SslStream"/>). The application owns
    /// consumption of <see cref="Input"/> / production to <see cref="Output"/>.
    /// </para>
    /// <para>
    /// Plain connections may call <see cref="UpgradeToTlsAsync"/> to authenticate TLS on the same socket.
    /// Plain or TLS connections may call <see cref="UpgradeToDeflateAsync"/> to activate bidirectional raw
    /// DEFLATE above the current byte stream (RFC 8054 layering: NNTP → DEFLATE → TLS → TCP).
    /// Pumps keep running across upgrades; exclusive stream ownership is enforced by transport quiescence.
    /// The NNTP STARTTLS and COMPRESS commands live in the session layer and invoke these upgrades.
    /// </para>
    /// <para>
    /// NNTPD performs server-side TLS authentication only. Client certificates are never requested or validated.
    /// </para>
    /// </remarks>
    public sealed class NntpConnection : INntpConnection
    {
        private const int ModePlain = 0;
        private const int ModeUpgrading = 1;
        private const int ModeTls = 2;

        private const int CompressionOff = 0;
        private const int CompressionUpgrading = 1;
        private const int CompressionOn = 2;

        private readonly ILogger _logger;
        private readonly Pipe _inputPipe;
        private readonly Pipe _outputPipe;
        private readonly CancellationTokenSource _connectionCts = new();
        private readonly object _completeGate = new();
        private readonly ConnectionClientIdentity _clientIdentity;
        private readonly IFeedDiagnostics? _feedDiagnostics;
        private Socket? _socket;
        private ConnectionByteTransport? _transport;
        private TlsCertificateLease? _certificateLease;
        private byte[]? _receivePrefix;
        private Task? _receiveTask;
        private Task? _sendTask;
        private int _mode;
        private int _compression;
        private int _inplaceUpgradeBusy;
        private int _completed;
        private int _outputWriterCompleted;
        private int _outputReaderCompleted;
        private int _inputReaderCompleted;
        private int _inputWriterCompleted;
        private int _inputReaderConsumer;
        private int _disconnectReason;
        private int _disconnectedLogged;
        private int _disposed;
        private int _sendPumpAwaitingOutput;
        private long _outboundIdleVersion;
        private TaskCompletionSource? _outboundIdleWaiter;
        private string? _negotiatedTlsVersion;
        private string? _negotiatedCipher;

        private NntpConnection(
            Socket socket,
            ConnectionByteTransport transport,
            EndPoint? remoteEndPoint,
            EndPoint? localEndPoint,
            bool isTls,
            ConnectionClientIdentity clientIdentity,
            ILogger logger,
            IFeedDiagnostics? feedDiagnostics = null)
        {
            _socket = socket;
            _transport = transport;
            RemoteEndPoint = remoteEndPoint;
            LocalEndPoint = localEndPoint;
            _mode = isTls ? ModeTls : ModePlain;
            _clientIdentity = clientIdentity;
            _logger = logger;
            _feedDiagnostics = feedDiagnostics;
            var inputOptions = NntpPipeOptions.Create();
            var outputOptions = NntpPipeOptions.CreateOutput();
            _inputPipe = new Pipe(inputOptions);
            _outputPipe = new Pipe(outputOptions);
            if (transport.Io is { } io)
            {
                io.TxPipePauseBytes = outputOptions.PauseWriterThreshold;
                io.TxPipeResumeBytes = outputOptions.ResumeWriterThreshold;
            }
        }

        /// <inheritdoc />
        public PipeReader Input => _inputPipe.Reader;

        /// <inheritdoc />
        public PipeWriter Output => _outputPipe.Writer;

        /// <inheritdoc />
        public IOutboundRateCap? OutboundRate => _transport?.RateLimiter;

        /// <inheritdoc />
        public EndPoint? RemoteEndPoint { get; }

        /// <inheritdoc />
        public EndPoint? LocalEndPoint { get; }

        /// <inheritdoc />
        public ConnectionClientIdentity ClientIdentity => _clientIdentity;

        /// <inheritdoc />
        public bool IsTls => Volatile.Read(ref _mode) == ModeTls;

        /// <inheritdoc />
        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            var version = Volatile.Read(ref _negotiatedTlsVersion);
            var suite = Volatile.Read(ref _negotiatedCipher);
            if (version is null || suite is null)
            {
                tlsVersion = string.Empty;
                cipher = string.Empty;
                return false;
            }

            tlsVersion = version;
            cipher = suite;
            return true;
        }

        /// <inheritdoc />
        public bool IsCompressed => Volatile.Read(ref _compression) == CompressionOn;

        /// <inheritdoc />
        public long OutboundIdleVersion => Volatile.Read(ref _outboundIdleVersion);

        /// <inheritdoc />
        public bool IsCompleted => Volatile.Read(ref _completed) == 1;

        /// <summary>Test-only: current byte transport (null after teardown closes it).</summary>
        internal ConnectionByteTransport? ByteTransportForTests => _transport;

        /// <summary>Test-only: send pump task started by <see cref="StartPumps"/>.</summary>
        internal Task? SendPumpTaskForTests => _sendTask;

        /// <summary>Test-only: whether <c>Output.Writer</c> has been completed.</summary>
        internal bool OutputWriterCompletedForTests => Volatile.Read(ref _outputWriterCompleted) == 1;

        /// <summary>Test-only: whether <c>Output.Reader</c> has been completed exactly once.</summary>
        internal bool OutputReaderCompletedForTests => Volatile.Read(ref _outputReaderCompleted) == 1;

        /// <summary>Test-only: whether <c>Input.Reader</c> has been completed exactly once.</summary>
        internal bool InputReaderCompletedForTests => Volatile.Read(ref _inputReaderCompleted) == 1;

        /// <summary>Test-only: whether <c>Input.Writer</c> has been completed exactly once.</summary>
        internal bool InputWriterCompletedForTests => Volatile.Read(ref _inputWriterCompleted) == 1;

        /// <summary>Test-only: receive pump task started by <see cref="StartPumps"/>.</summary>
        internal Task? ReceivePumpTaskForTests => _receiveTask;

        /// <summary>Test-only: 1 when the send pump is awaiting more <see cref="Output"/>.</summary>
        internal int SendPumpAwaitingOutputForTests => Volatile.Read(ref _sendPumpAwaitingOutput);

        /// <inheritdoc />
        public Task PauseReadsAsync(CancellationToken cancellationToken = default)
        {
            var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
            return transport.PauseReadsAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            WaitForOutboundIdleAfterAsync(outboundIdleVersionBeforeFlush, cancellationToken);

        /// <inheritdoc />
        public async Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default)
        {
            await PauseReadsAsync(cancellationToken).ConfigureAwait(false);
            await WaitForOutboundIdleAfterAsync(outboundIdleVersionBeforeFlush, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public CancellationToken ConnectionClosed => _connectionCts.Token;

        /// <summary>Creates and starts a plain TCP connection transport.</summary>
        public static NntpConnection StartPlain(
            Socket socket,
            ConnectionClientIdentity clientIdentity,
            ILogger logger,
            ReadOnlyMemory<byte> receivePrefix = default,
            IFeedDiagnostics? feedDiagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(clientIdentity);
            ArgumentNullException.ThrowIfNull(logger);

            ConfigureAcceptedSocket(socket);
            var network = new NetworkStream(socket, ownsSocket: false);
            var transport = new ConnectionByteTransport(network, isTls: false)
            {
                Io = TransportIoProbe.CreateSession(),
            };
            var connection = new NntpConnection(
                socket,
                transport,
                TryGetRemote(socket),
                TryGetLocal(socket),
                isTls: false,
                clientIdentity,
                logger,
                feedDiagnostics);
            if (!receivePrefix.IsEmpty)
            {
                connection._receivePrefix = receivePrefix.ToArray();
            }

            connection.StartPumps();
            return connection;
        }

        /// <summary>
        /// Performs a server TLS handshake then starts the duplex pipe pumps over the encrypted stream.
        /// </summary>
        public static async Task<NntpConnection> StartTlsAsync(
            Socket socket,
            ITlsCertificateContextProvider certificateProvider,
            ConnectionClientIdentity clientIdentity,
            ILogger logger,
            CancellationToken cancellationToken,
            ReadOnlyMemory<byte> tlsPrefix = default,
            IFeedDiagnostics? feedDiagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(certificateProvider);
            ArgumentNullException.ThrowIfNull(clientIdentity);
            ArgumentNullException.ThrowIfNull(logger);

            ConfigureAcceptedSocket(socket);
            var remote = TryGetRemote(socket);
            var local = TryGetLocal(socket);
            var lease = certificateProvider.Acquire();

            var networkStream = new NetworkStream(socket, ownsSocket: false);
            Stream sslInner = tlsPrefix.IsEmpty
                ? networkStream
                : new PrefixedStream(networkStream, tlsPrefix, leaveInnerOpen: false);
            var limiter = new OutboundRateLimiter(sslInner, initialMaxSendBytesPerSecond: 0, leaveInnerOpen: false);
            var sslStream = new SslStream(limiter, leaveInnerStreamOpen: false);

            try
            {
                await sslStream
                    .AuthenticateAsServerAsync(CreateServerAuthenticationOptions(lease.Context), cancellationToken)
                    .ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
                lease.Dispose();
                socket.Dispose();
                throw;
            }

            TlsNegotiationLogging.Capture(sslStream, out var tlsVersion, out var cipher);
            var transport = new ConnectionByteTransport(sslStream, isTls: true, limiter)
            {
                Io = TransportIoProbe.CreateSession(),
            };
            var connection = new NntpConnection(socket, transport, remote, local, isTls: true, clientIdentity, logger, feedDiagnostics)
            {
                _certificateLease = lease,
                _negotiatedTlsVersion = tlsVersion,
                _negotiatedCipher = cipher,
            };

            connection.StartPumps();
            TransportLogMessages.TlsHandshakeCompleted(logger, remote);
            return connection;
        }

        /// <inheritdoc />
        public async Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(certificateProvider);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            if (Volatile.Read(ref _completed) == 1)
            {
                throw new ObjectDisposedException(nameof(NntpConnection), "Connection is closed.");
            }

            if (Volatile.Read(ref _mode) == ModeTls)
            {
                throw new InvalidOperationException("Connection is already TLS-protected.");
            }

            if (Volatile.Read(ref _compression) != CompressionOff)
            {
                throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
            }

            if (Interlocked.CompareExchange(ref _inplaceUpgradeBusy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A transport layer upgrade is already in progress.");
            }

            var prior = Interlocked.CompareExchange(ref _mode, ModeUpgrading, ModePlain);
            if (prior != ModePlain)
            {
                Volatile.Write(ref _inplaceUpgradeBusy, 0);
                if (prior == ModeUpgrading)
                {
                    throw new InvalidOperationException("A TLS upgrade is already in progress.");
                }

                if (prior == ModeTls)
                {
                    throw new InvalidOperationException("Connection is already TLS-protected.");
                }

                throw new InvalidOperationException("Connection cannot be upgraded in its current state.");
            }

            if (Volatile.Read(ref _compression) != CompressionOff)
            {
                Volatile.Write(ref _mode, ModePlain);
                Volatile.Write(ref _inplaceUpgradeBusy, 0);
                throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
            }

            var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
            Exception? failure = null;
            var quiesced = false;
            var preconditionFailed = false;

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _connectionCts.Token);

                // STARTTLS pauses reads before writing 382; other callers may still need a pause here.
                if (!transport.IsReadsPaused)
                {
                    await transport.PauseReadsAsync(linked.Token).ConfigureAwait(false);
                }

                await WaitForOutboundIdleAsync(linked.Token).ConfigureAwait(false);
                await transport.QuiesceWritesAsync(linked.Token).ConfigureAwait(false);
                quiesced = true;

                try
                {
                    EnsureApplicationInputDrainedForUpgrade();
                }
                catch (InvalidOperationException)
                {
                    preconditionFailed = true;
                    transport.ResumeFromQuiesceWithoutUpgrade();
                    quiesced = false;
                    Volatile.Write(ref _mode, ModePlain);
                    throw;
                }

                var lease = certificateProvider.Acquire();
                var plainStream = transport.TakeQuiescedStreamForTlsWrap();
                var upgradePrefix = transport.TakePendingUpgradePrefix();
                Stream sslInner = upgradePrefix.IsEmpty
                    ? plainStream
                    : new PrefixedStream(plainStream, upgradePrefix, leaveInnerOpen: false);
                var sslStream = new SslStream(sslInner, leaveInnerStreamOpen: false);

                try
                {
                    await sslStream
                        .AuthenticateAsServerAsync(CreateServerAuthenticationOptions(lease.Context), linked.Token)
                        .ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    try
                    {
                        await sslStream.DisposeAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                    {
                        _ = ignored; // observed; intentionally ignored at this best-effort boundary
                        // Best-effort; disposes plain NetworkStream as well.
                    }

                    lease.Dispose();
                    failure = ex;
                    throw;
                }

                _certificateLease = lease;
                TlsNegotiationLogging.Capture(sslStream, out var tlsVersion, out var cipher);
                Volatile.Write(ref _negotiatedTlsVersion, tlsVersion);
                Volatile.Write(ref _negotiatedCipher, cipher);
                transport.PublishTlsAndResume(sslStream);
                Volatile.Write(ref _mode, ModeTls);
                TransportLogMessages.InPlaceTlsUpgradeCompleted(_logger, RemoteEndPoint);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (failure is null && !preconditionFailed)
            {
                failure = ex;
                throw;
            }
            finally
            {
                if (failure is not null)
                {
                    // Mark terminal and cancel ConnectionClosed before waking pumps so the session
                    // dispatcher cannot write a secondary NNTP status onto a dying transport.
                    if (Volatile.Read(ref _completed) == 0)
                    {
                        await CompleteAsync(failure).ConfigureAwait(false);
                    }

                    if (quiesced)
                    {
                        transport.AbortQuiesceForConnectionTeardown();
                    }
                    else if (Volatile.Read(ref _mode) == ModeUpgrading)
                    {
                        Volatile.Write(ref _mode, ModePlain);
                    }

                    Volatile.Write(ref _inplaceUpgradeBusy, 0);
                }
                else
                {
                    Volatile.Write(ref _inplaceUpgradeBusy, 0);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            if (Volatile.Read(ref _completed) == 1)
            {
                throw new ObjectDisposedException(nameof(NntpConnection), "Connection is closed.");
            }

            if (Volatile.Read(ref _compression) == CompressionOn)
            {
                throw new InvalidOperationException("Connection is already DEFLATE-compressed.");
            }

            if (Interlocked.CompareExchange(ref _inplaceUpgradeBusy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A transport layer upgrade is already in progress.");
            }

            var prior = Interlocked.CompareExchange(ref _compression, CompressionUpgrading, CompressionOff);
            if (prior != CompressionOff)
            {
                Volatile.Write(ref _inplaceUpgradeBusy, 0);
                if (prior == CompressionUpgrading)
                {
                    throw new InvalidOperationException("A DEFLATE upgrade is already in progress.");
                }

                throw new InvalidOperationException("Connection is already DEFLATE-compressed.");
            }

            if (Volatile.Read(ref _mode) == ModeUpgrading)
            {
                Volatile.Write(ref _compression, CompressionOff);
                Volatile.Write(ref _inplaceUpgradeBusy, 0);
                throw new InvalidOperationException("A TLS upgrade is already in progress.");
            }

            var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
            Exception? failure = null;
            var quiesced = false;
            var preconditionFailed = false;

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _connectionCts.Token);

                if (!transport.IsReadsPaused)
                {
                    await transport.PauseReadsAsync(linked.Token).ConfigureAwait(false);
                }

                await WaitForOutboundIdleAsync(linked.Token).ConfigureAwait(false);
                await transport.QuiesceWritesAsync(linked.Token).ConfigureAwait(false);
                quiesced = true;

                try
                {
                    EnsureApplicationInputDrainedForUpgrade();
                }
                catch (InvalidOperationException)
                {
                    preconditionFailed = true;
                    transport.ResumeFromQuiesceWithoutUpgrade();
                    quiesced = false;
                    Volatile.Write(ref _compression, CompressionOff);
                    throw;
                }

                var inner = transport.TakeQuiescedStreamForDeflateWrap();
                var upgradePrefix = transport.TakePendingUpgradePrefix();
                Stream deflateInner = upgradePrefix.IsEmpty
                    ? inner
                    : new PrefixedStream(inner, upgradePrefix, leaveInnerOpen: false);
                var deflate = new NntpDeflateStream(deflateInner);
                transport.PublishDeflateAndResume(deflate);
                Volatile.Write(ref _compression, CompressionOn);
                TransportLogMessages.InPlaceDeflateActivated(_logger, RemoteEndPoint);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (!preconditionFailed)
            {
                failure = ex;
                throw;
            }
            finally
            {
                if (failure is not null)
                {
                    if (Volatile.Read(ref _completed) == 0)
                    {
                        await CompleteAsync(failure).ConfigureAwait(false);
                    }

                    if (quiesced)
                    {
                        transport.AbortQuiesceForConnectionTeardown();
                    }
                    else if (Volatile.Read(ref _compression) == CompressionUpgrading)
                    {
                        Volatile.Write(ref _compression, CompressionOff);
                    }
                }

                Volatile.Write(ref _inplaceUpgradeBusy, 0);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <see cref="Output"/> writer completion and connection cancellation may unblock
        /// <c>SendAsync</c>. <c>Output.Reader</c> is owned exclusively by the send pump: this
        /// method must not complete that reader while <c>SendAsync</c> can still
        /// <c>AdvanceTo</c> an examined buffer. After the send pump returns, a best-effort
        /// reader complete runs only when the pump did not complete the reader itself.
        /// </para>
        /// <para>
        /// Connection cancellation unblocks the receive pump. <c>Input.Writer</c> is owned
        /// exclusively by <c>ReceiveAsync</c>: this method must not complete the writer while
        /// <c>GetMemory</c> / <c>Advance</c> can still run. The receive pump completes the writer
        /// in <c>finally</c> after it exits. Completing the writer here while the session has
        /// already completed the reader would <c>CompletePipe</c> and invalidate the writing head.
        /// </para>
        /// <para>
        /// <c>Input.Reader</c> is owned exclusively by the session RX consumer
        /// (<c>NntpSession</c> / <c>NntpContinuousRxReader</c>): this method must not complete
        /// the reader while <c>ReadAsync</c> / <c>AdvanceTo</c> can still run. The session
        /// completes the reader after the RX loop has stopped.
        /// </para>
        /// </remarks>
        public async Task CompleteAsync(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            NoteDisconnectReason(ClassifyCompleteReason(exception));
            LogTcpDisconnectedOnce();

            try
            {
                await _connectionCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Already disposed.
            }

            try
            {
                await _outputPipe.Writer.CompleteAsync(exception).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort.
            }
            finally
            {
                Interlocked.Exchange(ref _outputWriterCompleted, 1);
            }

            var receive = _receiveTask;
            var send = _sendTask;
            if (receive is not null)
            {
                try
                {
                    await receive.ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Observed via pump logging.
                }
            }
            else
            {
                await CompleteInputWriterAsync(exception).ConfigureAwait(false);
            }

            if (send is not null)
            {
                try
                {
                    await send.ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Observed via pump logging.
                }

                if (!send.IsCompletedSuccessfully)
                {
                    await CompleteOutputReaderBestEffortAsync(exception).ConfigureAwait(false);
                }
            }
            else
            {
                await CompleteOutputReaderBestEffortAsync(exception).ConfigureAwait(false);
            }

            WriteTransportIoDump();
            await CloseTransportSocketAsync().ConfigureAwait(false);
        }

        private void WriteTransportIoDump()
        {
            var io = _transport?.Io;
            if (io is null || !TransportIoProbe.IsEnabled || TransportIoProbe.OutputDirectory is not { } dir)
            {
                return;
            }

            try
            {
                var label = RemoteEndPoint?.ToString() ?? "connection";
                io.Write(dir, label);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Diagnostic dump must not affect teardown.
            }
        }

        /// <summary>
        /// Completes <c>Output.Reader</c> only when the send pump did not already do so.
        /// </summary>
        private async Task CompleteOutputReaderBestEffortAsync(Exception? exception)
        {
            if (Interlocked.Exchange(ref _outputReaderCompleted, 1) == 1)
            {
                return;
            }

            try
            {
                await _outputPipe.Reader.CompleteAsync(exception).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort after a faulted send pump.
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await CompleteAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _inputReaderConsumer) == 0)
            {
                await CompleteInputReaderAsync().ConfigureAwait(false);
            }

            _connectionCts.Dispose();
            _certificateLease?.Dispose();
            _certificateLease = null;
        }

        /// <summary>
        /// Records the first known disconnect cause. Later notes do not overwrite.
        /// </summary>
        internal void NoteDisconnectReason(TcpDisconnectReason reason)
        {
            if (reason == TcpDisconnectReason.Unspecified)
            {
                return;
            }

            Interlocked.CompareExchange(ref _disconnectReason, (int)reason, (int)TcpDisconnectReason.Unspecified);
        }

        /// <summary>Test-only: first-wins disconnect reason recorded for this connection.</summary>
        internal TcpDisconnectReason DisconnectReasonForTests =>
            (TcpDisconnectReason)Volatile.Read(ref _disconnectReason);

        private static TcpDisconnectReason ClassifyCompleteReason(Exception? exception) =>
            exception switch
            {
                null => TcpDisconnectReason.LocalClose,
                OperationCanceledException => TcpDisconnectReason.Cancellation,
                _ => TcpDisconnectReason.ConnectionClosed,
            };

        private void LogTcpDisconnectedOnce()
        {
            if (Interlocked.Exchange(ref _disconnectedLogged, 1) == 1)
            {
                return;
            }

            var reason = (TcpDisconnectReason)Volatile.Read(ref _disconnectReason);
            if (reason == TcpDisconnectReason.Unspecified)
            {
                reason = TcpDisconnectReason.ConnectionClosed;
            }

            ConnectionAcceptanceLogging.LogDisconnected(
                _logger,
                RemoteEndPoint,
                LocalEndPoint,
                reason.ToString());
        }

        /// <summary>
        /// Marks the session RX loop as the exclusive <see cref="Input"/> reader owner.
        /// </summary>
        internal void AttachInputReaderConsumer() => Interlocked.Exchange(ref _inputReaderConsumer, 1);

        /// <summary>
        /// Completes <see cref="Input"/> after the application consumer has stopped reading.
        /// Safe to call multiple times.
        /// </summary>
        internal async Task CompleteInputReaderAsync()
        {
            Interlocked.Exchange(ref _inputReaderConsumer, 0);
            if (Interlocked.Exchange(ref _inputReaderCompleted, 1) == 1)
            {
                return;
            }

            try
            {
                await _inputPipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort; reader may already be completed by a test host.
            }
        }

        /// <summary>
        /// Completes <c>Input.Writer</c> exactly once. Owned by <c>ReceiveAsync</c>;
        /// <see cref="CompleteAsync"/> calls this only when no receive pump was started.
        /// </summary>
        private async Task CompleteInputWriterAsync(Exception? exception)
        {
            if (Interlocked.Exchange(ref _inputWriterCompleted, 1) == 1)
            {
                return;
            }

            try
            {
                await _inputPipe.Writer.CompleteAsync(exception).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort.
            }
        }

        /// <summary>Shared server TLS options (server auth only; no client certificates).</summary>
        internal static SslServerAuthenticationOptions CreateServerAuthenticationOptions(
            SslStreamCertificateContext certificateContext) =>
            new()
            {
                ServerCertificateContext = certificateContext,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

        private void StartPumps()
        {
            _receiveTask = ReceiveAsync();
            _sendTask = SendAsync();
            _ = ObservePumpAsync(_receiveTask, "receive");
            _ = ObservePumpAsync(_sendTask, "send");
        }

        private void EnsureApplicationInputDrainedForUpgrade()
        {
            if (!_inputPipe.Reader.TryRead(out var result))
            {
                return;
            }

            try
            {
                if (!result.Buffer.IsEmpty)
                {
                    throw new InvalidOperationException(
                        "Cannot upgrade to TLS while unconsumed plaintext remains in Input. " +
                        "Drain application data before calling UpgradeToTlsAsync.");
                }
            }
            finally
            {
                // Leave unconsumed bytes in place for the caller (do not examine past start on failure).
                _inputPipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
            }
        }

        private async Task ObservePumpAsync(Task pump, string name)
        {
            Exception? error = null;
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (_connectionCts.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected on shutdown.
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                error = ex;
                TransportLogMessages.PumpEndedWithError(_logger, ex, name);
                NoteDisconnectReason(
                    string.Equals(name, "send", StringComparison.Ordinal)
                        ? TcpDisconnectReason.SendError
                        : TcpDisconnectReason.ReceiveError);
            }
            finally
            {
                if (Volatile.Read(ref _completed) == 0)
                {
                    _ = CompleteAsync(error);
                }
            }
        }

        private async Task WaitForOutboundIdleAsync(CancellationToken cancellationToken) =>
            await WaitForOutboundIdleAfterAsync(Volatile.Read(ref _outboundIdleVersion) - 1, cancellationToken)
                .ConfigureAwait(false);

        private async Task WaitForOutboundIdleAfterAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Volatile.Read(ref _sendPumpAwaitingOutput) == 1
                    && Volatile.Read(ref _outboundIdleVersion) > outboundIdleVersionBeforeFlush)
                {
                    return;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _outboundIdleWaiter, waiter);

                if (Volatile.Read(ref _sendPumpAwaitingOutput) == 1
                    && Volatile.Read(ref _outboundIdleVersion) > outboundIdleVersionBeforeFlush)
                {
                    Volatile.Write(ref _outboundIdleWaiter, null);
                    waiter.TrySetResult();
                    return;
                }

                await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void SignalOutboundIdle()
        {
            Interlocked.Increment(ref _outboundIdleVersion);
            Volatile.Write(ref _sendPumpAwaitingOutput, 1);
            var waiter = Interlocked.Exchange(ref _outboundIdleWaiter, null);
            waiter?.TrySetResult();
        }

        private void SignalOutboundBusy() => Volatile.Write(ref _sendPumpAwaitingOutput, 0);

        private static async ValueTask<FlushResult> FlushInputAsync(
            PipeWriter writer,
            CancellationToken token,
            TransportIoSession? io)
        {
            if (io is null)
            {
                return await writer.FlushAsync(token).ConfigureAwait(false);
            }

            var started = Stopwatch.GetTimestamp();
            var vt = writer.FlushAsync(token);
            if (vt.IsCompletedSuccessfully)
            {
                io.RecordRxPipeFlush(started, started, sync: true);
                return vt.Result;
            }

            var awaitStart = Stopwatch.GetTimestamp();
            var result = await vt.ConfigureAwait(false);
            io.RecordRxPipeFlush(started, awaitStart, sync: false);
            return result;
        }

        private static async ValueTask<ReadResult> ReadOutputAsync(
            PipeReader reader,
            CancellationToken token,
            TransportIoSession? io)
        {
            if (io is null)
            {
                return await reader.ReadAsync(token).ConfigureAwait(false);
            }

            var started = Stopwatch.GetTimestamp();
            var vt = reader.ReadAsync(token);
            if (vt.IsCompletedSuccessfully)
            {
                var completed = vt.Result;
                io.RecordTxPipeWait(started, started, sync: true);
                io.RecordTxReadShape(completed.Buffer);
                return completed;
            }

            var awaitStart = Stopwatch.GetTimestamp();
            var result = await vt.ConfigureAwait(false);
            io.RecordTxPipeWait(started, awaitStart, sync: false);
            io.RecordTxReadShape(result.Buffer);
            return result;
        }

        private async Task ReceiveAsync()
        {
            Exception? error = null;
            try
            {
                var transport = _transport ?? throw new InvalidOperationException("Transport missing.");
                var writer = _inputPipe.Writer;
                var token = _connectionCts.Token;

                var prefix = Interlocked.Exchange(ref _receivePrefix, null);
                if (prefix is { Length: > 0 })
                {
                    var memory = writer.GetMemory(prefix.Length);
                    prefix.CopyTo(memory);
                    writer.Advance(prefix.Length);
                    RecordReceivedBytes(prefix.Length);
                    var prefixFlush = await FlushInputAsync(writer, token, transport.Io).ConfigureAwait(false);
                    if (prefixFlush.IsCompleted || prefixFlush.IsCanceled)
                    {
                        return;
                    }
                }

                while (!token.IsCancellationRequested)
                {
                    var memory = writer.GetMemory(NntpPipeOptions.MinimumSegmentSize);
                    var bytes = await transport.ReadAsync(memory, token).ConfigureAwait(false);
                    if (bytes == 0)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            NoteDisconnectReason(TcpDisconnectReason.RemoteClosed);
                        }

                        break;
                    }

                    // If Quiesce completed while this read returned, octets belong to the upgrade wrap
                    // (e.g. TLS ClientHello), not the application Input pipe.
                    if (!transport.TryCommitReadToApplication(memory.Span[..bytes]))
                    {
                        continue;
                    }

                    RecordReceivedBytes(bytes);
                    writer.Advance(bytes);
                    var flush = await FlushInputAsync(writer, token, transport.Io).ConfigureAwait(false);
                    if (flush.IsCompleted || flush.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex) when (_connectionCts.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Expected on shutdown; complete the writer without faulting the reader.
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                await CompleteInputWriterAsync(error).ConfigureAwait(false);
            }
        }

        private void RecordReceivedBytes(int bytes)
        {
            if (bytes > 0 && _feedDiagnostics is { IsEnabled: true })
            {
                _feedDiagnostics.RecordTcpBytes(bytes);
            }
        }

        private async Task SendAsync()
        {
            var transport = _transport ?? throw new InvalidOperationException("Transport missing.");
            var reader = _outputPipe.Reader;
            var token = _connectionCts.Token;
            var socket = _socket;
            var io = transport.Io;
            io?.RecordSendLoopStart();
            var iterationEndTs = 0L;
            TxWriteCoalescer? coalescer = null;

            while (!token.IsCancellationRequested)
            {
                SignalOutboundIdle();
                var readIssuedTs = 0L;
                if (io is not null)
                {
                    readIssuedTs = Stopwatch.GetTimestamp();
                    if (iterationEndTs != 0)
                    {
                        io.RecordAdvanceToNextRead(iterationEndTs, readIssuedTs);
                    }
                }

                var result = await ReadOutputAsync(reader, token, io).ConfigureAwait(false);
                var readDoneTs = io is not null ? Stopwatch.GetTimestamp() : 0L;
                SignalOutboundBusy();
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var firstWriteTs = 0L;
                var lastWriteEndTs = 0L;
                try
                {
                    if (buffer.IsEmpty && result.IsCompleted)
                    {
                        if (coalescer is { PendingCount: > 0 })
                        {
                            lastWriteEndTs = await WriteCoalescedPendingAsync(
                                transport,
                                coalescer,
                                io,
                                readDoneTs,
                                firstWriteTs,
                                lastWriteEndTs,
                                token).ConfigureAwait(false);
                        }

                        break;
                    }

                    if (TxWriteGranularityExperiment.TryResolveTarget(transport, out var targetBytes))
                    {
                        if (io is not null)
                        {
                            io.WriteGranularityTargetBytes = targetBytes;
                        }

                        if (coalescer is null || coalescer.TargetBytes != targetBytes)
                        {
                            if (coalescer is { PendingCount: > 0 })
                            {
                                lastWriteEndTs = await WriteCoalescedPendingAsync(
                                    transport,
                                    coalescer,
                                    io,
                                    readDoneTs,
                                    firstWriteTs,
                                    lastWriteEndTs,
                                    token).ConfigureAwait(false);
                                firstWriteTs = lastWriteEndTs;
                            }

                            coalescer = new TxWriteCoalescer(targetBytes);
                        }

                        lastWriteEndTs = await CopyAndWriteAggregatesAsync(
                            transport,
                            coalescer,
                            buffer,
                            io,
                            readDoneTs,
                            firstWriteTs,
                            lastWriteEndTs,
                            token).ConfigureAwait(false);
                    }
                    else
                    {
                        if (coalescer is { PendingCount: > 0 })
                        {
                            lastWriteEndTs = await WriteCoalescedPendingAsync(
                                transport,
                                coalescer,
                                io,
                                readDoneTs,
                                firstWriteTs,
                                lastWriteEndTs,
                                token).ConfigureAwait(false);
                            coalescer.Clear();
                            coalescer = null;
                        }

                        foreach (var segment in buffer)
                        {
                            if (segment.Length == 0)
                            {
                                continue;
                            }

                            var writeStartTs = io is not null ? Stopwatch.GetTimestamp() : 0L;
                            if (io is not null)
                            {
                                if (firstWriteTs == 0)
                                {
                                    firstWriteTs = writeStartTs;
                                    io.RecordReadToFirstWrite(readDoneTs, firstWriteTs);
                                }
                                else
                                {
                                    io.RecordBetweenWrites(lastWriteEndTs, writeStartTs);
                                }
                            }

                            await transport.WriteAsync(segment, token).ConfigureAwait(false);
                            if (io is not null)
                            {
                                lastWriteEndTs = Stopwatch.GetTimestamp();
                                io.RecordTransportWrite(writeStartTs, lastWriteEndTs);
                            }
                        }

                        // Required for DEFLATE: sync-flush compressed bytes to the peer without ending the stream.
                        // No-op / cheap for NetworkStream and SslStream over NetworkStream.
                        await transport.FlushAsync(token).ConfigureAwait(false);
                    }

                    consumed = buffer.End;
                }
                finally
                {
                    var advanceStartTs = io is not null ? Stopwatch.GetTimestamp() : 0L;
                    if (io is not null && lastWriteEndTs != 0)
                    {
                        io.RecordLastWriteToAdvance(lastWriteEndTs, advanceStartTs);
                    }

                    reader.AdvanceTo(consumed);
                    if (io is not null)
                    {
                        var advanceEndTs = Stopwatch.GetTimestamp();
                        io.RecordAdvanceTo(advanceStartTs, advanceEndTs);
                        iterationEndTs = advanceEndTs;
                    }
                }

                if (coalescer is not null && !token.IsCancellationRequested)
                {
                    var drained = await DrainImmediateReadsAsync(
                        reader,
                        transport,
                        coalescer,
                        io,
                        readDoneTs,
                        firstWriteTs,
                        lastWriteEndTs,
                        token).ConfigureAwait(false);
                    lastWriteEndTs = drained.LastWriteEndTs;

                    if (coalescer.PendingCount > 0)
                    {
                        _ = await WriteCoalescedPendingAsync(
                            transport,
                            coalescer,
                            io,
                            readDoneTs,
                            firstWriteTs,
                            lastWriteEndTs,
                            token).ConfigureAwait(false);
                    }

                    if (drained.Completed)
                    {
                        break;
                    }
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }

            io?.RecordSendLoopEnd();
            await reader.CompleteAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _outputReaderCompleted, 1);

            // Half-close TCP send only for plaintext end-of-stream; TLS shutdown is via SslStream dispose.
            if (socket is not null && !IsTls)
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        private static async ValueTask<(long LastWriteEndTs, bool Completed)> DrainImmediateReadsAsync(
            PipeReader reader,
            ConnectionByteTransport transport,
            TxWriteCoalescer coalescer,
            TransportIoSession? io,
            long readDoneTs,
            long firstWriteTs,
            long lastWriteEndTs,
            CancellationToken token)
        {
            var completed = false;
            while (reader.TryRead(out var extra))
            {
                var extraConsumed = extra.Buffer.Start;
                try
                {
                    if (extra.Buffer.IsEmpty)
                    {
                        completed = extra.IsCompleted;
                        break;
                    }

                    io?.RecordTxReadShape(extra.Buffer);
                    lastWriteEndTs = await CopyAndWriteAggregatesAsync(
                        transport,
                        coalescer,
                        extra.Buffer,
                        io,
                        readDoneTs,
                        firstWriteTs,
                        lastWriteEndTs,
                        token).ConfigureAwait(false);
                    extraConsumed = extra.Buffer.End;
                    completed = extra.IsCompleted;
                }
                finally
                {
                    reader.AdvanceTo(extraConsumed);
                }

                if (completed)
                {
                    break;
                }
            }

            return (lastWriteEndTs, completed);
        }

        private static async ValueTask<long> CopyAndWriteAggregatesAsync(
            ConnectionByteTransport transport,
            TxWriteCoalescer coalescer,
            ReadOnlySequence<byte> buffer,
            TransportIoSession? io,
            long readDoneTs,
            long firstWriteTs,
            long lastWriteEndTs,
            CancellationToken token)
        {
            foreach (var segment in buffer)
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                var offset = 0;
                while (offset < segment.Length)
                {
                    if (coalescer.RemainingCapacity == 0)
                    {
                        lastWriteEndTs = await WriteCoalescedPendingAsync(
                            transport,
                            coalescer,
                            io,
                            readDoneTs,
                            firstWriteTs,
                            lastWriteEndTs,
                            token).ConfigureAwait(false);
                        firstWriteTs = firstWriteTs == 0 ? lastWriteEndTs : firstWriteTs;
                    }

                    offset += coalescer.Copy(segment.Span[offset..], io);
                }
            }

            if (coalescer.IsFull)
            {
                lastWriteEndTs = await WriteCoalescedPendingAsync(
                    transport,
                    coalescer,
                    io,
                    readDoneTs,
                    firstWriteTs,
                    lastWriteEndTs,
                    token).ConfigureAwait(false);
            }

            return lastWriteEndTs;
        }

        private static async ValueTask<long> WriteCoalescedPendingAsync(
            ConnectionByteTransport transport,
            TxWriteCoalescer coalescer,
            TransportIoSession? io,
            long readDoneTs,
            long firstWriteTs,
            long lastWriteEndTs,
            CancellationToken token)
        {
            if (coalescer.PendingCount == 0)
            {
                return lastWriteEndTs;
            }

            var writeStartTs = io is not null ? Stopwatch.GetTimestamp() : 0L;
            if (io is not null)
            {
                if (firstWriteTs == 0 && lastWriteEndTs == 0)
                {
                    io.RecordReadToFirstWrite(readDoneTs, writeStartTs);
                }
                else
                {
                    io.RecordBetweenWrites(lastWriteEndTs, writeStartTs);
                }
            }

            await transport.WriteAsync(coalescer.PendingMemory, token).ConfigureAwait(false);
            var writeEndTs = io is not null ? Stopwatch.GetTimestamp() : 0L;
            if (io is not null)
            {
                io.RecordTransportWrite(writeStartTs, writeEndTs);
            }

            await transport.FlushAsync(token).ConfigureAwait(false);
            coalescer.Clear();
            return writeEndTs;
        }

        private async Task CloseTransportSocketAsync()
        {
            ConnectionByteTransport? transport;
            Socket? socket;
            lock (_completeGate)
            {
                transport = _transport;
                _transport = null;
                socket = _socket;
                _socket = null;
            }

            if (transport is not null)
            {
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }
            }

            if (socket is not null)
            {
                try
                {
                    socket.Dispose();
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }
            }
        }

        private static void ConfigureAcceptedSocket(Socket socket)
        {
            try
            {
                socket.NoDelay = true;
            }
            catch (SocketException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private static EndPoint? TryGetRemote(Socket socket)
        {
            try
            {
                return socket.RemoteEndPoint;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        private static EndPoint? TryGetLocal(Socket socket)
        {
            try
            {
                return socket.LocalEndPoint;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }
}
