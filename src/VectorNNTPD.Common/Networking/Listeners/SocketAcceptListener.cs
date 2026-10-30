using System.Net;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.Common.Networking.Listeners
{
    using VectorNNTPD.Common.Core;
    /// <summary>
    /// Owns one listening <see cref="Socket"/> and runs an allocation-efficient asynchronous accept loop.
    /// </summary>
    /// <remarks>
    /// A single failed accepted connection must not stop this listener. The listen socket remains open
    /// until <see cref="DisposeAsync"/> / stop.
    /// </remarks>
    public sealed class SocketAcceptListener : IAsyncDisposable
    {
        private readonly ListenBinding _binding;
        private readonly Func<Socket, CancellationToken, ValueTask> _onAccepted;
        private readonly ILogger _logger;
        private readonly IListenSocketBinder _binder;
        private readonly Socket _listenSocket;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _maxConsecutiveAcceptFailures;
        private readonly TimeSpan _acceptFailureRetryDelay;
        private readonly TimeSpan _acceptFailureLogInterval;
        private readonly Func<TimeSpan, CancellationToken, Task>? _acceptFailureDelayAsync;
        private readonly IListenAcceptFailureObserver? _acceptFailureObserver;
        private Task? _acceptLoop;
        private int _stopped;
        private int _disposed;

        /// <summary>
        /// Optional accept replacement for tests. When null, production uses
        /// <see cref="Socket.AcceptAsync(CancellationToken)"/>.
        /// </summary>
        public Func<Socket, CancellationToken, ValueTask<Socket>>? AcceptAsyncOverride { get; set; }

        /// <summary>Initializes a new instance of the <see cref="SocketAcceptListener"/> class.</summary>
        /// <param name="binding">Planned local bind.</param>
        /// <param name="onAccepted">Per-connection handler. Failures are logged; the listen loop continues.</param>
        /// <param name="logger">Listener logger.</param>
        /// <param name="listenBinder">Bind/listen ownership seam. Null uses the production binder.</param>
        /// <param name="maxConsecutiveAcceptFailures">
        /// When greater than zero, the accept loop faults after this many consecutive
        /// <see cref="SocketException"/> failures. Zero keeps the historical infinite-retry behaviour.
        /// </param>
        /// <param name="acceptFailureRetryDelay">Delay between consecutive accept failures when faulting is enabled.</param>
        /// <param name="acceptFailureDelayAsync">Optional delay override for tests.</param>
        /// <param name="acceptFailureObserver">
        /// Optional host-specific accept-failure telemetry (rate-limited warnings, recovery, stop).
        /// When null, failures still emit shared <see cref="NetworkingLogMessages"/> events.
        /// </param>
        /// <param name="acceptFailureLogInterval">
        /// Minimum interval between observer failure logs for intermediate consecutive counts.
        /// </param>
        /// <param name="reuseAddress">
        /// When <see langword="true"/> (Reader/Transit default), sets <c>SO_REUSEADDR</c>.
        /// When <see langword="false"/> (Storage/BackFiller), keeps exclusive bind semantics.
        /// </param>
        public SocketAcceptListener(
            ListenBinding binding,
            Func<Socket, CancellationToken, ValueTask> onAccepted,
            ILogger logger,
            IListenSocketBinder? listenBinder = null,
            int maxConsecutiveAcceptFailures = 0,
            TimeSpan? acceptFailureRetryDelay = null,
            Func<TimeSpan, CancellationToken, Task>? acceptFailureDelayAsync = null,
            IListenAcceptFailureObserver? acceptFailureObserver = null,
            TimeSpan? acceptFailureLogInterval = null,
            bool reuseAddress = true)
        {
            ArgumentNullException.ThrowIfNull(onAccepted);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentOutOfRangeException.ThrowIfNegative(maxConsecutiveAcceptFailures);
            _binding = binding;
            _onAccepted = onAccepted;
            _logger = logger;
            _binder = listenBinder ?? SocketListenBinder.Instance;
            _maxConsecutiveAcceptFailures = maxConsecutiveAcceptFailures;
            _acceptFailureRetryDelay = acceptFailureRetryDelay ?? TimeSpan.FromMilliseconds(250);
            _acceptFailureDelayAsync = acceptFailureDelayAsync;
            _acceptFailureObserver = acceptFailureObserver;
            _acceptFailureLogInterval = acceptFailureLogInterval ?? TimeSpan.FromSeconds(5);

            var family = binding.Address.AddressFamily;
            _listenSocket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            if (family == AddressFamily.InterNetworkV6)
            {
                _listenSocket.DualMode = binding.DualMode;
            }

            // SO_REUSEADDR is retained for Reader/Transit: Bind success is ownership under these
            // socket options. On Windows it can allow another process to bind the same endpoint;
            // that is not a Bind failure and is not treated as exclusive port ownership.
            // Storage/BackFiller pass reuseAddress: false for exclusive bind semantics.
            if (reuseAddress)
            {
                try
                {
                    _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                catch (SocketException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                    // Best-effort; some platforms differ.
                }
            }
        }

        /// <summary>Gets the local endpoint after <see cref="Start"/> (port may be ephemeral in tests).</summary>
        public IPEndPoint LocalEndPoint =>
            _listenSocket.LocalEndPoint as IPEndPoint ?? _binding.EndPoint;

        /// <summary>Gets the planned binding.</summary>
        public ListenBinding Binding => _binding;

        /// <summary>Gets whether the accept loop is still running (tests).</summary>
        internal bool AcceptLoopActive => _acceptLoop is { IsCompleted: false };

        /// <summary>
        /// Gets the accept-loop task after <see cref="Start"/>, or <see langword="null"/> before start.
        /// Hosts may observe this as <c>IApplicationService.Execution</c> so accept-loop faults surface.
        /// </summary>
        public Task? AcceptLoopTask => _acceptLoop;

        /// <summary>Binds, listens, and starts the accept loop.</summary>
        /// <remarks>
        /// Bind/listen exceptions propagate to the caller and are startup failures.
        /// The accept loop is started only after bind/listen succeed; later accept
        /// failures are logged and the loop continues.
        /// </remarks>
        public void Start(int backlog = 512)
        {
            _binder.BindAndListen(_listenSocket, _binding.EndPoint, backlog);
            _acceptLoop = AcceptLoopAsync(_cts.Token);
            NetworkingLogMessages.ListenerStarted(_logger, LocalEndPoint, _binding.DualMode);
        }

        /// <summary>Stops accepting and closes the listen socket.</summary>
        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Already disposed.
            }

            try
            {
                _listenSocket.Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
                // Best-effort.
            }

            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                    // Expected.
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    NetworkingLogMessages.AcceptLoopStopError(_logger, ex);
                }
            }

            NetworkingLogMessages.ListenerStopped(_logger, _binding.Address, _binding.Port);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            var consecutiveFailures = 0;
            DateTimeOffset? lastFailureLogUtc = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = AcceptAsyncOverride is not null
                        ? await AcceptAsyncOverride(_listenSocket, cancellationToken).ConfigureAwait(false)
                        : await _listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);

                    if (consecutiveFailures > 0)
                    {
                        _acceptFailureObserver?.OnAcceptRecovered(consecutiveFailures);
                        consecutiveFailures = 0;
                        lastFailureLogUtc = null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _stopped != 0)
                {
                    break;
                }
                catch (SocketException ex) when (cancellationToken.IsCancellationRequested || _stopped != 0)
                {
                    NetworkingLogMessages.AcceptInterruptedDuringShutdown(_logger, ex);
                    break;
                }
                catch (SocketException ex) when (_maxConsecutiveAcceptFailures > 0)
                {
                    consecutiveFailures++;
                    var socketError = ex.SocketErrorCode.ToString();
                    var now = DateTimeOffset.UtcNow;
                    if (ShouldLogAcceptFailure(consecutiveFailures, lastFailureLogUtc, now))
                    {
                        if (_acceptFailureObserver is not null)
                        {
                            _acceptFailureObserver.OnAcceptFailed(
                                socketError,
                                consecutiveFailures,
                                _maxConsecutiveAcceptFailures);
                        }
                        else
                        {
                            NetworkingLogMessages.AcceptFailed(_logger, ex, _binding.EndPoint);
                        }

                        lastFailureLogUtc = now;
                    }

                    if (consecutiveFailures >= _maxConsecutiveAcceptFailures)
                    {
                        _acceptFailureObserver?.OnAcceptLoopStopping(consecutiveFailures, socketError);
                        throw new InvalidOperationException(
                            $"Accept failed {consecutiveFailures} consecutive times (last error: {socketError}).",
                            ex);
                    }

                    try
                    {
                        if (_acceptFailureDelayAsync is not null)
                        {
                            await _acceptFailureDelayAsync(_acceptFailureRetryDelay, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(_acceptFailureRetryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (_maxConsecutiveAcceptFailures == 0)
                {
                    NetworkingLogMessages.AcceptFailed(_logger, ex, _binding.EndPoint);
                    continue;
                }

                // With a consecutive-failure policy, unexpected accept faults must surface on AcceptLoopTask.

                _ = HandleAcceptedAsync(accepted, cancellationToken);
            }
        }

        private bool ShouldLogAcceptFailure(
            int consecutiveFailures,
            DateTimeOffset? lastFailureLogUtc,
            DateTimeOffset nowUtc)
        {
            if (consecutiveFailures == 1 || consecutiveFailures >= _maxConsecutiveAcceptFailures)
            {
                return true;
            }

            return lastFailureLogUtc is null
                   || nowUtc - lastFailureLogUtc.Value >= _acceptFailureLogInterval;
        }

        private async Task HandleAcceptedAsync(Socket accepted, CancellationToken cancellationToken)
        {
            try
            {
                await _onAccepted(accepted, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    accepted.Dispose();
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                NetworkingLogMessages.AcceptedHandlerFailed(_logger, ex);
                try
                {
                    accepted.Dispose();
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored at this best-effort boundary
                    // Best-effort.
                }
            }
        }
    }
}
