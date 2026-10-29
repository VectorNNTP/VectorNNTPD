namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Holds the active immutable Transit configuration snapshot.
    /// </summary>
    /// <remarks>
    /// Replacement is atomic. Readers use <see cref="Current"/> without taking a lock.
    /// A listener registered via <see cref="Subscribe"/> observes complete snapshots only.
    /// </remarks>
    public sealed class TransitConfigurationStore
    {
        private TransitConfigurationSnapshot _current = TransitConfigurationSnapshot.Empty;
        private event Action<TransitConfigurationSnapshot>? Changed;

        /// <summary>Gets the current complete snapshot.</summary>
        public TransitConfigurationSnapshot Current => Volatile.Read(ref _current!);

        /// <summary>Replaces the active snapshot with a fully built instance.</summary>
        public void Replace(TransitConfigurationSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Volatile.Write(ref _current, snapshot);
            Changed?.Invoke(snapshot);
        }

        /// <summary>Subscribes to complete snapshot replacements.</summary>
        public IDisposable Subscribe(Action<TransitConfigurationSnapshot> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            Changed += listener;
            return new Subscription(this, listener);
        }

        private sealed class Subscription : IDisposable
        {
            private TransitConfigurationStore? _store;
            private Action<TransitConfigurationSnapshot>? _listener;

            public Subscription(TransitConfigurationStore store, Action<TransitConfigurationSnapshot> listener)
            {
                _store = store;
                _listener = listener;
            }

            public void Dispose()
            {
                var store = Interlocked.Exchange(ref _store, null);
                var listener = Interlocked.Exchange(ref _listener, null);
                if (store is not null && listener is not null)
                {
                    store.Changed -= listener;
                }
            }
        }
    }
}
