using VectorNNTPD.BackFillerService.Nntp;
using Microsoft.Extensions.Logging.Abstractions;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class LocalUpstreamIsolationGateTests
    {
        [Fact]
        public void Rejects_missing_require_flag()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirements() with
                {
                    RequireLocalUpstreamFlag = false,
                }));
            Assert.Contains("Missing local-upstream requirement", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_missing_host()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirements() with { DeclaredHost = null }));
            Assert.Contains("Missing host", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_missing_port()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirements() with { DeclaredPort = null }));
            Assert.Contains("Missing or invalid port", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_localhost_and_ipv6_declared_host()
        {
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(
                    ValidRequirements() with { DeclaredHost = "localhost" }));
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(
                    ValidRequirements() with { DeclaredHost = "::1" }));
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(
                    ValidRequirements() with { DeclaredHost = "8.8.8.8" }));
        }

        [Fact]
        public void Rejects_mismatched_peer_port_in_snapshot()
        {
            var snapshot = ValidSnapshot(port: 11119);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(snapshot) with
                {
                    DeclaredPort = 22222,
                }));
            Assert.Contains("does not match peer port", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_external_provider_among_multiple_accounts()
        {
            var snapshot = new UpstreamProviderSnapshot
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                ServerId = 1,
                Source = "unit-test",
                PoolRefreshEvidence = "synthetic",
                Providers =
                [
                    new UpstreamProviderEndpoint
                    {
                        Backbone = "Giganews",
                        Host = "127.0.0.1",
                        Port = 11119,
                        UseTls = false,
                    },
                    new UpstreamProviderEndpoint
                    {
                        Backbone = "Eweka",
                        Host = "news.example.com",
                        Port = 563,
                        UseTls = true,
                    },
                ],
            };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(snapshot)));
            Assert.Contains("Eweka", ex.Message, StringComparison.Ordinal);
            Assert.Contains("not 127.0.0.1", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_missing_snapshot_as_uninspectable()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(null)));
            Assert.Contains("uninspectable", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rejects_stale_or_unverified_pool_snapshot()
        {
            var stale = ValidSnapshot() with
            {
                CapturedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            };
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(stale)));

            var unverified = ValidSnapshot() with { PoolRefreshEvidence = "   " };
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(unverified)));

            var unknownSource = ValidSnapshot() with { Source = "unknown" };
            Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirementsWithSnapshot(unknownSource)));
        }

        [Fact]
        public void Rejects_unavailable_local_peer_without_external_fallback()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalUpstreamIsolationGate.EnsureReadyOrThrow(
                    ValidRequirements() with { PeerReachable = static (_, _) => false }));
            Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no external upstream fallback", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Accepts_fully_verified_local_only_configuration()
        {
            var endpoint = LocalUpstreamIsolationGate.EnsureReadyOrThrow(ValidRequirements());
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Equal(11119, endpoint.Port);
        }

        [Fact]
        public async Task Allowlist_transport_blocks_external_and_allows_local_peer()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vnntp-allowlist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var messageId = $"<allow-{Guid.NewGuid():N}@lab.test>";
            await File.WriteAllTextAsync(
                Path.Combine(dir, "a1"),
                $"Message-ID: {messageId}\r\nFrom: a@b\r\n\r\nbody\r\n");

            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions { CorpusRoot = dir, Port = 0 });

            var allowlist = new LoopbackAllowlistNntpTransportFactory(
                new TcpNntpTransportFactory(),
                "127.0.0.1",
                peer.Port);

            // NntpProviderSession classifies transport exceptions as non-reusable provider failure (no throw).
            var bad = new BackFillerProviderDefinition(
                "Giganews", "news.example.com", 563, true, null, null, 0, 1);
            await using (var session = new NntpProviderSession(
                             bad, NntpSessionOptions.Default, NullLogger.Instance))
            {
                var refused = await session.ConnectAsync(allowlist, CancellationToken.None);
                Assert.NotNull(refused);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, refused!.Kind);
                Assert.False(refused.SessionReusable);
            }

            var good = new BackFillerProviderDefinition(
                "Giganews", "127.0.0.1", peer.Port, false, null, null, 0, 1);
            await using var okSession = new NntpProviderSession(
                good, NntpSessionOptions.Default, NullLogger.Instance);
            Assert.Null(await okSession.ConnectAsync(allowlist, CancellationToken.None));
            using var result = await okSession.DownloadArticleAsync(messageId, CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        }

        private static LocalUpstreamIsolationGate.Requirements ValidRequirements() =>
            ValidRequirementsWithSnapshot(ValidSnapshot());

        private static LocalUpstreamIsolationGate.Requirements ValidRequirementsWithSnapshot(
            UpstreamProviderSnapshot? snapshot) =>
            new()
            {
                RequireLocalUpstreamFlag = true,
                DeclaredHost = "127.0.0.1",
                DeclaredPort = 11119,
                Snapshot = snapshot,
                MaxSnapshotAge = TimeSpan.FromMinutes(15),
                NowUtc = DateTimeOffset.UtcNow,
                PeerReachable = static (_, _) => true,
            };

        private static UpstreamProviderSnapshot ValidSnapshot(int port = 11119) =>
            new()
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                ServerId = 1,
                Source = "unit-test",
                PoolRefreshEvidence = "synthetic-refresh-ok",
                Providers =
                [
                    new UpstreamProviderEndpoint
                    {
                        Backbone = "Giganews",
                        Host = "127.0.0.1",
                        Port = port,
                        UseTls = false,
                    },
                    new UpstreamProviderEndpoint
                    {
                        Backbone = "Eweka",
                        Host = "127.0.0.1",
                        Port = port,
                        UseTls = false,
                    },
                ],
            };
    }
}
