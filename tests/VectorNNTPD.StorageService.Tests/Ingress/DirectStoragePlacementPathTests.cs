using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Tests.Ingress
{

    /// <summary>
    /// Direct VATP placement does not pass through NNTPD. The pre-ingestion record is normalized
    /// with the organizational tracker as the parser identity, which Normalize does not emit as an
    /// application hop. StorageServer's normal traverse adds the cache hop.
    /// </summary>
    public sealed class DirectStoragePlacementPathTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private const string Tracker = "news.usenet.ninja";
        private const string SourceHop = "peer.example";

        [Fact]
        public void Normalize_then_storage_traverse_keeps_source_tracker_and_cache_once()
        {
            var source = Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n" +
                "Date: Tue, 13 Oct 2026 21:16:00 +0000\r\n" +
                "Message-ID: <soak-path@soak.invalid>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: soak@soak.invalid\r\n" +
                "Subject: soak\r\n" +
                "\r\n" +
                "x\r\n");
            Assert.Equal("Path: peer.example", HeaderLine(source, "Path"));

            // Parser identity is the organizational tracker. Normalize does not write it as an application hop.
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(Tracker),
                source,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            Assert.Equal(ArticleParseStatus.CanonicalV1, created.Record.ParseStatus);

            var beforePlacement = HeaderLine(created.Record.ArtData.Span, "Path");
            Assert.Equal($"Path: {Tracker}!{SourceHop}", beforePlacement);
            Assert.DoesNotContain("nntpd01.usenet.ninja", beforePlacement, StringComparison.Ordinal);
            Assert.DoesNotContain(CacheFqdn, beforePlacement, StringComparison.Ordinal);
            Assert.Equal(1, CountHop(beforePlacement, Tracker));
            Assert.Equal(1, CountHop(beforePlacement, SourceHop));
            Assert.EndsWith($"!{SourceHop}", beforePlacement, StringComparison.Ordinal);

            var processor = new AcceptedArticlePathProcessor(CacheFqdn, NullLogger.Instance);
            var inbound = new AcceptedArticle(
                created.Record.ArtId,
                created.Record.ArtHash,
                created.Record.ArtSize,
                created.Record.ArtData);
            Assert.True(processor.TryRewrite(inbound, out var stored));
            var storedPath = HeaderLine(stored.Span, "Path");
            Assert.Equal($"Path: {CacheFqdn}!{Tracker}!{SourceHop}", storedPath);
            Assert.Equal(1, CountHop(storedPath, CacheFqdn));
            Assert.Equal(1, CountHop(storedPath, Tracker));
            Assert.Equal(1, CountHop(storedPath, SourceHop));
            Assert.Equal(0, CountHop(storedPath, "nntpd01.usenet.ninja"));
            Assert.EndsWith($"!{SourceHop}", storedPath, StringComparison.Ordinal);
            Assert.Equal(created.Record.ArtId, ArticleId.FromMessageId(MessageId(stored.Span)));

            var renormalized = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(Tracker),
                stored,
                ArticlePathMode.Normalize);
            Assert.True(renormalized.IsAccepted, renormalized.MaterializeFailure.ToString());
            Assert.Equal(storedPath, HeaderLine(renormalized.Record.ArtData.Span, "Path"));
        }

        private static ReadOnlySpan<byte> MessageId(ReadOnlySpan<byte> article)
        {
            var line = HeaderLine(article, "Message-ID");
            Assert.StartsWith("Message-ID: ", line, StringComparison.Ordinal);
            return Encoding.ASCII.GetBytes(line["Message-ID: ".Length..]);
        }

        private static string HeaderLine(ReadOnlySpan<byte> article, string name)
        {
            var text = Encoding.ASCII.GetString(article);
            var prefix = name + ": ";
            var start = text.IndexOf(prefix, StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
            Assert.True(end > start);
            return text[start..end];
        }

        private static int CountHop(string path, string hop)
        {
            var value = path.StartsWith("Path: ", StringComparison.Ordinal) ? path["Path: ".Length..] : path;
            var count = 0;
            foreach (var token in value.Split('!'))
            {
                if (string.Equals(token, hop, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
