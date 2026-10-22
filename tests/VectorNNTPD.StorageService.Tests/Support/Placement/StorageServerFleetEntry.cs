using VectorNNTPD.Common.Messaging.Cache;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>Liveness classification for a known StorageServer.</summary>
    public enum StorageServerFleetState
    {
        /// <summary>LastSeen is within the cache-fleet liveness window.</summary>
        Active = 0,

        /// <summary>LastSeen is older than the cache-fleet liveness window.</summary>
        Stale = 1,
    }

    /// <summary>
    /// One in-memory StorageServer fleet entry maintained from cache broadcast advertisements.
    /// </summary>
    /// <param name="ServerId">Numeric StorageServer identity.</param>
    /// <param name="Fqdn">StorageServer FQDN (registry key).</param>
    /// <param name="TotalBytes">Latest advertised total storage bytes.</param>
    /// <param name="UsedBytes">Latest advertised used storage bytes.</param>
    /// <param name="AvailableBytes">Latest advertised available storage bytes.</param>
    /// <param name="LastSeen">UTC time when the latest accepted advertisement or Draining announcement was applied.</param>
    /// <param name="VatpPort">
    /// Advertised TLS VATP port. Null entries stay in the liveness view and are not placement targets.
    /// </param>
    /// <param name="IsDraining">
    /// When <see langword="true"/>, the entry is excluded from active selection even while
    /// <see cref="LastSeen"/> is inside the liveness window.
    /// </param>
    /// <param name="NewestMessageTimestamp">
    /// Newest advertisement or Draining timestamp accepted for this FQDN.
    /// </param>
    public readonly record struct StorageServerFleetEntry(
        int ServerId,
        string Fqdn,
        long TotalBytes,
        long UsedBytes,
        long AvailableBytes,
        DateTimeOffset LastSeen,
        int? VatpPort = null,
        bool IsDraining = false,
        DateTimeOffset NewestMessageTimestamp = default)
    {
        /// <summary>
        /// Classifies this entry relative to <paramref name="utcNow"/> using
        /// <see cref="CacheFleetTopology.LivenessWindow"/>.
        /// </summary>
        public StorageServerFleetState GetState(DateTimeOffset utcNow) =>
            utcNow - LastSeen <= CacheFleetTopology.LivenessWindow
                ? StorageServerFleetState.Active
                : StorageServerFleetState.Stale;

        /// <summary>Returns whether this entry is active at <paramref name="utcNow"/>.</summary>
        public bool IsActive(DateTimeOffset utcNow) => GetState(utcNow) == StorageServerFleetState.Active;
    }
}
