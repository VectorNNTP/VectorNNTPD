using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Storage.SegmentPools;

namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Creates the fixed shard tree under every validated spool. Observational storage is unchanged:
    /// this service does not write article files and does not make an article durable.
    /// </summary>
    internal sealed class SpoolShardInitializationService : IApplicationService
    {
        private readonly SegmentPoolCatalog _catalog;
        private readonly ISpoolShardDirectorySystem _directories;
        private readonly ILogger<SpoolShardInitializationService> _logger;
        private readonly TimeProvider _time;
        private readonly int _concurrency;

        /// <summary>Initializes the service with the production concurrency limit and clock.</summary>
        /// <param name="catalog">Canonical spool paths published by validation.</param>
        /// <param name="directories">Directory creator.</param>
        /// <param name="logger">Per-spool and summary logger.</param>
        public SpoolShardInitializationService(
            SegmentPoolCatalog catalog,
            ISpoolShardDirectorySystem directories,
            ILogger<SpoolShardInitializationService> logger)
            : this(catalog, directories, logger, TimeProvider.System, SpoolShardLayout.InitializationConcurrency)
        {
        }

        /// <summary>Initializes the service with an explicit clock and concurrency limit.</summary>
        /// <param name="catalog">Canonical spool paths published by validation.</param>
        /// <param name="directories">Directory creator.</param>
        /// <param name="logger">Per-spool and summary logger.</param>
        /// <param name="time">Clock used for elapsed time.</param>
        /// <param name="concurrency">Maximum directory creates in flight across all spools. At least 1.</param>
        internal SpoolShardInitializationService(
            SegmentPoolCatalog catalog,
            ISpoolShardDirectorySystem directories,
            ILogger<SpoolShardInitializationService> logger,
            TimeProvider time,
            int concurrency)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(directories);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);
            if (concurrency < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(concurrency));
            }

            _catalog = catalog;
            _directories = directories;
            _logger = logger;
            _time = time;
            _concurrency = concurrency;
        }

        /// <inheritdoc />
        public string Name => "SpoolShardInitialization";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <summary>The last completed attempt. Null until <see cref="StartAsync"/> finishes without cancellation.</summary>
        internal SpoolShardInitializationResult? LastResult { get; private set; }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var spools = UniqueSpools();
            if (spools.Count == 0)
            {
                throw new InvalidOperationException(
                    "Spool shard initialization requires at least one validated spool directory.");
            }

            var started = _time.GetTimestamp();
            using var gate = new SemaphoreSlim(_concurrency);
            var workersPerSpool = Math.Max(1, _concurrency / spools.Count);
            var tasks = new Task<SpoolShardSpoolResult>[spools.Count];
            for (var i = 0; i < spools.Count; i++)
            {
                var spool = spools[i];
                tasks[i] = InitializeSpoolAsync(spool, gate, workersPerSpool, cancellationToken);
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var created = 0L;
            var present = 0L;
            var failures = new List<string>();
            foreach (var result in results)
            {
                created += result.Created;
                present += result.AlreadyPresent;
                if (result.Failure is not null)
                {
                    failures.Add(result.SpoolPath + ": " + result.Failure);
                }
            }

            var elapsed = _time.GetElapsedTime(started);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "Spool shard initialization failed." + Environment.NewLine + string.Join(Environment.NewLine, failures));
            }

            LastResult = new SpoolShardInitializationResult(results, created, present, elapsed);
            SpoolShardLogMessages.Summary(_logger, results.Length, created, present, (long)elapsed.TotalMilliseconds);
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Distinct canonical spool paths from the validation catalog.</summary>
        private List<string> UniqueSpools() =>
            _catalog.Devices
                .SelectMany(static device => device.PoolPaths)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Creates every shard directory for one spool. Levels run in order so parents exist.
        /// A failure stops this spool's remaining creates and leaves other spools running.
        /// </summary>
        private async Task<SpoolShardSpoolResult> InitializeSpoolAsync(
            string spoolPath,
            SemaphoreSlim gate,
            int workers,
            CancellationToken cancellationToken)
        {
            var started = _time.GetTimestamp();
            var created = 0L;
            var present = 0L;
            var failure = new string?[1];
            using var spoolCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                foreach (var (start, count) in SpoolShardLayout.LevelRanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunLevelAsync(
                        spoolPath,
                        start,
                        count,
                        gate,
                        workers,
                        spoolCancellation.Token,
                        cancellationToken,
                        failure,
                        () => spoolCancellation.Cancel(),
                        () => Interlocked.Increment(ref created),
                        () => Interlocked.Increment(ref present)).ConfigureAwait(false);
                    if (Volatile.Read(ref failure[0]) is not null)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var elapsed = _time.GetElapsedTime(started);
            var failureText = Volatile.Read(ref failure[0]);
            if (failureText is not null)
            {
                SpoolShardLogMessages.SpoolFailed(_logger, spoolPath, created, present, failureText);
                return new SpoolShardSpoolResult(spoolPath, created, present, failureText, elapsed);
            }

            SpoolShardLogMessages.SpoolCompleted(_logger, spoolPath, created, present, (long)elapsed.TotalMilliseconds);
            return new SpoolShardSpoolResult(spoolPath, created, present, null, elapsed);
        }

        /// <summary>Creates one level of directories with a bounded worker set.</summary>
        private async Task RunLevelAsync(
            string spoolPath,
            int start,
            int count,
            SemaphoreSlim gate,
            int workers,
            CancellationToken spoolToken,
            CancellationToken hostToken,
            string?[] failure,
            Action cancelSpool,
            Action created,
            Action present)
        {
            var next = 0;
            var workerTasks = new Task[workers];
            for (var i = 0; i < workers; i++)
            {
                workerTasks[i] = Task.Run(() => WorkerAsync(), hostToken);
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);

            async Task WorkerAsync()
            {
                while (true)
                {
                    hostToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref failure[0]) is not null || spoolToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var offset = Interlocked.Increment(ref next) - 1;
                    if (offset >= count)
                    {
                        return;
                    }

                    await gate.WaitAsync(hostToken).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref failure[0]) is not null || spoolToken.IsCancellationRequested)
                        {
                            return;
                        }

                        var fullPath = UnderSpool(spoolPath, SpoolShardLayout.RelativeDirectories[start + offset]);
                        var result = _directories.CreateDirectory(fullPath);
                        if (result == SpoolDirectoryCreateResult.Created)
                        {
                            created();
                        }
                        else
                        {
                            present();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && !ExceptionHelpers.IsFatal(ex))
                    {
                        Interlocked.CompareExchange(ref failure[0], ex.GetType().Name + ": " + ex.Message, null);
                        cancelSpool();
                        return;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }
        }

        /// <summary>Joins a relative shard path onto a spool root without interpreting <c>..</c>.</summary>
        private static string UnderSpool(string spoolRoot, string relative)
        {
            var path = spoolRoot;
            var rest = relative.AsSpan();
            while (rest.Length > 0)
            {
                var slash = rest.IndexOf('/');
                var segment = slash < 0 ? rest : rest[..slash];
                path = Path.Join(path, segment.ToString());
                if (slash < 0)
                {
                    return path;
                }

                rest = rest[(slash + 1)..];
            }

            return path;
        }
    }

    /// <summary>One spool's initialization counts.</summary>
    /// <param name="SpoolPath">Canonical spool root.</param>
    /// <param name="Created">Directories created by this attempt.</param>
    /// <param name="AlreadyPresent">Directories that already existed.</param>
    /// <param name="Failure">Failure text, or <see langword="null"/> when the spool finished.</param>
    /// <param name="Elapsed">Time spent on this spool.</param>
    internal readonly record struct SpoolShardSpoolResult(
        string SpoolPath,
        long Created,
        long AlreadyPresent,
        string? Failure,
        TimeSpan Elapsed);

    /// <summary>Aggregate initialization counts after every spool has finished.</summary>
    /// <param name="Spools">Per-spool results.</param>
    /// <param name="Created">Directories created across all spools.</param>
    /// <param name="AlreadyPresent">Directories that already existed across all spools.</param>
    /// <param name="Elapsed">Wall time from the start of initialization.</param>
    internal readonly record struct SpoolShardInitializationResult(
        IReadOnlyList<SpoolShardSpoolResult> Spools,
        long Created,
        long AlreadyPresent,
        TimeSpan Elapsed);
}
