namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Classification of an Article Work quorum-membership health observation.
    /// </summary>
    internal enum ArticleWorkQuorumHealthKind
    {
        /// <summary>Configured membership and online availability both meet the required policy.</summary>
        Healthy = 0,

        /// <summary>Queue exists but has fewer configured members than required.</summary>
        InsufficientMembers = 1,

        /// <summary>Queue has enough configured members, but too few are online.</summary>
        InsufficientOnlineMembers = 2,

        /// <summary>Management reported the queue as missing.</summary>
        QueueMissing = 3,

        /// <summary>Membership could not be verified (Management unavailable, timeout, or malformed response).</summary>
        Unverifiable = 4,
    }

    /// <summary>
    /// Verified (or unverifiable) quorum membership observation for one provider queue.
    /// </summary>
    /// <param name="Kind">Readiness classification.</param>
    /// <param name="RequiredMembers">Configured membership floor that must be met.</param>
    /// <param name="ObservedMembers">Configured quorum members when reported; otherwise <see langword="null"/>.</param>
    /// <param name="OnlineMembers">Online quorum members when reported; otherwise <see langword="null"/>.</param>
    /// <param name="Leader">Leader node name when reported; otherwise <see langword="null"/>.</param>
    /// <param name="Reason">Stable diagnostic text suitable for structured logs. Never credentials.</param>
    internal readonly record struct ArticleWorkQuorumHealthResult(
        ArticleWorkQuorumHealthKind Kind,
        int RequiredMembers,
        int? ObservedMembers,
        int? OnlineMembers,
        string? Leader,
        string Reason);

    /// <summary>
    /// Management-plane probe that verifies Article Work quorum membership independently of AMQP declaration.
    /// </summary>
    internal interface IArticleWorkQuorumHealthProbe
    {
        /// <summary>
        /// Inspects quorum membership for <paramref name="queueName"/> against <paramref name="requiredMembers"/>.
        /// </summary>
        /// <param name="queueName">Provider queue name (for example <c>backfiller.Giganews</c>).</param>
        /// <param name="requiredMembers">Configured membership floor from runtime options.</param>
        /// <param name="cancellationToken">Cancels the Management HTTP request.</param>
        /// <returns>A verified or unverifiable health result. Never throws for broker/HTTP faults.</returns>
        Task<ArticleWorkQuorumHealthResult> InspectAsync(
            string queueName,
            int requiredMembers,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Test and DI stand-in that always reports healthy membership.
    /// </summary>
    /// <remarks>
    /// Production DI registers <see cref="ArticleWorkQuorumManagementHealthProbe"/>. Unit tests that do not
    /// exercise membership use this stand-in so declaration success remains the readiness gate under test.
    /// </remarks>
    internal sealed class AlwaysHealthyArticleWorkQuorumHealthProbe : IArticleWorkQuorumHealthProbe
    {
        /// <summary>Shared instance for tests.</summary>
        internal static AlwaysHealthyArticleWorkQuorumHealthProbe Instance { get; } = new();

        /// <inheritdoc />
        public Task<ArticleWorkQuorumHealthResult> InspectAsync(
            string queueName,
            int requiredMembers,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
            ArgumentOutOfRangeException.ThrowIfLessThan(requiredMembers, 1);
            return Task.FromResult(
                new ArticleWorkQuorumHealthResult(
                    ArticleWorkQuorumHealthKind.Healthy,
                    requiredMembers,
                    ObservedMembers: requiredMembers,
                    OnlineMembers: requiredMembers,
                    Leader: "test-leader",
                    Reason: "Test stand-in reports healthy quorum membership."));
        }
    }
}
