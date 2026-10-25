using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// End-to-end stress orchestrator: Article Work publish → RabbitMQ reply → TLS ARTICLE delivery.
    /// </summary>
    /// <remarks>
    /// The RabbitMQ reply is routing metadata only. Article payload bytes are measured exclusively
    /// from the BackFiller NNTP listener after a complete multiline response is validated.
    /// Upstream fetch start/end are not observable without production changes; publish-to-reply
    /// is reported as an aggregate BackFiller interval.
    /// </remarks>
    public sealed class BackFillerE2EStressHarness
    {
        private readonly StressHarnessOptions _options;

        public BackFillerE2EStressHarness(StressHarnessOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<NewsLogDiscoveryStats> DiscoverAndWriteWorkloadAsync(
            CancellationToken cancellationToken)
        {
            NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(_options.NewsLogDirectory);
            Directory.CreateDirectory(_options.WorkloadDirectory);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var stats = await NewsLogMessageIdDiscovery.DiscoverAsync(
                    _options.NewsLogDirectory,
                    unique,
                    cancellationToken)
                .ConfigureAwait(false);

            var partitions = NewsLogMessageIdDiscovery.WriteWorkloadSets(
                unique,
                _options.WorkloadDirectory,
                _options.Seed,
                warmupCount: 1_000,
                mainCount: 100_000,
                repeatableCount: 2_000);

            var mainPath = Path.Join(_options.WorkloadDirectory, StressCorpusPaths.MainIdsAuditFileName);
            var manifest = new WorkloadCorpusManifest
            {
                CorpusDirectory = Path.GetFullPath(_options.NewsLogDirectory),
                FileCount = stats.FileCount,
                LogRecordsExamined = stats.TotalLogEntries,
                ValidParsedIds = stats.ValidIds,
                RejectedInvalidMessageIds = stats.RejectedInvalidMessageIds,
                UniqueValidIds = stats.UniqueIds,
                WarmupSelected = partitions.Warmup,
                MainSelected = partitions.Main,
                RepeatableSelected = partitions.Repeatable,
                Seed = partitions.Seed,
                MainIdsSha256 = WorkloadCorpusManifest.ComputeFileSha256(mainPath),
                CreatedUtc = DateTimeOffset.UtcNow,
                Notes =
                    "Message-IDs extracted only from the configured news-log corpus after canonical validation. "
                    + "No synthetic fallback.",
            };
            await WorkloadCorpusManifest.WriteAsync(_options.WorkloadDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);

            var statsPath = Path.Join(_options.WorkloadDirectory, "discovery-stats.json");
            var payload = new
            {
                CorpusDirectory = manifest.CorpusDirectory,
                stats.FileCount,
                stats.TotalLogEntries,
                stats.ValidIds,
                stats.UniqueIds,
                stats.MalformedEntries,
                stats.RejectedInvalidMessageIds,
                stats.StatusPlus,
                stats.StatusMinus,
                stats.StatusJ,
                stats.ProviderGiganews,
                stats.ProviderEweka,
                stats.ProviderOther,
                stats.DuplicateRatePercent,
                partitions.Warmup,
                partitions.Main,
                partitions.Repeatable,
                partitions.Seed,
                manifest.MainIdsSha256,
            };
            await File.WriteAllTextAsync(
                    statsPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);

            return stats;
        }

        public async Task<StressRunSummary> RunAsync(
            string runName,
            string backbone,
            string messageIdFile,
            int concurrency,
            int maxInFlight,
            TimeSpan duration,
            int? maxRequests,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runName);
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageIdFile);
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlight, 1);

            // Static publish gate: corpus + provenance + SHA before any AMQP connect/publish.
            NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(_options.NewsLogDirectory);
            await StressWorkloadGuard.RequireProvenancedWorkloadAsync(_options, cancellationToken)
                .ConfigureAwait(false);

            var ids = await LoadMessageIdsAsync(messageIdFile, cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException($"Workload file '{messageIdFile}' contained no Message-IDs.");
            }

            foreach (var id in ids)
            {
                if (!NewsLogMessageIdDiscovery.IsCanonicalMessageId(id))
                {
                    throw new InvalidOperationException(
                        "Workload contains a Message-ID that fails canonical validation: " + id
                        + ". Re-run discovery against the required corpus; do not invent replacements.");
                }
            }

            var runId = Guid.NewGuid().ToString("D");
            var metrics = new StressMetrics
            {
                RunName = runName,
                Backbone = backbone,
                ConfiguredConcurrency = concurrency,
                ConfiguredMaxInFlight = maxInFlight,
            };
            Directory.CreateDirectory(_options.ReportDirectory);
            await File.WriteAllTextAsync(
                    Path.Join(_options.ReportDirectory, $"run-ids-{runName}-{runId}.txt"),
                    string.Join(Environment.NewLine, ids),
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Join(_options.ReportDirectory, $"run-meta-{runName}-{runId}.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            RunId = runId,
                            RunName = runName,
                            Backbone = backbone,
                            CorpusDirectory = _options.NewsLogDirectory,
                            MessageIdFile = messageIdFile,
                            SelectedCount = ids.Count,
                            Seed = _options.Seed,
                        },
                        new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);

            var workClient = await ArticleWorkStressClient.ConnectAsync(_options, cancellationToken)
                .ConfigureAwait(false);
            workClient.StressRunId = runId;

            try
            {
            // Live retrieval preflight runs after the reply consumer is ready and before any throughput publish.
            if (_options.ArticlePreflightCount > 0)
            {
                var preflight = await CorpusArticlePreflight.RunLiveRetrievalPreflightAsync(
                        workClient,
                        backbone,
                        ids,
                        _options.ArticlePreflightCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(
                        Path.Join(_options.ReportDirectory, $"preflight-articles-{runName}-{runId}.json"),
                        JsonSerializer.Serialize(
                            preflight.Select(static r => new { r.MessageId, Kind = r.Kind.ToString(), r.Detail }),
                            new JsonSerializerOptions { WriteIndented = true }),
                        cancellationToken)
                    .ConfigureAwait(false);
                CorpusArticlePreflight.ThrowIfPreflightBlocksPublish(preflight);
            }

            var listenerHost = _options.ListenerHostOverride;
            var listenerPort = _options.ListenerPortOverride ?? 0;
            BackFillerNntpConnectionPool? pool = null;
            var poolGate = new object();

            var workChannel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(maxInFlight)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

            var deliveryChannel = Channel.CreateBounded<DeliveryItem>(new BoundedChannelOptions(maxInFlight)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
            if (duration > TimeSpan.Zero)
            {
                feedCts.CancelAfter(duration);
            }

            metrics.SteadyStateStartTimestamp = Stopwatch.GetTimestamp();
            var queueName = ArticleWorkStressClient.ComposeProviderEntity(backbone);
            var sampler = Task.Run(
                async () =>
                {
                    while (!runCts.IsCancellationRequested)
                    {
                        metrics.SampleThroughput(Stopwatch.GetTimestamp());
                        await SampleQueueDepthSafeAsync(metrics, queueName, runCts.Token).ConfigureAwait(false);
                        try
                        {
                            await Task.Delay(1000, runCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                },
                CancellationToken.None);

            var feeder = Task.Run(
                async () =>
                {
                    var index = 0;
                    var issued = 0;
                    try
                    {
                        while (!feedCts.IsCancellationRequested)
                        {
                            if (maxRequests is { } limit && issued >= limit)
                            {
                                break;
                            }

                            var messageId = ids[index % ids.Count];
                            index++;
                            issued++;
                            var item = new WorkItem(messageId, backbone, Stopwatch.GetTimestamp());
                            await workChannel.Writer.WriteAsync(item, feedCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Feeder stops when the measured duration or abort cancels feedCts.
                    }
                    finally
                    {
                        workChannel.Writer.TryComplete();
                    }
                },
                CancellationToken.None);

            var publishers = new Task[Math.Min(concurrency, maxInFlight)];
            for (var i = 0; i < publishers.Length; i++)
            {
                publishers[i] = Task.Run(
                    async () =>
                    {
                        await foreach (var item in workChannel.Reader.ReadAllAsync(CancellationToken.None)
                                           .ConfigureAwait(false))
                        {
                            if (runCts.IsCancellationRequested)
                            {
                                break;
                            }

                            metrics.RequestStarted();
                            try
                            {
                                var reply = await workClient.RequestAsync(
                                        item.Backbone,
                                        item.MessageId,
                                        runCts.Token)
                                    .ConfigureAwait(false);
                                metrics.RecordWorkReply(item.Backbone, reply.Outcome);

                                var scheduleToPublish = reply.PublishCompletedTimestamp > 0
                                    ? (long)Stopwatch.GetElapsedTime(item.StartedTimestamp, reply.PublishCompletedTimestamp)
                                        .TotalMilliseconds
                                    : 0;
                                var publishCall = reply.PublishMilliseconds;
                                var publishToReply = reply.LatencyMilliseconds;

                                if (reply.Outcome != ArticleWorkStressOutcome.Success
                                    || string.IsNullOrWhiteSpace(reply.Fqdn)
                                    || reply.ListenerPort is not { } port)
                                {
                                    var failure = NntpArticleResponseReader.ClassifyFailure(
                                        reply.Outcome,
                                        nntpKind: null,
                                        timedOut: reply.Outcome == ArticleWorkStressOutcome.NoResponse,
                                        disconnected: false);
                                    var endToEnd = (long)Stopwatch.GetElapsedTime(item.StartedTimestamp).TotalMilliseconds;
                                    metrics.RecordStageTimings(
                                        scheduleToPublish,
                                        publishCall,
                                        publishToReply,
                                        replyToNntpStartMs: null,
                                        nntpDeliveryMs: null,
                                        endToEnd,
                                        success: false,
                                        logicalBytes: null);
                                    metrics.RecordFailure(item.Backbone, failure, endToEndMs: null);
                                    MaybeAbort(metrics, runCts);
                                    continue;
                                }

                                listenerHost ??= reply.Fqdn;
                                if (listenerPort == 0)
                                {
                                    listenerPort = port;
                                }

                                if (_options.ListenerHostOverride is not null)
                                {
                                    listenerHost = _options.ListenerHostOverride;
                                }

                                if (_options.ListenerPortOverride is { } overridePort)
                                {
                                    listenerPort = overridePort;
                                }

                                if (pool is null && listenerHost is not null && listenerPort > 0)
                                {
                                    lock (poolGate)
                                    {
                                        pool ??= new BackFillerNntpConnectionPool(
                                            listenerHost,
                                            listenerPort,
                                            concurrency);
                                    }
                                }

                                await deliveryChannel.Writer.WriteAsync(
                                        new DeliveryItem(
                                            item.MessageId,
                                            item.Backbone,
                                            item.StartedTimestamp,
                                            reply.RequestId,
                                            reply.CorrelationId,
                                            scheduleToPublish,
                                            publishCall,
                                            publishToReply,
                                            reply.ReplyReceivedTimestamp,
                                            listenerHost!,
                                            listenerPort),
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                            {
                                break;
                            }
                            // codeql[cs/catch-of-all-exceptions] Publisher worker must record any broker/RPC failure and keep flooding other items.
                            catch (Exception)
                            {
                                metrics.RecordFailure(
                                    item.Backbone,
                                    StressFailureClass.Other,
                                    (long)Stopwatch.GetElapsedTime(item.StartedTimestamp).TotalMilliseconds);
                                MaybeAbort(metrics, runCts);
                            }
                            finally
                            {
                                metrics.RequestFinished();
                            }
                        }
                    },
                    CancellationToken.None);
            }

            var deliveryWorkers = new Task[concurrency];
            for (var i = 0; i < deliveryWorkers.Length; i++)
            {
                deliveryWorkers[i] = Task.Run(
                    async () =>
                    {
                        await foreach (var item in deliveryChannel.Reader.ReadAllAsync(CancellationToken.None)
                                           .ConfigureAwait(false))
                        {
                            BackFillerNntpConnectionPool? localPool;
                            lock (poolGate)
                            {
                                localPool = pool;
                            }

                            if (localPool is null)
                            {
                                metrics.RecordFailure(item.Backbone, StressFailureClass.Other, null);
                                continue;
                            }

                            metrics.DeliveryStarted();
                            BackFillerNntpStressClient? client = null;
                            var healthy = false;
                            try
                            {
                                using var articleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                articleCts.CancelAfter(_options.ArticleTimeout);
                                client = await localPool.RentAsync(metrics, articleCts.Token).ConfigureAwait(false);
                                var nntpStarted = Stopwatch.GetTimestamp();
                                var replyToNntp = item.ReplyReceivedTimestamp > 0
                                    ? (long)Stopwatch.GetElapsedTime(item.ReplyReceivedTimestamp, nntpStarted)
                                        .TotalMilliseconds
                                    : 0;
                                var exchange = await client.ArticleAsync(item.MessageId, articleCts.Token)
                                    .ConfigureAwait(false);
                                var nntpMs = (long)Stopwatch.GetElapsedTime(nntpStarted).TotalMilliseconds;
                                var endToEndMs = (long)Stopwatch.GetElapsedTime(item.StartedTimestamp).TotalMilliseconds;

                                if (exchange.Kind == NntpResponseKind.ArticleDelivered)
                                {
                                    metrics.RecordStageTimings(
                                        item.ScheduleToPublishMs,
                                        item.PublishCallMs,
                                        item.PublishToReplyMs,
                                        replyToNntp,
                                        nntpMs,
                                        endToEndMs,
                                        success: true,
                                        exchange.LogicalArticleBytes);
                                    metrics.RecordArticleDelivered(
                                        item.Backbone,
                                        exchange.LogicalArticleBytes,
                                        exchange.WireArticleBytes,
                                        endToEndMs,
                                        nntpMs);
                                    healthy = true;
                                }
                                else
                                {
                                    var failure = NntpArticleResponseReader.ClassifyFailure(
                                        ArticleWorkStressOutcome.Success,
                                        exchange.Kind,
                                        timedOut: false,
                                        disconnected: exchange.Kind == NntpResponseKind.Disconnect);
                                    metrics.RecordStageTimings(
                                        item.ScheduleToPublishMs,
                                        item.PublishCallMs,
                                        item.PublishToReplyMs,
                                        replyToNntp,
                                        nntpMs,
                                        endToEndMs,
                                        success: false,
                                        logicalBytes: null);
                                    metrics.RecordFailure(item.Backbone, failure, endToEndMs: null, exchange.Kind);
                                    healthy = exchange.Kind is NntpResponseKind.NotFound
                                        or NntpResponseKind.NotAvailable
                                        or NntpResponseKind.SyntaxError;
                                    MaybeAbort(metrics, runCts);
                                }
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                metrics.RecordFailure(
                                    item.Backbone,
                                    StressFailureClass.Timeout,
                                    (long)Stopwatch.GetElapsedTime(item.StartedTimestamp).TotalMilliseconds);
                                MaybeAbort(metrics, runCts);
                            }
                            // codeql[cs/catch-of-all-exceptions] Delivery worker must classify any NNTP/transport failure and return the leased client.
                            catch (Exception)
                            {
                                metrics.RecordFailure(
                                    item.Backbone,
                                    StressFailureClass.Disconnect,
                                    (long)Stopwatch.GetElapsedTime(item.StartedTimestamp).TotalMilliseconds);
                                MaybeAbort(metrics, runCts);
                            }
                            finally
                            {
                                if (client is not null)
                                {
                                    localPool.Return(client, metrics, healthy);
                                }

                                metrics.DeliveryFinished();
                            }
                        }
                    },
                    CancellationToken.None);
            }

            _ = Task.Run(
                async () =>
                {
                    await Task.WhenAll(publishers).ConfigureAwait(false);
                    deliveryChannel.Writer.TryComplete();
                },
                CancellationToken.None);

            try
            {
                await Task.WhenAll(feeder).ConfigureAwait(false);
                await Task.WhenAll(publishers).ConfigureAwait(false);
                await Task.WhenAll(deliveryWorkers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Measured run cancelled via abort or host token; continue into drain/dispose.
            }
            finally
            {
                runCts.Cancel();
                try
                {
                    await sampler.ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Queue-depth sampler is optional observability and must not fail the soak.
                catch (Exception)
                {
                    // Sampler faults are non-fatal.
                }

                metrics.SteadyStateEndTimestamp = Stopwatch.GetTimestamp();
            }

            var drainStarted = Stopwatch.GetTimestamp();
            var drainDeadline = drainStarted
                + (long)(_options.ReplyQueueDisposeDrainTimeout.TotalSeconds * Stopwatch.Frequency);
            while ((metrics.Outstanding > 0
                    || metrics.DeliveryOutstanding > 0
                    || workClient.WaitingCount > 0
                    || workClient.BrokerOutstandingCount > 0)
                   && Stopwatch.GetTimestamp() < drainDeadline)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }

            metrics.SetDrainMilliseconds((long)Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds);
            var brokerOutstandingAtDrainEnd = workClient.BrokerOutstandingCount;
            if (brokerOutstandingAtDrainEnd > 0)
            {
                Console.Error.WriteLine(
                    $"Harness drain ended with brokerOutstanding={brokerOutstandingAtDrainEnd} waiting={workClient.WaitingCount}; dispose will apply bounded ReplyTo drain.");
            }

            if (pool is not null)
            {
                await pool.DisposeAsync().ConfigureAwait(false);
            }

            await workClient.DisposeAsync().ConfigureAwait(false);
            metrics.SetReplyLifecycleCounters(
                workClient.LateRepliesAfterTimeout,
                workClient.UnresolvedAtDispose,
                brokerOutstandingAtDrainEnd);

            var summary = metrics.BuildSummary(Process.GetCurrentProcess());
            var safeName = SanitizeFileName(runName);
            metrics.WriteJson(Path.Join(_options.ReportDirectory, safeName + ".json"), Process.GetCurrentProcess());
            await File.AppendAllTextAsync(
                    Path.Join(_options.ReportDirectory, "summary-table.txt"),
                    summary.FormatTableRow() + Environment.NewLine,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return summary;
            }
            // codeql[cs/catch-of-all-exceptions] Run failure must still dispose the exclusive ReplyTo client before rethrowing.
            catch (Exception)
            {
                await workClient.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task SampleQueueDepthSafeAsync(
            StressMetrics metrics,
            string queueName,
            CancellationToken cancellationToken)
        {
            try
            {
                var user = _options.RabbitMqUsername;
                var pass = _options.RabbitMqPassword;
                var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + pass));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
                var vhost = Uri.EscapeDataString(_options.RabbitMqVirtualHost);
                var q = Uri.EscapeDataString(queueName);
                var url = $"http://{_options.RabbitMqHost}:15672/api/queues/{vhost}/{q}";
                using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var root = doc.RootElement;
                var messages = root.TryGetProperty("messages", out var m) ? m.GetInt64() : 0;
                var ready = root.TryGetProperty("messages_ready", out var r) ? r.GetInt64() : 0;
                var unacked = root.TryGetProperty("messages_unacknowledged", out var u) ? u.GetInt64() : 0;
                var consumers = root.TryGetProperty("consumers", out var c) ? c.GetInt64() : 0;
                metrics.SampleQueueDepth(Stopwatch.GetTimestamp(), queueName, messages, ready, unacked, consumers);
            }
            // codeql[cs/catch-of-all-exceptions] Management API sampling is optional; any fault must not abort the soak.
            catch (Exception)
            {
                // Optional observability; never fail the run.
            }
        }

        private void MaybeAbort(StressMetrics metrics, CancellationTokenSource runCts)
        {
            var summary = metrics.BuildSummary();
            if (StressAbortDecision.ShouldAbort(
                    summary.RequestsStarted,
                    summary.WorkSuccess,
                    summary.WorkNotFound,
                    summary.WorkOtherFailure,
                    summary.Timeouts,
                    summary.ArticlesDelivered,
                    _options.AbortAfterRequests,
                    _options.AbortOnTimeoutRate,
                    _options.AbortOnFailureRate))
            {
                runCts.Cancel();
            }
        }

        private static async Task<IReadOnlyList<string>> LoadMessageIdsAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            return lines
                .Where(static line => !string.IsNullOrWhiteSpace(line) && line.StartsWith('<'))
                .ToArray();
        }

        private static string SanitizeFileName(string runName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = runName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        private readonly record struct WorkItem(string MessageId, string Backbone, long StartedTimestamp);

        private readonly record struct DeliveryItem(
            string MessageId,
            string Backbone,
            long StartedTimestamp,
            Guid? RequestId,
            string? CorrelationId,
            long ScheduleToPublishMs,
            long PublishCallMs,
            long PublishToReplyMs,
            long ReplyReceivedTimestamp,
            string Host,
            int Port);
    }
}
