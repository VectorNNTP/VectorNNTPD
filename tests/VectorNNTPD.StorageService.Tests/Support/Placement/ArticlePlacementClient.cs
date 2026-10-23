using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>
    /// Long-lived TAKETHIS client. One TLS connection per placement target.
    /// </summary>
    public sealed class ArticlePlacementClient : IArticlePlacementClient, IAsyncDisposable
    {
        private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(5);

        private readonly ILogger<ArticlePlacementClient> _logger;
        private readonly StorageFeedClientOptions _options;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Dictionary<string, ArticlePlacementConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ArticlePlacementConnection>> _dialing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _nextDial = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TimeSpan> _backoff = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;
        private int _connects;
        private int _dials;

        /// <summary>Optional certificate callback for tests only.</summary>
        internal RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <summary>Optional TCP connect host override for tests. TLS TargetHost remains the advertised FQDN.</summary>
        internal string? TestTcpConnectHost { get; set; }

        /// <summary>Gets how many TCP connections this client has established.</summary>
        internal int ConnectCount => Volatile.Read(ref _connects);

        /// <summary>Gets how many dial attempts have started, including failures.</summary>
        internal int DialAttempts => Volatile.Read(ref _dials);

        /// <summary>Gets the outstanding TAKETHIS limit applied to each connection.</summary>
        internal int MaxOutstandingStores { get; }

        /// <summary>Sums wait and response counters across connections still owned by this client.</summary>
        internal PlacementTransportCounters Snapshot()
        {
            lock (_gate)
            {
                var counters = default(PlacementTransportCounters);
                foreach (var connection in _connections.Values)
                {
                    counters = counters.Add(connection);
                }

                return counters;
            }
        }

        /// <summary>Default outstanding TAKETHIS operations per connection.</summary>
        internal const int DefaultMaxOutstandingStoresPerConnection = 2;

        /// <summary>Initializes a placement client with two outstanding TAKETHIS operations.</summary>
        public ArticlePlacementClient(ILogger<ArticlePlacementClient> logger)
            : this(logger, DefaultMaxOutstandingStoresPerConnection)
        {
        }

        /// <summary>Initializes a placement client with an explicit outstanding-TAKETHIS limit.</summary>
        public ArticlePlacementClient(ILogger<ArticlePlacementClient> logger, int maxOutstandingStores)
            : this(logger, new StorageFeedClientOptions(), maxOutstandingStores, TimeProvider.System)
        {
        }

        internal ArticlePlacementClient(
            ILogger<ArticlePlacementClient> logger,
            StorageFeedClientOptions options,
            int maxOutstandingStores = DefaultMaxOutstandingStoresPerConnection,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(options);
            if (maxOutstandingStores is not (1 or 2))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxOutstandingStores),
                    maxOutstandingStores,
                    "Outstanding TAKETHIS operations per connection must be 1 or 2.");
            }

            _logger = logger;
            _options = options;
            _time = time ?? TimeProvider.System;
            MaxOutstandingStores = maxOutstandingStores;
        }

        /// <inheritdoc />
        public async ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "shutdown");
            }

            if (target.VatpPort is not int port || port is < 1 or > 65535)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "vatp-port");
            }

            if (!ArticlePlacementMessageId.TryCopy(in record, out _))
            {
                return new ArticlePlacementResult(ArticlePlacementKind.RejectedInvalid, "message-id");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leased = await LeaseAsync(target.Fqdn, port, cancellationToken).ConfigureAwait(false);
                if (leased.Failure is { } failure)
                {
                    return failure;
                }

                var connection = leased.Connection
                    ?? throw new InvalidOperationException("Placement lease returned neither a connection nor a failure.");
                var result = await connection.PlaceAsync(record, cancellationToken).ConfigureAwait(false);
                if (connection.IsDead)
                {
                    Forget(target.Fqdn, port, connection);
                    try
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
                    {
                        ExpectedException.Ignore(ex);
                    }
                }

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout");
            }
            catch (OperationCanceledException)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            catch (Exception ex)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, DescribeTransportFailure(ex));
            }
        }

        /// <summary>Closes every placement connection and rejects later calls.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ArticlePlacementConnection[] connections;
            lock (_gate)
            {
                connections = _connections.Values.ToArray();
                _connections.Clear();
            }

            var outstanding = 0;
            foreach (var connection in connections)
            {
                outstanding += connection.ActiveStores;
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            ArticlePlacementTransportLogMessages.Shutdown(_logger, outstanding);
        }

        private async Task<(ArticlePlacementConnection? Connection, ArticlePlacementResult? Failure)> LeaseAsync(
            string fqdn,
            int port,
            CancellationToken cancellationToken)
        {
            var key = fqdn + ":" + port.ToString(CultureInfo.InvariantCulture);
            Task<ArticlePlacementConnection>? pending = null;
            var owner = false;
            ArticlePlacementConnection? dead = null;
            ArticlePlacementResult? deferred = null;
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return (null, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "shutdown"));
                }

                if (_connections.TryGetValue(key, out var live) && !live.IsDead)
                {
                    ArticlePlacementTransportLogMessages.Reused(_logger, fqdn, port, live.ActiveStores);
                    return (live, null);
                }

                if (_connections.TryGetValue(key, out dead))
                {
                    _connections.Remove(key);
                }

                if (_dialing.TryGetValue(key, out pending))
                {
                }
                else if (_nextDial.TryGetValue(key, out var next) && _time.GetUtcNow() < next)
                {
                    var wait = next - _time.GetUtcNow();
                    ArticlePlacementTransportLogMessages.ReconnectDeferred(
                        _logger, fqdn, port, (long)wait.TotalMilliseconds, "backoff");
                    deferred = new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "backoff");
                }
                else
                {
                    pending = DialAsync(fqdn, port, key);
                    _dialing[key] = pending;
                    owner = true;
                }
            }

            if (dead is not null)
            {
                try
                {
                    await dead.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
                {
                    ExpectedException.Ignore(ex);
                }
            }

            if (deferred is not null)
            {
                return (null, deferred);
            }

            try
            {
                var connection = await pending!.WaitAsync(cancellationToken).ConfigureAwait(false);
                return (connection, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, new ArticlePlacementResult(ArticlePlacementKind.Cancelled));
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException)
            {
                if (owner)
                {
                    NoteFailure(key);
                }

                var failure = DescribeTransportFailure(ex);
                ArticlePlacementTransportLogMessages.ConnectionFailed(_logger, fqdn, port, failure);
                return (null, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, failure));
            }
            finally
            {
                if (owner)
                {
                    _ = ReleaseDialAsync(key, pending!);
                }
            }
        }

        private async Task ReleaseDialAsync(string key, Task<ArticlePlacementConnection> pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExpectedException.Ignore(ex);
            }

            lock (_gate)
            {
                if (_dialing.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    _dialing.Remove(key);
                }
            }
        }

        private async Task<ArticlePlacementConnection> DialAsync(string fqdn, int port, string key)
        {
            Interlocked.Increment(ref _dials);
            var connection = await ArticlePlacementConnection.EstablishAsync(
                    fqdn,
                    port,
                    TestTcpConnectHost ?? fqdn,
                    _logger,
                    _options,
                    MaxOutstandingStores,
                    ServerCertificateValidationCallback,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _connects);
            lock (_gate)
            {
                _backoff[key] = InitialBackoff;
                _nextDial.Remove(key);
                if (_disposed != 0)
                {
                    _ = connection.DisposeAsync();
                    throw new IOException("shutdown");
                }

                _connections[key] = connection;
            }

            return connection;
        }

        private void NoteFailure(string key)
        {
            lock (_gate)
            {
                var current = _backoff.TryGetValue(key, out var backoff) ? backoff : InitialBackoff;
                _nextDial[key] = _time.GetUtcNow().Add(current);
                var doubled = TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaximumBackoff.Ticks));
                _backoff[key] = doubled;
            }
        }

        private void Forget(string fqdn, int port, ArticlePlacementConnection connection)
        {
            var key = fqdn + ":" + port.ToString(CultureInfo.InvariantCulture);
            lock (_gate)
            {
                if (_connections.TryGetValue(key, out var current) && ReferenceEquals(current, connection))
                {
                    _connections.Remove(key);
                }
            }
        }

        private static string DescribeTransportFailure(Exception ex)
        {
            if (ex is TimeoutException)
            {
                return "timeout";
            }

            if (ex is SocketException socket)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"SocketException: SocketErrorCode={socket.SocketErrorCode} HResult={socket.HResult} Message={socket.Message}");
            }

            return ex.GetType().Name;
        }
    }

    /// <summary>Wait and response totals for connections still held by one placement client.</summary>
    internal readonly record struct PlacementTransportCounters(
        long SlotWaitTicks,
        long CreditWaitTicks,
        long ResultCount,
        long ResultTicks,
        long ResultMaxTicks,
        int PeakActive,
        long DataFramesWritten)
    {
        /// <summary>Adds one connection's counters.</summary>
        internal PlacementTransportCounters Add(ArticlePlacementConnection connection) =>
            new(
                SlotWaitTicks + connection.SlotWaitTicks,
                CreditWaitTicks + connection.CreditWaitTicks,
                ResultCount + connection.ResultCount,
                ResultTicks + connection.ResultTicks,
                Math.Max(ResultMaxTicks, connection.ResultMaxTicks),
                Math.Max(PeakActive, connection.PeakActive),
                DataFramesWritten + connection.DataFramesWritten);
    }
}
