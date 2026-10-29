using System.Net;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.TransitService.Networking.Listeners
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
        private Task? _acceptLoop;
        private int _stopped;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="SocketAcceptListener"/> class.</summary>
        public SocketAcceptListener(
            ListenBinding binding,
            Func<Socket, CancellationToken, ValueTask> onAccepted,
            ILogger logger,
            IListenSocketBinder? listenBinder = null)
        {
            ArgumentNullException.ThrowIfNull(onAccepted);
            ArgumentNullException.ThrowIfNull(logger);
            _binding = binding;
            _onAccepted = onAccepted;
            _logger = logger;
            _binder = listenBinder ?? SocketListenBinder.Instance;

            var family = binding.Address.AddressFamily;
            _listenSocket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            if (family == AddressFamily.InterNetworkV6)
            {
                _listenSocket.DualMode = binding.DualMode;
            }

            // SO_REUSEADDR is retained: Bind success is ownership under these socket options.
            // On Windows it can allow another process to bind the same endpoint; that is not a
            // Bind failure and is not treated as exclusive port ownership.
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

        /// <summary>Gets the local endpoint after <see cref="Start"/> (port may be ephemeral in tests).</summary>
        public IPEndPoint LocalEndPoint =>
            _listenSocket.LocalEndPoint as IPEndPoint ?? _binding.EndPoint;

        /// <summary>Gets the planned binding.</summary>
        public ListenBinding Binding => _binding;

        /// <summary>Gets whether the accept loop is still running (tests).</summary>
        internal bool AcceptLoopActive => _acceptLoop is { IsCompleted: false };

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
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await _listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
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
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    NetworkingLogMessages.AcceptFailed(_logger, ex, _binding.EndPoint);
                    continue;
                }

                _ = HandleAcceptedAsync(accepted, cancellationToken);
            }
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
