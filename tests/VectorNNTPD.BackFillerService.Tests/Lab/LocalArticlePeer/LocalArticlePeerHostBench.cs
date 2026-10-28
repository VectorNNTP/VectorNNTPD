using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Opt-in lab host that keeps <see cref="LocalArticleNntpPeer"/> alive for a later authorized benchmark.
    /// Disabled unless <c>VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER=1</c>. Not a production service.
    /// </summary>
    /// <remarks>
    /// Correctness unit tests start ephemeral peers and dispose them. This entry point is only for
    /// operator-authorized local-peer stress and must not be enabled during implementation validation.
    /// </remarks>
    public sealed class LocalArticlePeerHostBench
    {
        /// <summary>Master enable for the long-lived peer host process.</summary>
        public const string EnableEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER";

        /// <summary>Corpus root override; defaults to repo <c>.artifacts/Articles</c>.</summary>
        public const string CorpusEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_CORPUS";

        /// <summary>Bind port (default 11119). Use a fixed port so lab MySQL can target the peer.</summary>
        public const string PortEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_PORT";

        /// <summary>Max concurrent sessions (default 64).</summary>
        public const string MaxSessionsEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_MAX_SESSIONS";

        /// <summary>Optional AUTHINFO USER for lab auth exercise.</summary>
        public const string UsernameEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_USERNAME";

        /// <summary>Optional AUTHINFO PASS for lab auth exercise.</summary>
        public const string PasswordEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_PASSWORD";

        /// <summary>Directory for identity/manifest JSON (default under local-article-peer-benchmark artifacts).</summary>
        public const string OutDirectoryEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_OUT";

        /// <summary>
        /// When <c>1</c>, preload and frame the corpus once at startup (memory-resident ARTICLE responses).
        /// Default remains file-backed per-request load/frame.
        /// </summary>
        public const string PreloadEnvironmentVariable = "VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_PRELOAD";

        /// <summary>
        /// When enabled, starts the peer on loopback, writes identity + corpus fingerprint, then blocks
        /// until cancelled. Does not contact real upstreams.
        /// </summary>
        [Fact]
        public async Task Host_local_article_peer_when_enabled()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                return;
            }

            var repoRoot = FindRepositoryRoot();
            var corpus = Environment.GetEnvironmentVariable(CorpusEnvironmentVariable)
                ?? Path.Join(repoRoot, ".artifacts", "Articles");
            if (!Directory.Exists(corpus))
            {
                Assert.Fail($"Corpus missing: {corpus}");
            }

            var port = 11119;
            if (int.TryParse(Environment.GetEnvironmentVariable(PortEnvironmentVariable), out var configuredPort)
                && configuredPort is >= 0 and <= 65535)
            {
                port = configuredPort;
            }

            var maxSessions = 64;
            if (int.TryParse(Environment.GetEnvironmentVariable(MaxSessionsEnvironmentVariable), out var ms)
                && ms > 0)
            {
                maxSessions = ms;
            }

            var preload = string.Equals(
                Environment.GetEnvironmentVariable(PreloadEnvironmentVariable),
                "1",
                StringComparison.Ordinal);

            var outDir = Environment.GetEnvironmentVariable(OutDirectoryEnvironmentVariable)
                ?? Path.Join(
                    repoRoot,
                    ".artifacts",
                    "incident",
                    "smoke-soak",
                    "local-article-peer-benchmark-20261027",
                    "runtime");
            Directory.CreateDirectory(outDir);

            var options = new LocalArticleNntpPeerOptions
            {
                CorpusRoot = corpus,
                Port = port,
                MaxConcurrentSessions = maxSessions,
                Username = Environment.GetEnvironmentVariable(UsernameEnvironmentVariable),
                Password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable),
                PreloadArticlesIntoMemory = preload,
            };

            Console.WriteLine(
                $"LocalArticleNntpPeer starting mode={(preload ? "memory" : "file")} corpus={corpus} "
                + $"preload={preload} (init is outside the measured interval)");
            await using var peer = await LocalArticleNntpPeer.StartAsync(options);
            await LabBinaryIdentity.WriteAsync(Path.Join(outDir, "assembly-identity.json"), CancellationToken.None);
            await peer.Index.WriteManifestAsync(Path.Join(outDir, "corpus-fingerprint.json"), CancellationToken.None);
            var preloadMetrics = peer.PreloadedStore?.Metrics;
            await File.WriteAllTextAsync(
                Path.Join(outDir, "peer-endpoint.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        Host = "127.0.0.1",
                        peer.Port,
                        peer.PeerMode,
                        PreloadArticlesIntoMemory = preload,
                        UseTls = false,
                        StartTlsAdvertised = false,
                        RequiresAuthentication = options.RequiresAuthentication,
                        peer.Index.Count,
                        peer.Index.FingerprintSha256,
                        MaxConcurrentSessions = maxSessions,
                        Preload = preloadMetrics is null
                            ? null
                            : new
                            {
                                preloadMetrics.ArticlesLoaded,
                                preloadMetrics.LogicalBytesLoaded,
                                preloadMetrics.WireBytesLoaded,
                                LoadDurationMs = preloadMetrics.LoadDuration.TotalMilliseconds,
                                preloadMetrics.LoadFailures,
                            },
                        SecurityNote =
                            "Cleartext lab peer. BackFiller provider row must use usessl=n and hostname=127.0.0.1.",
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var metricsPath = Path.Join(outDir, "peer-metrics.json");
            Environment.SetEnvironmentVariable("VECTORNNTPD_LAB_LOCAL_ARTICLE_PEER_METRICS_JSON", metricsPath);

            Console.WriteLine(
                $"LocalArticleNntpPeer listening 127.0.0.1:{peer.Port} mode={peer.PeerMode} corpus={corpus} "
                + $"count={peer.Index.Count} fp={peer.Index.FingerprintSha256} auth={options.RequiresAuthentication}");
            if (preloadMetrics is not null)
            {
                Console.WriteLine(
                    $"Preload articles={preloadMetrics.ArticlesLoaded} logicalBytes={preloadMetrics.LogicalBytesLoaded} "
                    + $"wireBytes={preloadMetrics.WireBytesLoaded} durationMs={preloadMetrics.LoadDuration.TotalMilliseconds:F0} "
                    + $"failures={preloadMetrics.LoadFailures}");
            }

            Console.WriteLine("Press Ctrl+C / cancel test to stop. Do not point this peer at production.");

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                tcs.TrySetResult();
            };

            // Periodic snapshots so ramp/smoke can reconcile without relying on graceful Ctrl+C under redirected IO.
            using var metricsTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var metricsPump = Task.Run(async () =>
            {
                try
                {
                    while (await metricsTimer.WaitForNextTickAsync())
                    {
                        await WritePeerMetricsAsync(metricsPath, peer);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Host shutdown.
                }
            });

            await tcs.Task;
            metricsTimer.Dispose();
            try
            {
                await metricsPump;
            }
            catch (ObjectDisposedException)
            {
                // Timer disposed concurrently with WaitForNextTickAsync.
            }

            await WritePeerMetricsAsync(metricsPath, peer);
            Console.WriteLine($"Wrote peer metrics to {metricsPath}");
        }

        private static Task WritePeerMetricsAsync(string metricsPath, LocalArticleNntpPeer peer)
        {
            var preload = peer.PreloadedStore?.Metrics;
            return File.WriteAllTextAsync(
                metricsPath,
                JsonSerializer.Serialize(
                    new
                    {
                        CapturedUtc = DateTimeOffset.UtcNow,
                        peer.PeerMode,
                        peer.Metrics.AcceptedConnections,
                        peer.Metrics.RejectedConnections,
                        peer.Metrics.ArticleRequests,
                        peer.Metrics.ArticleSuccess,
                        peer.Metrics.ArticleMissing,
                        peer.Metrics.ArticleErrors,
                        peer.Metrics.ProtocolErrors,
                        peer.Metrics.LogicalBytesTransferred,
                        peer.Metrics.WireBytesTransferred,
                        peer.Metrics.OutstandingArticles,
                        peer.Metrics.MaxOutstandingArticles,
                        peer.Metrics.ActiveSessions,
                        peer.Metrics.MaxActiveSessions,
                        Preload = preload is null
                            ? null
                            : new
                            {
                                preload.ArticlesLoaded,
                                preload.LogicalBytesLoaded,
                                preload.WireBytesLoaded,
                                LoadDurationMs = preload.LoadDuration.TotalMilliseconds,
                                preload.LoadFailures,
                            },
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
