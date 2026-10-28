namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Optional compatibility checks against <c>.artifacts/Articles</c> when present.
    /// Skips when the corpus directory is missing (CI without local artifacts).
    /// </summary>
    public sealed class LocalArticlePeerCorpusTests
    {
        private static readonly string CorpusRoot = Path.GetFullPath(
            Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".artifacts", "Articles"));

        [Fact]
        public async Task Real_corpus_indexes_and_serves_one_article_when_present()
        {
            if (!Directory.Exists(CorpusRoot))
            {
                return;
            }

            await using var peer = await LocalArticleNntpPeer.StartAsync(
                new LocalArticleNntpPeerOptions
                {
                    CorpusRoot = CorpusRoot,
                    Port = 0,
                    MaxConcurrentSessions = 4,
                });

            Assert.True(peer.Index.Count > 0, "Expected corpus Message-IDs");
            var messageId = peer.Index.MessageIds.First();
            Assert.True(peer.Index.TryResolve(messageId, out var path));
            Assert.True(File.Exists(path));

            var provider = new VectorNNTPD.BackFillerService.Nntp.BackFillerProviderDefinition(
                "LabLocal", "127.0.0.1", peer.Port, false, null, null, 0, 1);
            await using var session = new VectorNNTPD.BackFillerService.Nntp.NntpProviderSession(
                provider,
                VectorNNTPD.BackFillerService.Nntp.NntpSessionOptions.Default,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Null(
                await session.ConnectAsync(
                    new VectorNNTPD.BackFillerService.Nntp.TcpNntpTransportFactory(),
                    CancellationToken.None));
            using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);
            Assert.Equal(VectorNNTPD.BackFillerService.Nntp.ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.True(result.Article!.Memory.Length > 0);

            var outDir = Path.GetFullPath(
                Path.Join(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    ".artifacts",
                    "incident",
                    "smoke-soak",
                    "local-article-peer-benchmark-20261027"));
            Directory.CreateDirectory(outDir);
            await peer.Index.WriteManifestAsync(
                Path.Join(outDir, "corpus-fingerprint.json"),
                CancellationToken.None);
            await LabBinaryIdentity.WriteAsync(
                Path.Join(outDir, "assembly-identity-correctness.json"),
                CancellationToken.None);
        }
    }
}
