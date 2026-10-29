using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Session.SpeedTest
{
    /// <summary>
    /// Snapshot-backed peer lookup and bounded SPEEDTEST concurrency.
    /// </summary>
    /// <remarks>
    /// Each resolve reads <see cref="TransitConfigurationStore.Current"/>. Limits are read from
    /// <see cref="IOptionsMonitor{TOptions}"/> at acquire time. In-flight tests keep the limits
    /// captured on the lease. This type does not initiate outbound Transit connections.
    /// </remarks>
    public sealed class SpeedTestCoordinator : ISpeedTestCoordinator
    {
        private readonly TransitConfigurationStore _store;
        private readonly IOptionsMonitor<NntpdOptions> _options;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, int> _perPeer = new(StringComparer.Ordinal);
        private int _global;

        /// <summary>Creates a coordinator bound to the live Transit store and NNTPD options.</summary>
        public SpeedTestCoordinator(TransitConfigurationStore store, IOptionsMonitor<NntpdOptions> options)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(options);
            _store = store;
            _options = options;
        }

        /// <summary>Creates a coordinator for tests with a fixed options snapshot.</summary>
        public static SpeedTestCoordinator Create(
            TransitConfigurationStore store,
            NntpdOptions? options = null) =>
            new(store, new StaticOptions(options ?? new NntpdOptions()));

        /// <inheritdoc />
        public bool TryResolvePeer(ReadOnlySpan<byte> peerName, out TransitPeerPolicy? peer)
        {
            peer = null;
            if (peerName.IsEmpty)
            {
                return false;
            }

            var snapshot = _store.Current;
            foreach (var candidate in snapshot.Peers.Values)
            {
                if (NameEquals(candidate.Identifier, peerName))
                {
                    peer = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryAcquire(string peerName, out SpeedTestLease? lease)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            lease = null;
            var speed = _options.CurrentValue.SpeedTest ?? new SpeedTestOptions();
            var limits = speed.Snapshot();

            lock (_gate)
            {
                if (_global >= limits.MaxConcurrent)
                {
                    return false;
                }

                var peerCount = _perPeer.GetValueOrDefault(peerName);
                if (peerCount >= limits.MaxConcurrentPerPeer)
                {
                    return false;
                }

                _global++;
                _perPeer[peerName] = peerCount + 1;
            }

            lease = new SpeedTestLease(peerName, limits, Release);
            return true;
        }

        /// <summary>Gets the current host-wide in-flight count (tests).</summary>
        internal int GlobalInFlight
        {
            get
            {
                lock (_gate)
                {
                    return _global;
                }
            }
        }

        /// <summary>Gets the current in-flight count for <paramref name="peerName"/> (tests).</summary>
        internal int InFlightFor(string peerName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            lock (_gate)
            {
                return _perPeer.GetValueOrDefault(peerName);
            }
        }

        private void Release(string peerName)
        {
            lock (_gate)
            {
                _global = Math.Max(0, _global - 1);
                if (_perPeer.TryGetValue(peerName, out var count))
                {
                    if (count <= 1)
                    {
                        _perPeer.TryRemove(peerName, out _);
                    }
                    else
                    {
                        _perPeer[peerName] = count - 1;
                    }
                }
            }
        }

        /// <summary>
        /// Exact Transit name match. ASCII names compare as ordinal bytes; non-ASCII names
        /// compare as UTF-8 of the configured string (the dictionary key representation).
        /// </summary>
        internal static bool NameEquals(string name, ReadOnlySpan<byte> token)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (IsAscii(name))
            {
                if (name.Length != token.Length)
                {
                    return false;
                }

                for (var i = 0; i < name.Length; i++)
                {
                    if ((byte)name[i] != token[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            var max = Encoding.UTF8.GetMaxByteCount(name.Length);
            if (max > 1024)
            {
                return false;
            }

            Span<byte> utf8 = stackalloc byte[max];
            if (!Encoding.UTF8.TryGetBytes(name, utf8, out var written))
            {
                return false;
            }

            return utf8[..written].SequenceEqual(token);
        }

        private static bool IsAscii(string name)
        {
            foreach (var ch in name)
            {
                if (ch > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class StaticOptions : IOptionsMonitor<NntpdOptions>
        {
            public StaticOptions(NntpdOptions value) => CurrentValue = value;

            public NntpdOptions CurrentValue { get; }

            public NntpdOptions Get(string? name) => CurrentValue;

            public IDisposable OnChange(Action<NntpdOptions, string?> listener) => EmptyDisposable.Instance;
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
