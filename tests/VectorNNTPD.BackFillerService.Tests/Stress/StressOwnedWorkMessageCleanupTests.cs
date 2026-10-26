using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>Deterministic coverage for StressRunId header matching used by owned work-queue cleanup.</summary>
    public sealed class StressOwnedWorkMessageCleanupTests
    {
        /// <summary>String header values match the owned run id.</summary>
        [Fact]
        public void IsOwnedByRun_matches_string_header()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressOwnedWorkMessageCleanup.StressRunIdHeader] = "run-a",
            };
            Assert.True(StressOwnedWorkMessageCleanup.IsOwnedByRun(headers, "run-a"));
            Assert.False(StressOwnedWorkMessageCleanup.IsOwnedByRun(headers, "run-b"));
        }

        /// <summary>UTF-8 byte[] headers (RabbitMQ.Client default) match the owned run id.</summary>
        [Fact]
        public void IsOwnedByRun_matches_utf8_byte_header()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressOwnedWorkMessageCleanup.StressRunIdHeader] = Encoding.UTF8.GetBytes("run-bytes"),
            };
            Assert.True(StressOwnedWorkMessageCleanup.IsOwnedByRun(headers, "run-bytes"));
            Assert.False(StressOwnedWorkMessageCleanup.IsOwnedByRun(headers, "other"));
        }

        /// <summary>Missing, blank, empty-byte, and non-string header values are never treated as owned.</summary>
        [Fact]
        public void IsOwnedByRun_rejects_missing_blank_empty_bytes_and_unknown_types()
        {
            Assert.False(StressOwnedWorkMessageCleanup.IsOwnedByRun(null, "run-a"));
            Assert.False(StressOwnedWorkMessageCleanup.IsOwnedByRun(new Dictionary<string, object?>(), "run-a"));
            Assert.False(
                StressOwnedWorkMessageCleanup.IsOwnedByRun(
                    new Dictionary<string, object?>
                    {
                        [StressOwnedWorkMessageCleanup.StressRunIdHeader] = "   ",
                    },
                    "run-a"));
            Assert.False(
                StressOwnedWorkMessageCleanup.IsOwnedByRun(
                    new Dictionary<string, object?>
                    {
                        [StressOwnedWorkMessageCleanup.StressRunIdHeader] = Array.Empty<byte>(),
                    },
                    "run-a"));
            Assert.False(
                StressOwnedWorkMessageCleanup.IsOwnedByRun(
                    new Dictionary<string, object?>
                    {
                        [StressOwnedWorkMessageCleanup.StressRunIdHeader] = 42,
                    },
                    "run-a"));
            Assert.Null(
                StressOwnedWorkMessageCleanup.TryGetStressRunId(
                    new Dictionary<string, object?>
                    {
                        [StressOwnedWorkMessageCleanup.StressRunIdHeader] = 42,
                    }));
        }

        /// <summary>TryGetStressRunId trims string values.</summary>
        [Fact]
        public void TryGetStressRunId_trims_string()
        {
            var headers = new Dictionary<string, object?>
            {
                [StressOwnedWorkMessageCleanup.StressRunIdHeader] = "  padded-id  ",
            };
            Assert.Equal("padded-id", StressOwnedWorkMessageCleanup.TryGetStressRunId(headers));
        }

        /// <summary>CopyProperties preserves correlation, reply-to, and StressRunId for foreign republish.</summary>
        [Fact]
        public void CopyProperties_preserves_identity_and_stress_run_header()
        {
            var source = ArticleWorkStressClient.CreateRequestProperties(
                correlationId: "corr-1",
                replyTo: "reply-q",
                requestId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                stressRunId: "foreign-run");
            var copy = StressOwnedWorkMessageCleanup.CopyProperties(source);
            Assert.Equal("corr-1", copy.CorrelationId);
            Assert.Equal("reply-q", copy.ReplyTo);
            Assert.Equal(source.ContentType, copy.ContentType);
            Assert.Equal(source.DeliveryMode, copy.DeliveryMode);
            Assert.Equal(
                "foreign-run",
                StressOwnedWorkMessageCleanup.TryGetStressRunId(copy.Headers));
            Assert.NotSame(source.Headers, copy.Headers);
        }
    }
}
