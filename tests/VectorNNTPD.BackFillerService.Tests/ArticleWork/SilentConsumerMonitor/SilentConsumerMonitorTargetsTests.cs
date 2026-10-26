using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// <see cref="SilentConsumerMonitorTargets.Resolve"/> maps provider catalog entries and backbone usable capacity
    /// to per-queue monitor targets, including <c>ExpectedActive</c> when capacity is positive.
    /// </summary>
    public sealed class SilentConsumerMonitorTargetsTests
    {
        /// <summary>
        /// Providers with positive usable capacity become expected-active queue targets; zero-capacity providers are included
        /// with <c>ExpectedActive</c> false; zero <c>MaxSessions</c> catalog entries are omitted.
        /// </summary>
        [Fact]
        public void Resolves_catalog_queues_without_json_list()
        {
            var catalog = new StaticBackFillerProviderCatalog(
            [
                Provider("Giganews", maxSessions: 2),
                Provider("Eweka", maxSessions: 1),
                Provider("Idle", maxSessions: 0),
            ]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Giganews"] = 2,
                ["Eweka"] = 0,
            });

            var targets = SilentConsumerMonitorTargets.Resolve(catalog, capacity);
            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, t => t.QueueName == "backfiller.giganews" && t.ExpectedActive);
            Assert.Contains(targets, t => t.QueueName == "backfiller.eweka" && !t.ExpectedActive);
            Assert.DoesNotContain(targets, t => t.Backbone.Equals("Idle", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Adding a provider to the catalog yields a new queue target on the next resolve without requiring a JSON queue list.
        /// </summary>
        [Fact]
        public void Catalog_change_adds_new_queue_target()
        {
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Giganews"] = 1,
                ["Eweka"] = 1,
            });

            var first = SilentConsumerMonitorTargets.Resolve(
                new StaticBackFillerProviderCatalog([Provider("Giganews", 1)]),
                capacity);
            Assert.Equal(["backfiller.giganews"], first.Select(t => t.QueueName));

            var second = SilentConsumerMonitorTargets.Resolve(
                new StaticBackFillerProviderCatalog(
                [
                    Provider("Giganews", 1),
                    Provider("Eweka", 1),
                ]),
                capacity);
            Assert.Equal(
                ["backfiller.eweka", "backfiller.giganews"],
                second.Select(t => t.QueueName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        /// <summary>Minimal catalog row for target-resolution tests.</summary>
        /// <param name="backbone">Provider backbone name.</param>
        /// <param name="maxSessions">Catalog <c>MaxSessions</c> cap.</param>
        /// <returns>A <see cref="BackFillerProviderDefinition"/> with fixed host, port, and credentials.</returns>
        private static BackFillerProviderDefinition Provider(string backbone, int maxSessions) =>
            new(backbone, "host", 563, true, "u", "p", MinSessions: 0, MaxSessions: maxSessions);
    }
}
