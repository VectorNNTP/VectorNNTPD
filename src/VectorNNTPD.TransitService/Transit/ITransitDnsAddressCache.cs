using System.Net;

namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Thread-safe cache of DNS-resolved AllowFrom addresses.
    /// </summary>
    /// <remarks>
    /// A refresh atomically replaces the address set for one hostname. Transient failures
    /// keep the previous set. Readers never observe a partial hostname update.
    /// </remarks>
    public interface ITransitDnsAddressCache
    {
        /// <summary>Minimum interval between DNS refreshes for one hostname.</summary>
        static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(60);

        /// <summary>Gets the current resolved address set for <paramref name="hostname"/>.</summary>
        /// <remarks>
        /// In-memory only. Must not perform DNS, reverse DNS, or network I/O.
        /// Connection-time identification calls this after refresh has materialized addresses.
        /// </remarks>
        IReadOnlySet<IPAddress> GetResolved(string hostname);

        /// <summary>Resolves every hostname referenced by <paramref name="snapshot"/>.</summary>
        Task RefreshAllAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken);

        /// <summary>Refreshes hostnames whose TTL/minimum interval has elapsed.</summary>
        Task RefreshDueAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the delay until the next hostname is due, or <see cref="Timeout.InfiniteTimeSpan"/>
        /// when no hostnames are configured.
        /// </summary>
        TimeSpan GetDelayUntilNextRefresh(TransitConfigurationSnapshot snapshot);
    }
}
