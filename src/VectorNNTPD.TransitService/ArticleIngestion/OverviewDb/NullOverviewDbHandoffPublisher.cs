namespace VectorNNTPD.TransitService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// Test-only OverviewDB publisher that completes without contacting a broker.
    /// Production DI registers <see cref="OverviewDbHandoffPublisher"/> instead.
    /// </summary>
    internal sealed class NullOverviewDbHandoffPublisher : IOverviewDbHandoffPublisher
    {
        /// <summary>Shared succeeding instance used when tests construct the worker directly.</summary>
        public static NullOverviewDbHandoffPublisher Instance { get; } = new();

        private NullOverviewDbHandoffPublisher()
        {
        }

        /// <inheritdoc />
        public Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public int OutstandingCount => 0;

        /// <inheritdoc />
        public bool TryDequeuePublishFailure(out OverviewDbWorkItem item)
        {
            item = null!;
            return false;
        }

        /// <inheritdoc />
        public void AbandonOutstanding()
        {
        }
    }
}
