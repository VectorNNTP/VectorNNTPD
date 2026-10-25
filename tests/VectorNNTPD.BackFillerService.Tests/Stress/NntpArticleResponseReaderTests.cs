using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>Unit tests for NNTP multiline read, failure classification, and provider entity naming.</summary>
    public sealed class NntpArticleResponseReaderTests
    {
        /// <summary>220 multiline path counts wire vs logical bytes after dot-stuffing.</summary>
        [Fact]
        public async Task ReadArticleExchange_counts_destuffed_payload_without_framing()
        {
            // 220 line + stuffed body line "..hi\r\n" (logical ".hi\r\n") + terminator
            var wire = "220 0 <id@host> article follows\r\n"u8.ToArray()
                .Concat("..hi\r\n"u8.ToArray())
                .Concat(".\r\n"u8.ToArray())
                .ToArray();
            await using var stream = new MemoryStream(wire);
            var result = await NntpArticleResponseReader.ReadArticleExchangeAsync(
                stream,
                new byte[512],
                new byte[1024],
                CancellationToken.None);

            Assert.Equal(NntpResponseKind.ArticleDelivered, result.Kind);
            Assert.Equal(220, result.StatusCode);
            Assert.Equal(6, result.WireArticleBytes); // "..hi\r\n"
            Assert.Equal(5, result.LogicalArticleBytes); // ".hi\r\n"
        }

        /// <summary>430 response yields zero payload bytes.</summary>
        [Fact]
        public async Task ReadArticleExchange_not_found_has_zero_payload()
        {
            await using var stream = new MemoryStream("430 No Such Article Found\r\n"u8.ToArray());
            var result = await NntpArticleResponseReader.ReadArticleExchangeAsync(
                stream,
                new byte[512],
                new byte[1024],
                CancellationToken.None);
            Assert.Equal(NntpResponseKind.NotFound, result.Kind);
            Assert.Equal(0, result.LogicalArticleBytes);
            Assert.Equal(0, result.WireArticleBytes);
        }

        /// <summary>Body read without terminator yields <see cref="NntpResponseKind.Incomplete"/>.</summary>
        [Fact]
        public async Task ConsumeArticleBody_incomplete_when_peer_closes()
        {
            await using var stream = new MemoryStream("line\r\n"u8.ToArray());
            var result = await NntpArticleResponseReader.ConsumeArticleBodyAsync(
                stream,
                new byte[1024],
                CancellationToken.None);
            Assert.Equal(NntpResponseKind.Incomplete, result.Kind);
            Assert.True(result.WireArticleBytes > 0);
        }

        /// <summary>ClassifyFailure maps work, NNTP, timeout, and disconnect inputs to <see cref="StressFailureClass"/>.</summary>
        [Theory]
        [InlineData(ArticleWorkStressOutcome.ArticleNotFound, null, false, false, StressFailureClass.MissingOrExpired)]
        [InlineData(ArticleWorkStressOutcome.ProviderFailure, null, false, false, StressFailureClass.UpstreamOrValidation)]
        [InlineData(ArticleWorkStressOutcome.UnexpectedFailure, null, false, false, StressFailureClass.Other)]
        [InlineData(ArticleWorkStressOutcome.RetentionRejected, null, false, false, StressFailureClass.UpstreamOrValidation)]
        [InlineData(ArticleWorkStressOutcome.Success, NntpResponseKind.NotFound, false, false, StressFailureClass.MissingOrExpired)]
        [InlineData(ArticleWorkStressOutcome.Success, NntpResponseKind.ArticleDelivered, false, false, StressFailureClass.None)]
        [InlineData(ArticleWorkStressOutcome.NoResponse, null, true, false, StressFailureClass.Timeout)]
        [InlineData(ArticleWorkStressOutcome.BrokerError, null, false, false, StressFailureClass.RabbitMqError)]
        public void ClassifyFailure_maps_buckets(
            ArticleWorkStressOutcome work,
            NntpResponseKind? nntp,
            bool timedOut,
            bool disconnected,
            StressFailureClass expected)
        {
            var actual = NntpArticleResponseReader.ClassifyFailure(work, nntp, timedOut, disconnected);
            Assert.Equal(expected, actual);
        }

        /// <summary>ComposeProviderEntity lowercases backbone in the backfiller entity name.</summary>
        [Fact]
        public void ComposeProviderEntity_normalizes_backbone()
        {
            Assert.Equal("backfiller.giganews", ArticleWorkStressClient.ComposeProviderEntity("Giganews"));
            Assert.Equal("backfiller.eweka", ArticleWorkStressClient.ComposeProviderEntity("Eweka"));
        }
    }
}
