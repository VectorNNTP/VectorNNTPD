using System.Collections.Concurrent;
using VectorNNTPD.Common.Messaging.Cache;

namespace VectorNNTPD.TransitService.Storage
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
    /// When <see langword="true"/>, the entry is excluded from <see cref="IStorageServerRegistry.GetActive"/>
    /// even while <see cref="LastSeen"/> is inside the liveness window. Liveness expiry does not clear it.
    /// </param>
    /// <param name="NewestMessageTimestamp">
    /// Newest advertisement or Draining timestamp accepted for this FQDN. An older timestamp is not applied.
    /// This orders one advertisement stream. It is not a process identity.
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

    /// <summary>
    /// In-memory authoritative view of currently known StorageServers for future placement decisions.
    /// </summary>
    /// <remarks>
    /// Hot-path lookups must use this registry only. Do not call RabbitMQ, disk, or network from
    /// selection logic merely to learn which StorageServers are alive.
    /// A periodic advertisement is the readiness, liveness, capacity, and VATP endpoint signal.
    /// Draining removes the FQDN from <see cref="GetActive"/> immediately and does not change
    /// capacity bytes. There is no separate Ready announcement and no process epoch.
    /// </remarks>
    public interface IStorageServerRegistry
    {
        /// <summary>
        /// Applies a parsed advertisement, creating or updating the FQDN-keyed entry and refreshing
        /// <see cref="StorageServerFleetEntry.LastSeen"/> when the advertisement is accepted.
        /// </summary>
        /// <param name="advertisement">Validated advertisement payload.</param>
        /// <param name="receivedAtUtc">UTC receive time used as LastSeen when the advertisement is accepted.</param>
        /// <remarks>
        /// An advertisement older than <see cref="StorageServerFleetEntry.NewestMessageTimestamp"/> is ignored.
        /// While the entry is draining, only a strictly newer advertisement is accepted, and that advertisement
        /// clears draining. A restarted process whose timestamps are not strictly newer than the accepted
        /// Draining timestamp cannot be distinguished from a delayed advertisement and stays out of
        /// <see cref="GetActive"/>. Receive time is not compared with the message timestamp.
        /// </remarks>
        void ApplyAdvertisement(StorageServerAdvertisement advertisement, DateTimeOffset receivedAtUtc);

        /// <summary>Attempts to read the entry for <paramref name="fqdn"/>.</summary>
        bool TryGet(string fqdn, out StorageServerFleetEntry entry);

        /// <summary>Returns a snapshot of all known entries (active and stale).</summary>
        IReadOnlyList<StorageServerFleetEntry> Snapshot();

        /// <summary>
        /// Returns entries that are inside the liveness window and not draining at <paramref name="utcNow"/>.
        /// </summary>
        IReadOnlyList<StorageServerFleetEntry> GetActive(DateTimeOffset utcNow);

        /// <summary>
        /// Applies a Draining announcement for the announcement FQDN.
        /// </summary>
        /// <param name="announcement">Validated Draining announcement.</param>
        /// <param name="receivedAtUtc">UTC receive time used as LastSeen when the announcement is accepted.</param>
        /// <remarks>
        /// Draining removes the FQDN from <see cref="GetActive"/> immediately. Capacity bytes already recorded
        /// for the FQDN are kept. A Draining timestamp older than
        /// <see cref="StorageServerFleetEntry.NewestMessageTimestamp"/> is ignored, so a delayed Draining
        /// announcement does not override a newer advertisement. Liveness expiry does not clear Draining.
        /// </remarks>
        void ApplyLifecycle(StorageServerLifecycleAnnouncement announcement, DateTimeOffset receivedAtUtc);
    }

    /// <summary>Thread-safe in-memory StorageServer fleet registry.</summary>
    public sealed class StorageServerRegistry : IStorageServerRegistry
    {
        private readonly ConcurrentDictionary<string, StorageServerFleetEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public void ApplyAdvertisement(StorageServerAdvertisement advertisement, DateTimeOffset receivedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(advertisement);
            ArgumentException.ThrowIfNullOrWhiteSpace(advertisement.Fqdn);

            var key = advertisement.Fqdn.Trim();
            var receivedAt = receivedAtUtc.ToUniversalTime();
            _entries.AddOrUpdate(
                key,
                _ => EntryFromAdvertisement(advertisement, key, receivedAt),
                (_, existing) => MergeAdvertisement(existing, advertisement, key, receivedAt));
        }

        /// <inheritdoc />
        public void ApplyLifecycle(StorageServerLifecycleAnnouncement announcement, DateTimeOffset receivedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(announcement);
            ArgumentException.ThrowIfNullOrWhiteSpace(announcement.Fqdn);
            if (announcement.State != StorageServerLifecycleState.Draining)
            {
                return;
            }

            var key = announcement.Fqdn.Trim();
            var receivedAt = receivedAtUtc.ToUniversalTime();
            _entries.AddOrUpdate(
                key,
                _ => EntryFromDraining(announcement, key, receivedAt),
                (_, existing) => MergeDraining(existing, announcement, key, receivedAt));
        }

        private static StorageServerFleetEntry EntryFromAdvertisement(
            StorageServerAdvertisement advertisement,
            string key,
            DateTimeOffset receivedAt)
        {
            var timestamp = advertisement.Timestamp.ToUniversalTime();
            return new StorageServerFleetEntry(
                advertisement.ServerId,
                key,
                advertisement.TotalBytes,
                advertisement.UsedBytes,
                advertisement.AvailableBytes,
                receivedAt,
                advertisement.VatpPort,
                IsDraining: false,
                timestamp);
        }

        private static StorageServerFleetEntry EntryFromDraining(
            StorageServerLifecycleAnnouncement announcement,
            string key,
            DateTimeOffset receivedAt)
        {
            var timestamp = announcement.Timestamp.ToUniversalTime();
            return new StorageServerFleetEntry(
                announcement.ServerId,
                key,
                TotalBytes: 0,
                UsedBytes: 0,
                AvailableBytes: 0,
                receivedAt,
                announcement.VatpPort,
                IsDraining: true,
                timestamp);
        }

        private static StorageServerFleetEntry MergeAdvertisement(
            StorageServerFleetEntry existing,
            StorageServerAdvertisement advertisement,
            string key,
            DateTimeOffset receivedAt)
        {
            var timestamp = advertisement.Timestamp.ToUniversalTime();
            if (timestamp < existing.NewestMessageTimestamp)
            {
                return existing;
            }

            if (existing.IsDraining && timestamp <= existing.NewestMessageTimestamp)
            {
                return existing;
            }

            return new StorageServerFleetEntry(
                advertisement.ServerId,
                key,
                advertisement.TotalBytes,
                advertisement.UsedBytes,
                advertisement.AvailableBytes,
                receivedAt,
                advertisement.VatpPort,
                IsDraining: false,
                timestamp);
        }

        private static StorageServerFleetEntry MergeDraining(
            StorageServerFleetEntry existing,
            StorageServerLifecycleAnnouncement announcement,
            string key,
            DateTimeOffset receivedAt)
        {
            var timestamp = announcement.Timestamp.ToUniversalTime();
            if (timestamp < existing.NewestMessageTimestamp)
            {
                return existing;
            }

            return existing with
            {
                ServerId = announcement.ServerId,
                Fqdn = key,
                LastSeen = receivedAt,
                VatpPort = announcement.VatpPort,
                IsDraining = true,
                NewestMessageTimestamp = timestamp,
            };
        }

        /// <inheritdoc />
        public bool TryGet(string fqdn, out StorageServerFleetEntry entry)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            return _entries.TryGetValue(fqdn.Trim(), out entry);
        }

        /// <inheritdoc />
        public IReadOnlyList<StorageServerFleetEntry> Snapshot() => [.. _entries.Values];

        /// <inheritdoc />
        public IReadOnlyList<StorageServerFleetEntry> GetActive(DateTimeOffset utcNow)
        {
            var now = utcNow.ToUniversalTime();
            List<StorageServerFleetEntry>? active = null;
            foreach (var entry in _entries.Values)
            {
                if (!entry.IsActive(now) || entry.IsDraining)
                {
                    continue;
                }

                active ??= [];
                active.Add(entry);
            }

            return active is null ? Array.Empty<StorageServerFleetEntry>() : active;
        }
    }
}
