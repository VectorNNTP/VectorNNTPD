using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Reports managed bytes allocated by one valid Article Work parse.
    /// The counts are diagnostic. They are not a correctness contract.
    /// </summary>
    public sealed class ArticleWorkRequestParserAllocationProbe
    {
        /// <summary>Writes per-parse allocation counts for the reader path and a JsonDocument comparison.</summary>
        [Fact]
        public void Valid_payload_allocation_is_reported()
        {
            var delivery = ArticleWorkTestDeliveries.Canonical();
            const int warmup = 100;
            const int iterations = 1000;
            for (var i = 0; i < warmup; i++)
            {
                _ = ArticleWorkRequestParser.Parse(delivery, "Giganews", 1024);
                _ = ParseWithJsonDocument(delivery.Body);
            }

            var readerBytes = Measure(iterations, () => ArticleWorkRequestParser.Parse(delivery, "Giganews", 1024).Request!.MessageId.Length);
            var documentBytes = Measure(iterations, () => ParseWithJsonDocument(delivery.Body).Request!.MessageId.Length);

            var readerPerParse = readerBytes / (double)iterations;
            var documentPerParse = documentBytes / (double)iterations;
            Console.WriteLine(
                $"ArticleWorkRequestParser.Parse iterations={iterations} allocated={readerBytes} perParse={readerPerParse:F1}");
            Console.WriteLine(
                $"JsonDocument comparison iterations={iterations} allocated={documentBytes} perParse={documentPerParse:F1}");
            Console.WriteLine(
                "Measured after warmup on this thread. Both sides return ArticleWorkParseResult for the canonical payload. The JsonDocument side also copies requestId. It does not run the former parser.");

            Assert.True(ArticleWorkRequestParser.Parse(delivery, "Giganews", 1024).IsValid);
        }

        /// <summary>Builds the same request through <see cref="JsonDocument"/> so the probe can compare allocator traffic.</summary>
        /// <param name="json">Canonical UTF-8 payload.</param>
        /// <returns>The parse result constructed from the DOM.</returns>
        private static ArticleWorkParseResult ParseWithJsonDocument(ReadOnlyMemory<byte> json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var requestId = Guid.Parse(root.GetProperty("requestId").GetString()!);
            var messageId = root.GetProperty("messageId").GetString()!;
            var backbone = root.GetProperty("backbone").GetString()!;
            return ArticleWorkParseResult.Valid(new ArticleWorkRequest(1, requestId, messageId, backbone));
        }

        /// <summary>Returns bytes allocated on this thread by <paramref name="iterations"/> calls.</summary>
        /// <param name="iterations">Number of measured calls.</param>
        /// <param name="action">Work to measure. Its result is consumed so the call stays live.</param>
        /// <returns>Allocated bytes for the whole loop.</returns>
        private static long Measure(int iterations, Func<int> action)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var sink = 0;
            for (var i = 0; i < iterations; i++)
            {
                sink += action();
            }

            if (sink <= 0)
            {
                throw new InvalidOperationException("Measured work produced no output.");
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
