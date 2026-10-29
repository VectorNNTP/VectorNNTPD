using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.OverviewDb;
using VectorNNTPD.TransitService.ArticleIngestion.OverviewDb;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Storage;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Background application service that drains CanonicalV1 queued articles through
    /// a dynamically sized <see cref="IngestionWorkerPool"/> and hands OverviewDB
    /// payloads to an independent publisher stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Article workers encode OverviewArticleV1, enqueue the owned bytes onto a bounded
    /// in-process <see cref="IOverviewDbWorkQueue"/>, then write the news log and Path
    /// survey. The article body is then discarded. StorageServer/VATP placement remains
    /// on this type and is temporarily not invoked.
    /// Workers do <b>not</b> await RabbitMQ publication or publisher confirmation.
    /// Successful enqueue is not durable RabbitMQ handoff; broker confirmation remains
    /// owned by <see cref="OverviewDbPublisherPool"/> via asynchronous outstanding
    /// confirms on <see cref="IOverviewDbHandoffPublisher"/>.
    /// </para>
    /// <para>
    /// OverviewDB publisher workers scale independently from article workers based on
    /// OverviewDB work-queue pressure. Publisher confirms, mandatory publishing,
    /// persistent delivery, and the 2-second AMQP expiration are unchanged.
    /// </para>
    /// </remarks>
    public sealed class IncomingSpoolWriterService : IApplicationService
    {
        private static readonly TimeSpan RejectedPressureRetryDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets whether StorageServer placement runs after Path survey.
        /// </summary>
        /// <remarks>
        /// Temporarily <see langword="false"/>. Placement methods stay on this type so the
        /// bypass is reversed by returning <see langword="true"/>.
        /// </remarks>
        private static bool ArticlePlacementEnabled => false;

        private readonly IArticleIngestionQueue _queue;
        private readonly INewsLogWriter _newsLog;
        private readonly IPathSurveyWriter _pathSurvey;
        private readonly INewsgroupCatalogue? _catalogue;
        private readonly IOverviewDbHandoffPublisher _overviewHandoff;
        private readonly IOptions<NntpdOptions> _options;
        private readonly ILogger<IncomingSpoolWriterService> _logger;
        private readonly IFeedDiagnostics _feedDiagnostics;
        private readonly IngestionPipelineMetrics? _pipeline;
        private readonly TimeProvider _time;
        private readonly Func<IngestionPressureSnapshot>? _samplePressure;
        private readonly IArticlePlacementClient? _placement;
        private readonly IStorageServerRegistry? _placementRegistry;
        private readonly CancellationTokenSource _articleRunCts = new();
        private readonly CancellationTokenSource _overviewRunCts = new();
        private Task? _execution;
        private int _started;
        private IngestionWorkerPool? _pool;
        private OverviewDbWorkQueue? _overviewWorkQueue;
        private OverviewDbPublisherPool? _overviewPublisherPool;
        private Task? _overviewPublisherExecution;

        /// <summary>Initializes a new instance of the <see cref="IncomingSpoolWriterService"/> class.</summary>
        public IncomingSpoolWriterService(
            IArticleIngestionQueue queue,
            IOptions<NntpdOptions> options,
            ILogger<IncomingSpoolWriterService> logger,
            IFeedDiagnostics? feedDiagnostics = null,
            INewsLogWriter? newsLog = null,
            INewsgroupCatalogue? catalogue = null,
            TimeProvider? timeProvider = null,
            IOverviewDbHandoffPublisher? overviewHandoff = null,
            IngestionPipelineMetrics? pipelineMetrics = null,
            IPathSurveyWriter? pathSurvey = null,
            Func<IngestionPressureSnapshot>? samplePressure = null,
            IArticlePlacementClient? placement = null,
            IStorageServerRegistry? placementRegistry = null)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _queue = queue;
            _options = options;
            _logger = logger;
            _feedDiagnostics = feedDiagnostics ?? NullFeedDiagnostics.Instance;
            _newsLog = newsLog ?? NullNewsLogWriter.Instance;
            _pathSurvey = pathSurvey ?? NullPathSurveyWriter.Instance;
            _catalogue = catalogue;
            _time = timeProvider ?? TimeProvider.System;
            _overviewHandoff = overviewHandoff ?? NullOverviewDbHandoffPublisher.Instance;
            _pipeline = pipelineMetrics;
            _samplePressure = samplePressure;
            _placement = placement;
            _placementRegistry = placementRegistry;
            PressureRetryDelay = (delay, cancellationToken) => Task.Delay(delay, _time, cancellationToken);
        }

        /// <inheritdoc />
        public string Name => "IncomingSpoolWriter";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Gets the active article worker pool (tests).</summary>
        internal IngestionWorkerPool? Pool => _pool;

        /// <summary>
        /// Wait between a <see cref="ArticlePlacementKind.RejectedPressure"/> result and the one retry.
        /// Production waits five seconds on <see cref="TimeProvider"/>.
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task> PressureRetryDelay { get; set; }

        /// <summary>Gets the OverviewDB work queue (tests).</summary>
        internal IOverviewDbWorkQueue? OverviewWorkQueue => _overviewWorkQueue;

        /// <summary>Gets the OverviewDB publisher pool (tests).</summary>
        internal OverviewDbPublisherPool? OverviewPublisherPool => _overviewPublisherPool;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            var ingestion = _options.Value.ArticleIngestion ?? new ArticleIngestionOptions();
            SpoolLogMessages.WriterStarted(
                _logger,
                _queue.MemoryLimitBytes,
                _queue.MaxArticleBytes);

            _overviewWorkQueue = new OverviewDbWorkQueue(ingestion.OverviewDbWorkQueueMemoryLimit);
            _overviewPublisherPool = new OverviewDbPublisherPool(
                _overviewWorkQueue,
                _overviewHandoff,
                ingestion,
                _logger,
                _pipeline,
                _time);
            _overviewPublisherExecution = Task.Run(
                () => _overviewPublisherPool.RunAsync(_overviewRunCts.Token),
                CancellationToken.None);

            _pool = new IngestionWorkerPool(
                _queue,
                ProcessArticleAsync,
                ingestion,
                _logger,
                _pipeline,
                _samplePressure,
                _time);
            _execution = Task.Run(() => _pool.RunAsync(_articleRunCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // 1) Stop admitting article ingress and drain article workers (they may still
            //    enqueue OverviewDB work while draining). Publisher pool keeps running.
            _queue.Complete();
            await _articleRunCts.CancelAsync().ConfigureAwait(false);

            var execution = _execution;
            if (execution is not null)
            {
                try
                {
                    await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SpoolLogMessages.StopCanceledWithBufferedArticles(_logger, _queue.Count);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    SpoolLogMessages.WriterStoppedWithError(_logger, ex);
                }
            }

            // 2) Complete OverviewDB work queue and drain publisher workers for a bounded period.
            //    Outstanding OverviewDB confirms may be abandoned after the configured timeout.
            _overviewWorkQueue?.Complete();
            await _overviewRunCts.CancelAsync().ConfigureAwait(false);
            var overviewExecution = _overviewPublisherExecution;
            if (overviewExecution is not null)
            {
                var shutdownSeconds = Math.Max(
                    1,
                    (_options.Value.ArticleIngestion ?? new ArticleIngestionOptions())
                        .OverviewDbPublisherShutdownSeconds);
                using var overviewDrainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                overviewDrainCts.CancelAfter(TimeSpan.FromSeconds(shutdownSeconds));
                try
                {
                    await overviewExecution.WaitAsync(overviewDrainCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    SpoolLogMessages.WriterStoppedWithError(_logger, ex);
                }
            }

            _overviewHandoff.AbandonOutstanding();

            try
            {
                _newsLog.Flush();
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SpoolLogMessages.NewsLogFlushFailed(_logger, ex);
            }

            try
            {
                _pathSurvey.Flush();
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SpoolLogMessages.PathSurveyFlushFailed(_logger, ex);
            }

            SpoolLogMessages.WriterStopped(_logger);
        }

        private async Task ProcessArticleAsync(
            InboundArticle article,
            long itemStart,
            CancellationToken cancellationToken)
        {
            var overviewAccepted = false;
            _feedDiagnostics.BeginSpoolWork();
            try
            {
                await EnqueueOverviewWorkAsync(article, itemStart, cancellationToken).ConfigureAwait(false);
                overviewAccepted = true;
                var newsStart = System.Diagnostics.Stopwatch.GetTimestamp();
                WriteNewsLog(article);
                _pipeline?.RecordNews(newsStart);
                WritePathSurvey(article);
                if (ArticlePlacementEnabled)
                {
                    if (article.Producer == InboundArticleProducer.BackFiller)
                    {
                        await StoreBackFillerAsync(article, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await PlaceAfterPersistAsync(article, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    DiscardAcceptedArticle(article);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (!overviewAccepted)
                {
                    OverviewDbHandoffLogMessages.PublishFailed(
                        _logger,
                        ex,
                        article.MessageId,
                        article.Payload.Length);
                    await RequeueAsync(article).ConfigureAwait(false);
                }
                else
                {
                    SpoolLogMessages.FinishFailed(
                        _logger,
                        ex,
                        article.MessageId,
                        article.Payload.Length);
                }
            }
            finally
            {
                _feedDiagnostics.EndSpoolWork(article.Payload.Length, persisted: false);
            }
        }

        /// <summary>
        /// One VATP STORE of a BackFiller article to one active StorageServer.
        /// </summary>
        /// <remarks>
        /// The target is the active registry entry with a dialable VATP port and the lowest
        /// <see cref="StorageServerFleetEntry.ServerId"/>, then ordinal FQDN. Advertised free
        /// space is ignored. This method does not call
        /// <see cref="StorageServerPlacementSelector"/> or <see cref="PlaceAfterPersistAsync"/>.
        /// Accepted and Duplicate are terminal success.
        /// <see cref="ArticlePlacementKind.RejectedPressure"/> is logged and retried once,
        /// after five seconds, against the same server and the same article. Every other
        /// <see cref="ArticlePlacementKind"/> is logged once and not retried. NNTPD does not
        /// place a second copy.
        /// </remarks>
        private async Task StoreBackFillerAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            if (_placement is null || _placementRegistry is null)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var artId = article.Record.ArtId.ToLowerHexString();
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ArticlePlacementLogMessages.Cancelled(_logger, artId, 0, 0, string.Empty, Elapsed(started), Attempt: 1);
                    return;
                }

                if (!TrySelectBackFillerTarget(_placementRegistry.GetActive(_time.GetUtcNow()), out var target)
                    || target.VatpPort is not int port)
                {
                    ArticlePlacementLogMessages.NoActiveServer(_logger, artId, Elapsed(started), Attempt: 1);
                    return;
                }

                await PlaceOnSelectedTargetAsync(article.Record, target, port, artId, started, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ArticlePlacementLogMessages.Cancelled(_logger, artId, 0, 0, string.Empty, Elapsed(started), Attempt: 1);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticlePlacementLogMessages.TransportFailed(
                    _logger,
                    artId,
                    0,
                    0,
                    string.Empty,
                    ex.GetType().Name,
                    Elapsed(started),
                    Attempt: 1);
            }
        }

        /// <summary>
        /// Chooses one dialable active StorageServer for <see cref="StoreBackFillerAsync"/>.
        /// </summary>
        /// <remarks>
        /// Lowest <see cref="StorageServerFleetEntry.ServerId"/>, then ordinal FQDN.
        /// Entries without a VATP port from 1 through 65535 are skipped. Free space
        /// and placement persistence are not used.
        /// </remarks>
        private static bool TrySelectBackFillerTarget(
            IReadOnlyList<StorageServerFleetEntry> active,
            out StorageServerFleetEntry selected)
        {
            ArgumentNullException.ThrowIfNull(active);
            selected = default;
            var found = false;
            foreach (var entry in active)
            {
                if (entry.VatpPort is not (>= 1 and <= 65535))
                {
                    continue;
                }

                if (!found
                    || entry.ServerId < selected.ServerId
                    || (entry.ServerId == selected.ServerId
                        && string.CompareOrdinal(entry.Fqdn, selected.Fqdn) < 0))
                {
                    selected = entry;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// One STORE after persist. Accepted and Duplicate complete placement.
        /// <see cref="ArticlePlacementKind.RejectedPressure"/> is retried once against the same
        /// target and article. Failures stay in this method so they cannot requeue OverviewDB
        /// work or undo persistence. NNTPD does not select or store a second copy.
        /// </summary>
        private async Task PlaceAfterPersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            if (article.Producer is not (
                InboundArticleProducer.TakeThis
                or InboundArticleProducer.IHave
                or InboundArticleProducer.Post))
            {
                return;
            }

            if (_placement is null || _placementRegistry is null)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var artId = article.Record.ArtId.ToLowerHexString();
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ArticlePlacementLogMessages.Cancelled(_logger, artId, 0, 0, string.Empty, Elapsed(started), Attempt: 1);
                    return;
                }

                var active = _placementRegistry.GetActive(_time.GetUtcNow());
                if (!StorageServerPlacementSelector.TrySelect(active, out var target) || target.VatpPort is not int port)
                {
                    ArticlePlacementLogMessages.NoActiveServer(_logger, artId, Elapsed(started), Attempt: 1);
                    return;
                }

                await PlaceOnSelectedTargetAsync(article.Record, target, port, artId, started, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ArticlePlacementLogMessages.Cancelled(_logger, artId, 0, 0, string.Empty, Elapsed(started), Attempt: 1);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                ArticlePlacementLogMessages.TransportFailed(
                    _logger,
                    artId,
                    0,
                    0,
                    string.Empty,
                    ex.GetType().Name,
                    Elapsed(started),
                    Attempt: 1);
            }
        }

        /// <summary>
        /// Stores <paramref name="record"/> on <paramref name="target"/>.
        /// <see cref="ArticlePlacementKind.RejectedPressure"/> is logged as attempt 1 and
        /// retried once, after <see cref="RejectedPressureRetryDelay"/>, with the same record
        /// and target. The second result is settled by <see cref="LogPlacementResult"/>.
        /// </summary>
        private async Task PlaceOnSelectedTargetAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            int port,
            string artId,
            long started,
            CancellationToken cancellationToken)
        {
            var result = await _placement!.PlaceAsync(record, target, cancellationToken).ConfigureAwait(false);
            if (result.Kind != ArticlePlacementKind.RejectedPressure)
            {
                LogPlacementResult(result, artId, target, port, Elapsed(started), attempt: 1);
                return;
            }

            LogPlacementResult(result, artId, target, port, Elapsed(started), attempt: 1);
            await PressureRetryDelay(RejectedPressureRetryDelay, cancellationToken).ConfigureAwait(false);
            var retryStarted = Stopwatch.GetTimestamp();
            result = await _placement.PlaceAsync(record, target, cancellationToken).ConfigureAwait(false);
            LogPlacementResult(result, artId, target, port, Elapsed(retryStarted), attempt: 2);
        }

        /// <summary>Logs one placement result. The attempt number is the STORE call that produced it.</summary>
        private void LogPlacementResult(
            ArticlePlacementResult result,
            string artId,
            StorageServerFleetEntry target,
            int port,
            long elapsedMs,
            int attempt)
        {
            switch (result.Kind)
            {
                case ArticlePlacementKind.Accepted:
                    ArticlePlacementLogMessages.Accepted(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.Duplicate:
                    ArticlePlacementLogMessages.Duplicate(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.Conflict:
                    ArticlePlacementLogMessages.Conflict(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.RejectedCapacity:
                    ArticlePlacementLogMessages.RejectedCapacity(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.RejectedPressure:
                    ArticlePlacementLogMessages.RejectedPressure(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.RejectedInvalid:
                    ArticlePlacementLogMessages.RejectedInvalid(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.Cancelled:
                    ArticlePlacementLogMessages.Cancelled(_logger, artId, target.ServerId, port, target.Fqdn, elapsedMs, attempt);
                    break;
                case ArticlePlacementKind.AcknowledgementNotObserved:
                    ArticlePlacementLogMessages.AcknowledgementNotObserved(
                        _logger,
                        artId,
                        target.ServerId,
                        port,
                        target.Fqdn,
                        elapsedMs,
                        attempt);
                    break;
                default:
                    ArticlePlacementLogMessages.TransportFailed(
                        _logger,
                        artId,
                        target.ServerId,
                        port,
                        target.Fqdn,
                        result.Failure ?? result.Kind.ToString(),
                        elapsedMs,
                        attempt);
                    break;
            }
        }

        private static long Elapsed(long started) =>
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        /// <summary>
        /// Writes the post-queue INN <c>news</c> event. Failures are logged and
        /// swallowed so the already-accepted article still reaches Path survey.
        /// </summary>
        private void WriteNewsLog(InboundArticle article)
        {
            try
            {
                var transit = _options.Value.Transit ?? new TransitOptions();
                if (!IngressNewsDisposition.TryCreateEvent(
                        article,
                        transit,
                        _catalogue,
                        _time.GetLocalNow(),
                        out var evt))
                {
                    return;
                }

                _newsLog.Write(in evt);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SpoolLogMessages.NewsLogFailed(_logger, ex, article.MessageId);
            }
        }

        /// <summary>
        /// Writes the canonical Path-survey observation. Failures are logged and
        /// swallowed so the worker still discards the article and continues.
        /// </summary>
        private void WritePathSurvey(InboundArticle article)
        {
            try
            {
                _pathSurvey.Write(article.Record.Path);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                SpoolLogMessages.PathSurveyFailed(_logger, ex, article.MessageId);
            }
        }

        /// <summary>
        /// Drops <paramref name="article"/> after Path survey. The body is not copied,
        /// written, or passed to StorageServer selection or VATP placement.
        /// </summary>
        private static void DiscardAcceptedArticle(InboundArticle article)
        {
            ArgumentNullException.ThrowIfNull(article);
        }

        /// <summary>
        /// Encodes OverviewArticleV1 and enqueues it onto the in-process OverviewDB work
        /// queue. Does not call RabbitMQ.
        /// </summary>
        private async Task EnqueueOverviewWorkAsync(
            InboundArticle article,
            long itemStart,
            CancellationToken cancellationToken)
        {
            var workQueue = _overviewWorkQueue
                ?? throw new InvalidOperationException("OverviewDB work queue is not started.");

            var max = OverviewArticleV1Codec.GetMaxEncodedSize(article.Record);
            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(max, 1));
            try
            {
                var encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var written = OverviewArticleV1Codec.Encode(article.Record, rented);
                _pipeline?.RecordEncode(encodeStart);
                _pipeline?.RecordToPublishStart(itemStart);

                var owned = new byte[written];
                rented.AsSpan(0, written).CopyTo(owned);
                var item = new OverviewDbWorkItem(owned, article.MessageId);
                var result = await workQueue.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
                if (result != ArticleEnqueueResult.Accepted)
                {
                    throw new InvalidOperationException(
                        $"OverviewDB work queue rejected handoff enqueue ({result}).");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private async Task RequeueAsync(InboundArticle article)
        {
            var result = await _queue.EnqueueAsync(article, CancellationToken.None).ConfigureAwait(false);
            if (result == ArticleEnqueueResult.Accepted)
            {
                OverviewDbHandoffLogMessages.Requeued(_logger, article.MessageId);
                return;
            }

            OverviewDbHandoffLogMessages.RequeueUnavailable(_logger, article.MessageId);
        }
    }
}
