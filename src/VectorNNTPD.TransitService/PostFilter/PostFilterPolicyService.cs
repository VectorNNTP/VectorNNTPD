using VectorNNTPD.Common.Core;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>
    /// Loads PostFilter policy from NntpDB, publishes an immutable snapshot,
    /// and refreshes it every 60 seconds.
    /// </summary>
    /// <remarks>
    /// Initial load failure prevents <c>RUNNING</c>. Refresh failure retains last-known-good.
    /// A successful refresh of the same published revision does not replace the snapshot.
    /// POST observes <see cref="Current"/> via a volatile snapshot read and never queries MySQL.
    /// There is no appsettings fallback.
    /// </remarks>
    internal sealed class PostFilterPolicyService : IPostFilterPolicySource, IApplicationService, IAsyncDisposable
    {
        /// <summary>Refresh interval after a successful initial load.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

        private readonly IPostFilterPolicyRepository _repository;
        private readonly ILogger<PostFilterPolicyService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private PostFilterPolicySnapshot? _current;
        private Task? _execution;
        private int _started;
        private int _refreshing;
        private int _disposed;

        /// <summary>Initializes a production policy service.</summary>
        public PostFilterPolicyService(
            IPostFilterPolicyRepository repository,
            ILogger<PostFilterPolicyService> logger)
            : this(repository, logger, TimeProvider.System, RefreshInterval)
        {
        }

        /// <summary>Initializes a service with an explicit clock and interval (tests).</summary>
        internal PostFilterPolicyService(
            IPostFilterPolicyRepository repository,
            ILogger<PostFilterPolicyService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _repository = repository;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "PostFilterPolicy";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public PostFilterPolicySnapshot Current
        {
            get
            {
                var snapshot = Volatile.Read(ref _current);
                if (snapshot is null)
                {
                    throw new InvalidOperationException("PostFilter policy has not been published.");
                }

                return snapshot;
            }
        }

        /// <summary>Gets whether a snapshot has been published (tests).</summary>
        internal bool HasSnapshot => Volatile.Read(ref _current) is not null;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            try
            {
                var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot, previousRevision: null);
                _execution = RunAsync(_runCts.Token);
                await Task.CompletedTask.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    PostFilterLogMessages.PolicyInitialLoadFailed(_logger, ex);
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is not null)
            {
                try
                {
                    await _execution.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _runCts.CancelAsync().ConfigureAwait(false);
            _runCts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private async Task TryRefreshAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var previous = Volatile.Read(ref _current);
                var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (previous is not null && previous.Revision == snapshot.Revision)
                {
                    return;
                }

                Publish(snapshot, previous?.Revision);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                PostFilterLogMessages.PolicyRefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        private async Task<PostFilterPolicySnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
        {
            var record = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            return PostFilterPolicyCompiler.Compile(record.Options, record.Revision);
        }

        private void Publish(PostFilterPolicySnapshot snapshot, long? previousRevision)
        {
            Volatile.Write(ref _current, snapshot);
            PostFilterLogMessages.PolicyPublished(
                _logger,
                snapshot.Revision,
                snapshot.Gate.ToString(),
                snapshot.SpamAssassinEnabled,
                snapshot.SpamAssassinMaxArticleSize,
                snapshot.SpamAssassinExcludeArtTypes.ToString(),
                snapshot.SpamAssassinHosts.Count,
                snapshot.SpamAssassinMaxConnections);
            if (previousRevision is { } prior && prior != snapshot.Revision)
            {
                PostFilterLogMessages.PolicyRevisionChanged(_logger, prior, snapshot.Revision);
            }
        }
    }
}
