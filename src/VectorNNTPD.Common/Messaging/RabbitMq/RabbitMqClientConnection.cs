using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Owns one RabbitMQ.Client <see cref="IConnection"/>.</summary>
    internal sealed class RabbitMqClientConnection : IRabbitMqConnection
    {
        /// <summary>Owned RabbitMQ.Client connection. Closed, then disposed, by <see cref="DisposeAsync"/>.</summary>
        private readonly IConnection _connection;

        /// <summary>Logger for a failed <c>CloseAsync</c>. Credentials are not written.</summary>
        private readonly ILogger _logger;

        /// <summary>Stored shutdown delegate so <see cref="DisposeAsync"/> can unsubscribe the same instance.</summary>
        private readonly AsyncEventHandler<ShutdownEventArgs> _shutdownHandler;

        /// <summary>Stored callback-exception delegate so dispose can unsubscribe the same instance.</summary>
        private readonly AsyncEventHandler<CallbackExceptionEventArgs> _callbackHandler;

        /// <summary>Stored connection-blocked delegate so dispose can unsubscribe the same instance.</summary>
        private readonly AsyncEventHandler<ConnectionBlockedEventArgs> _blockedHandler;

        /// <summary>Stored connection-unblocked delegate so dispose can unsubscribe the same instance.</summary>
        private readonly AsyncEventHandler<AsyncEventArgs> _unblockedHandler;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>. Later calls return immediately.</summary>
        private int _disposed;

        /// <summary>Initializes a new wrapper around an opened broker connection.</summary>
        internal RabbitMqClientConnection(IConnection connection, string virtualHost, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);
            ArgumentNullException.ThrowIfNull(logger);

            _connection = connection;
            _logger = logger;
            VirtualHost = virtualHost;
            ClientProvidedName = !string.IsNullOrWhiteSpace(connection.ClientProvidedName)
                ? connection.ClientProvidedName
                : throw new InvalidOperationException(
                    "RabbitMQ connection invariant violated: IConnection.ClientProvidedName must be non-null and non-whitespace.");

            _shutdownHandler = OnShutdownAsync;
            _callbackHandler = OnCallbackExceptionAsync;
            _blockedHandler = OnBlockedAsync;
            _unblockedHandler = OnUnblockedAsync;

            _connection.ConnectionShutdownAsync += _shutdownHandler;
            _connection.CallbackExceptionAsync += _callbackHandler;
            _connection.ConnectionBlockedAsync += _blockedHandler;
            _connection.ConnectionUnblockedAsync += _unblockedHandler;
        }

        /// <inheritdoc />
        public bool IsOpen => _connection.IsOpen;

        /// <inheritdoc />
        public string Host => _connection.Endpoint.HostName;

        /// <inheritdoc />
        public int Port => _connection.Endpoint.Port;

        /// <inheritdoc />
        public string VirtualHost { get; }

        /// <inheritdoc />
        public string ClientProvidedName { get; }

        /// <inheritdoc />
        public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

        /// <inheritdoc />
        public event EventHandler<RabbitMqConnectionBlockedEventArgs>? ConnectionBlocked;

        /// <inheritdoc />
        public event EventHandler? ConnectionUnblocked;

        /// <inheritdoc />
        public async Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for topology declaration.");
            }

            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false);
            var channel = await _connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RabbitMqClientTopologyChannel(channel);
        }

        /// <inheritdoc />
        public async Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for article-work RPC.");
            }

            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            var channel = await _connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RabbitMqClientRpcChannel(channel, generation);
        }

        /// <inheritdoc />
        public async Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for channel creation.");
            }

            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false);
            var channel = await _connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RabbitMqClientManualAckChannel(channel, generation);
        }

        /// <inheritdoc />
        public async Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for publication.");
            }

            // Explicit limiter: the two-arg CreateChannelOptions leaves OutstandingPublisherConfirmationsRateLimiter
            // null (unlimited). Align with RabbitMqPublisherConfirmLimits / Article Work response publisher gate.
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(
                    RabbitMqPublisherConfirmLimits.MaxOutstandingConfirmations));
            var channel = await _connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RabbitMqClientPublishChannel(channel, generation);
        }

        /// <inheritdoc />
        public async Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not open for publication.");
            }

            // Tracking disabled: BasicPublishAsync returns after the write; confirms arrive
            // via BasicAcksAsync / BasicNacksAsync / BasicReturnAsync.
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: false);
            var channel = await _connection
                .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RabbitMqClientAsyncConfirmPublishChannel(channel, generation);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _connection.ConnectionShutdownAsync -= _shutdownHandler;
            _connection.CallbackExceptionAsync -= _callbackHandler;
            _connection.ConnectionBlockedAsync -= _blockedHandler;
            _connection.ConnectionUnblockedAsync -= _unblockedHandler;

            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                RabbitMqLogMessages.ConnectionCloseFailed(_logger, ex);
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Raises <see cref="ConnectionLost"/> with the broker reply code, text, and initiator. Does not dispose the connection.
        /// </summary>
        /// <param name="sender">Unused. The event is raised on this wrapper.</param>
        /// <param name="eventArgs">Shutdown details copied into <see cref="RabbitMqConnectionLostEventArgs"/>.</param>
        /// <returns>A completed task. Recovery is requested by <see cref="RabbitMqService"/>, not here.</returns>
        private Task OnShutdownAsync(object sender, ShutdownEventArgs eventArgs)
        {
            ConnectionLost?.Invoke(
                this,
                new RabbitMqConnectionLostEventArgs(
                    eventArgs.ReplyCode,
                    eventArgs.ReplyText,
                    eventArgs.Initiator.ToString()));
            return Task.CompletedTask;
        }

        /// <summary>Logs the callback exception message. Does not raise <see cref="ConnectionLost"/> or dispose the connection.</summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Callback failure. Only <see cref="Exception.Message"/> is logged.</param>
        /// <returns>A completed task.</returns>
        private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs eventArgs)
        {
            RabbitMqLogMessages.CallbackException(_logger, eventArgs.Exception.Message);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Logs the broker block reason and raises <see cref="ConnectionBlocked"/>.
        /// Does not pause channels or request recovery; <see cref="RabbitMqService"/> owns the timeout.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Broker block reason.</param>
        /// <returns>A completed task.</returns>
        private Task OnBlockedAsync(object sender, ConnectionBlockedEventArgs eventArgs)
        {
            var reason = eventArgs.Reason ?? string.Empty;
            RabbitMqLogMessages.ConnectionBlocked(_logger, reason);
            ConnectionBlocked?.Invoke(this, new RabbitMqConnectionBlockedEventArgs(reason));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Logs that the broker unblocked the connection and raises <see cref="ConnectionUnblocked"/>.
        /// <paramref name="eventArgs"/> is unused.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Unused client event args.</param>
        /// <returns>A completed task.</returns>
        private Task OnUnblockedAsync(object sender, AsyncEventArgs eventArgs)
        {
            RabbitMqLogMessages.ConnectionUnblocked(_logger);
            ConnectionUnblocked?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
