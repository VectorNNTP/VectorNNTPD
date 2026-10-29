using System.Collections.Concurrent;
using System.Net.Security;

namespace VectorNNTPD.TransitService.Transport.Vatp
{
    /// <summary>Pools multiplexed VATP connections keyed by cache endpoint.</summary>
    internal sealed class VatpConnectionPool : IAsyncDisposable
    {
        private readonly ILogger<VatpConnectionPool> _logger;
        private readonly VatpClientOptions _options;
        private readonly ConcurrentDictionary<EndpointKey, EndpointPool> _endpoints = new();
        private int _disposed;

        /// <summary>Optional certificate callback for tests only.</summary>
        internal RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <summary>Optional TCP connect host override for tests (TLS TargetHost remains the URI host).</summary>
        internal string? TestTcpConnectHost { get; set; }

        public VatpConnectionPool(ILogger<VatpConnectionPool> logger)
            : this(logger, new VatpClientOptions())
        {
        }

        internal VatpConnectionPool(ILogger<VatpConnectionPool> logger, VatpClientOptions options)
        {
            _logger = logger;
            _options = options;
        }

        internal async Task<(VatpConnection? Connection, VatpFetchResult? Failure)> AcquireAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var key = new EndpointKey(host, port);
            var pool = _endpoints.GetOrAdd(key, static _ => new EndpointPool());

            var existing = pool.TryAcquireExisting();
            if (existing is not null)
            {
                return (existing, null);
            }

            if (pool.ConnectionCount >= _options.MaxConnectionsPerEndpoint)
            {
                return pool.ConnectionCount == 0
                    ? (null, VatpFetchResult.ConnectionFailure("No VATP connections available for endpoint."))
                    : (null, VatpFetchResult.ConnectionFailure(
                        "All VATP connections to the endpoint are at stream capacity."));
            }

            await pool.CreateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = pool.TryAcquireExisting();
                if (existing is not null)
                {
                    return (existing, null);
                }

                if (pool.ConnectionCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return (
                        null,
                        VatpFetchResult.ConnectionFailure(
                            "All VATP connections to the endpoint are at stream capacity."));
                }

                try
                {
                    var created = await VatpConnection.ConnectAsync(
                        host,
                        port,
                        _logger,
                        _options,
                        ServerCertificateValidationCallback,
                        cancellationToken,
                        TestTcpConnectHost).ConfigureAwait(false);
                    pool.Add(created);
                    return (created, null);
                }
                catch (VatpConnectionException ex)
                {
                    return (null, VatpFetchResult.ConnectionFailure(ex.Message));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    return (null, VatpFetchResult.ConnectionFailure(ex.Message));
                }
            }
            finally
            {
                pool.CreateGate.Release();
            }
        }

        internal void Release(VatpConnection connection)
        {
            if (connection.IsDead)
            {
                RemoveDead(connection);
            }
        }

        internal void RemoveDead(VatpConnection connection)
        {
            var key = new EndpointKey(connection.Host, connection.Port);
            if (_endpoints.TryGetValue(key, out var pool))
            {
                pool.Remove(connection);
            }

            _ = connection.DisposeAsync();
        }

        internal int GetEndpointConnectionCount(string host, int port)
        {
            var key = new EndpointKey(host, port);
            return _endpoints.TryGetValue(key, out var pool) ? pool.ConnectionCount : 0;
        }

        internal int GetEndpointAliveConnectionCount(string host, int port)
        {
            var key = new EndpointKey(host, port);
            return _endpoints.TryGetValue(key, out var pool) ? pool.AliveConnectionCount : 0;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            foreach (var pool in _endpoints.Values)
            {
                await pool.DisposeAsync().ConfigureAwait(false);
            }

            _endpoints.Clear();
        }

        private readonly record struct EndpointKey(string Host, int Port);

        private sealed class EndpointPool
        {
            private readonly List<VatpConnection> _connections = [];
            private readonly object _gate = new();

            internal SemaphoreSlim CreateGate { get; } = new(1, 1);

            public int ConnectionCount
            {
                get
                {
                    lock (_gate)
                    {
                        return _connections.Count;
                    }
                }
            }

            public int AliveConnectionCount
            {
                get
                {
                    lock (_gate)
                    {
                        PurgeDeadLocked();
                        return _connections.Count;
                    }
                }
            }

            public void Add(VatpConnection connection)
            {
                lock (_gate)
                {
                    _connections.Add(connection);
                }
            }

            public void Remove(VatpConnection connection)
            {
                lock (_gate)
                {
                    _connections.Remove(connection);
                }
            }

            public VatpConnection? TryAcquireExisting()
            {
                lock (_gate)
                {
                    PurgeDeadLocked();
                    foreach (var candidate in _connections)
                    {
                        if (candidate.HasStreamCapacity)
                        {
                            return candidate;
                        }
                    }
                }

                return null;
            }

            public async ValueTask DisposeAsync()
            {
                VatpConnection[] snapshot;
                lock (_gate)
                {
                    snapshot = _connections.ToArray();
                    _connections.Clear();
                }

                CreateGate.Dispose();
                foreach (var connection in snapshot)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            private void PurgeDeadLocked()
            {
                for (var i = _connections.Count - 1; i >= 0; i--)
                {
                    if (_connections[i].IsDead)
                    {
                        var dead = _connections[i];
                        _connections.RemoveAt(i);
                        _ = dead.DisposeAsync();
                    }
                }
            }
        }
    }
}
