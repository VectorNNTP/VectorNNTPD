using System.Diagnostics;
using System.Text;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using Xunit.Abstractions;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Proves a large retained article can be claimed without copying ArtData.
    /// A single-thread duration is written for diagnosis and is not a pass condition.
    /// This is not a before/after measurement of the previous retention binary, and it does not
    /// reconstruct a lock-held scan. That reconstruction saturated a 4-CPU CI runner and starved
    /// other retention tests.
    /// </summary>
    public sealed class ArticleRetentionClaimMeasurementTests
    {
        /// <summary>Approximate retained body size from issue 153.</summary>
        private const int TargetBodyBytes = 740 * 1024;

        /// <summary>Captures the diagnostic duration.</summary>
        private readonly ITestOutputHelper _output;

        /// <summary>Creates the measurement fixture.</summary>
        /// <param name="output">xUnit output for the diagnostic duration.</param>
        public ArticleRetentionClaimMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A ~740 KiB article is claimed and released on one thread. Allocation stays far below the article size.
        /// </summary>
        [Fact]
        public void Large_article_claims_do_not_copy_the_article()
        {
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero)),
                maxBytes: 2 * 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<m0@measure.test>", BuildBody(TargetBodyBytes));
            var id = Encoding.ASCII.GetBytes(prepared.MessageId);
            var artSize = prepared.Record.ArtSize;
            var miss = Encoding.ASCII.GetBytes("<missing@measure.test>");

            var claimed = authority.TryClaimForDelivery(id);
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            claimed.Claim.Dispose();

            const int iterations = 32;
            var beforeHits = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                var hit = authority.TryClaimForDelivery(id);
                Assert.Equal(ArticleDeliveryClaimKind.Claimed, hit.Kind);
                hit.Claim!.Dispose();
            }

            var hitBytes = GC.GetAllocatedBytesForCurrentThread() - beforeHits;
            var elapsedUs = (Stopwatch.GetTimestamp() - started) * 1_000_000d / Stopwatch.Frequency;
            var beforeMisses = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(miss).Kind);
            }

            var missBytes = GC.GetAllocatedBytesForCurrentThread() - beforeMisses;
            _output.WriteLine(
                $"artSize={artSize} iterations={iterations} hitBytes={hitBytes} missBytes={missBytes} "
                + $"singleThreadHitUs={elapsedUs:F0}");

            Assert.True(hitBytes < (long)iterations * artSize / 8);
            Assert.True(missBytes < (long)iterations * artSize / 8);
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>Builds a CRLF body of about <paramref name="targetBytes"/> without exceeding the 1024-byte line limit.</summary>
        private static string BuildBody(int targetBytes)
        {
            var line = new string('x', 998) + "\r\n";
            var builder = new StringBuilder(targetBytes + line.Length);
            while (builder.Length < targetBytes)
            {
                builder.Append(line);
            }

            return builder.ToString();
        }
    }
}
