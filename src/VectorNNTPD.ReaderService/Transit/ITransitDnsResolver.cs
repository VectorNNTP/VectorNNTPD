using System.Net;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>Resolves Transit AllowFrom hostnames to A/AAAA addresses with optional DNS TTL.</summary>
    public interface ITransitDnsResolver
    {
        /// <summary>Resolves <paramref name="hostname"/> asynchronously.</summary>
        Task<TransitDnsResolveResult> ResolveAsync(string hostname, CancellationToken cancellationToken);
    }

    /// <summary>Classifies a DNS lookup outcome for AllowFrom refresh.</summary>
    public enum TransitDnsOutcome
    {
        /// <summary>One or more addresses were returned.</summary>
        Success = 0,

        /// <summary>
        /// Authoritative empty result (NXDOMAIN or NOERROR with no A/AAAA).
        /// The cached address set may be cleared.
        /// </summary>
        Empty = 1,

        /// <summary>
        /// Temporary failure (timeout, SERVFAIL, transport error).
        /// The last valid address set must be retained.
        /// </summary>
        TransientFailure = 2,
    }

    /// <summary>Result of one hostname lookup.</summary>
    public sealed class TransitDnsResolveResult
    {
        /// <summary>Creates a successful result with addresses and optional TTL.</summary>
        public static TransitDnsResolveResult Success(IReadOnlyList<IPAddress> addresses, TimeSpan? timeToLive) =>
            new(TransitDnsOutcome.Success, addresses, timeToLive);

        /// <summary>Creates an authoritative empty result.</summary>
        public static TransitDnsResolveResult Empty(TimeSpan? timeToLive = null) =>
            new(TransitDnsOutcome.Empty, Array.Empty<IPAddress>(), timeToLive);

        /// <summary>Creates a transient failure that must preserve the last address set.</summary>
        public static TransitDnsResolveResult TransientFailure() =>
            new(TransitDnsOutcome.TransientFailure, Array.Empty<IPAddress>(), timeToLive: null);

        private TransitDnsResolveResult(TransitDnsOutcome outcome, IReadOnlyList<IPAddress> addresses, TimeSpan? timeToLive)
        {
            Outcome = outcome;
            Addresses = addresses;
            TimeToLive = timeToLive;
        }

        /// <summary>Gets the lookup outcome.</summary>
        public TransitDnsOutcome Outcome { get; }

        /// <summary>Gets resolved addresses (empty unless <see cref="TransitDnsOutcome.Success"/>).</summary>
        public IReadOnlyList<IPAddress> Addresses { get; }

        /// <summary>
        /// Gets the DNS TTL when the resolver provided one; otherwise <see langword="null"/>.
        /// A missing TTL uses the 60-second minimum refresh interval as the fallback.
        /// </summary>
        public TimeSpan? TimeToLive { get; }
    }
}
