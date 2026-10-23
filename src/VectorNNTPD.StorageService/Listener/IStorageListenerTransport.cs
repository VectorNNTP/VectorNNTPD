namespace VectorNNTPD.StorageService.Listener
{
    /// <summary>One established TLS transport owned by a StorageServer NNTP feed session.</summary>
    public interface IStorageListenerTransport : IAsyncDisposable
    {
        /// <summary>Reads bytes. Zero means the peer closed.</summary>
        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>Writes bytes and returns how many were accepted.</summary>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>
        /// Suspends or resumes the read no-progress timeout.
        /// A TAKETHIS wait for queue capacity suspends it until that wait ends.
        /// </summary>
        /// <param name="suspended"><see langword="true"/> to disarm the outstanding read timer.</param>
        void SetReadProgressSuspended(bool suspended) { }
    }
}
