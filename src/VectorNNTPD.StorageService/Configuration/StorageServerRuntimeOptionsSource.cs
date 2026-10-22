namespace VectorNNTPD.StorageService.Configuration
{

    /// <summary>
    /// Holds the runtime snapshot published after <c>nntpsharedconfig</c> is loaded.
    /// </summary>
    /// <remarks>
    /// The snapshot is created once, during shared-configuration startup, after DNS suffix
    /// and ACME settings have been copied from NntpDB. Later refreshes update the live
    /// options objects and do not replace this snapshot. Resolving it before publication
    /// fails instead of using a JSON or in-code fallback.
    /// </remarks>
    public sealed class StorageServerRuntimeOptionsSource
    {
        private StorageServerRuntimeOptions? _current;

        /// <summary>Gets whether <see cref="Publish"/> has stored a snapshot.</summary>
        public bool IsPublished => Volatile.Read(ref _current) is not null;

        /// <summary>Gets the startup snapshot.</summary>
        /// <exception cref="InvalidOperationException">No snapshot has been published.</exception>
        public StorageServerRuntimeOptions Current =>
            Volatile.Read(ref _current)
            ?? throw new InvalidOperationException("StorageServer runtime options have not been published.");

        /// <summary>Stores the first snapshot. A later call leaves the first snapshot in place.</summary>
        /// <param name="options">Runtime options built from the published shared configuration.</param>
        public void Publish(StorageServerRuntimeOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Interlocked.CompareExchange(ref _current, options, null);
        }
    }
}
