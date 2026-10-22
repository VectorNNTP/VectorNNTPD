namespace VectorNNTPD.Common.Core
{
    /// <summary>
    /// Holds an <see cref="IDisposable"/> until <see cref="Transfer"/> moves ownership, or <see cref="Dispose"/> cleans it up.
    /// </summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <remarks>
    /// <para>
    /// Use with a <c>using</c> statement: on success call <see cref="Transfer"/> before the <c>using</c> ends;
    /// on failure the <c>using</c> disposes the resource. Avoids manual dispose-in-<c>finally</c> while keeping
    /// explicit ownership transfer.
    /// </para>
    /// <para>
    /// Intended for single-threaded ownership within one method. <see cref="Transfer"/> and <see cref="Dispose"/>
    /// use atomic exchange so a concurrent dispose cannot dispose a resource that was already transferred, and
    /// a second transfer fails. Concurrent <see cref="Resource"/> access with transfer/dispose is not supported.
    /// A throw from the resource's <see cref="IDisposable.Dispose"/> propagates; when that happens inside a
    /// <c>using</c> that is already unwinding for another exception, the runtime may replace the primary exception
    /// (standard <c>using</c> behaviour).
    /// </para>
    /// </remarks>
    public sealed class DisposableOwnership<T> : IDisposable
        where T : class, IDisposable
    {
        private T? _resource;

        /// <summary>Takes ownership of <paramref name="resource"/> until transfer or dispose.</summary>
        /// <param name="resource">Resource to hold. Must not be null.</param>
        public DisposableOwnership(T resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            _resource = resource;
        }

        /// <summary>Gets the held resource before transfer.</summary>
        /// <exception cref="ObjectDisposedException">The resource was already transferred or disposed.</exception>
        public T Resource =>
            _resource ?? throw new ObjectDisposedException(nameof(DisposableOwnership<T>));

        /// <summary>
        /// Releases ownership to the caller without disposing. Subsequent <see cref="Dispose"/> is a no-op.
        /// </summary>
        /// <returns>The held resource.</returns>
        /// <exception cref="ObjectDisposedException">The resource was already transferred or disposed.</exception>
        public T Transfer()
        {
            var resource = Interlocked.Exchange(ref _resource, null);
            return resource ?? throw new ObjectDisposedException(nameof(DisposableOwnership<T>));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var resource = Interlocked.Exchange(ref _resource, null);
            resource?.Dispose();
        }
    }

    /// <summary>
    /// Holds an <see cref="IAsyncDisposable"/> until <see cref="Transfer"/> moves ownership, or
    /// <see cref="DisposeAsync"/> cleans it up.
    /// </summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <remarks>
    /// Same single-owner contract as <see cref="DisposableOwnership{T}"/>. <see cref="DisposeAsync"/> awaits
    /// the resource dispose and is not fire-and-forget.
    /// </remarks>
    public sealed class AsyncDisposableOwnership<T> : IAsyncDisposable
        where T : class, IAsyncDisposable
    {
        private T? _resource;

        /// <summary>Takes ownership of <paramref name="resource"/> until transfer or dispose.</summary>
        /// <param name="resource">Resource to hold. Must not be null.</param>
        public AsyncDisposableOwnership(T resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            _resource = resource;
        }

        /// <summary>Gets the held resource before transfer.</summary>
        /// <exception cref="ObjectDisposedException">The resource was already transferred or disposed.</exception>
        public T Resource =>
            _resource ?? throw new ObjectDisposedException(nameof(AsyncDisposableOwnership<T>));

        /// <summary>
        /// Releases ownership to the caller without disposing. Subsequent <see cref="DisposeAsync"/> is a no-op.
        /// </summary>
        /// <returns>The held resource.</returns>
        /// <exception cref="ObjectDisposedException">The resource was already transferred or disposed.</exception>
        public T Transfer()
        {
            var resource = Interlocked.Exchange(ref _resource, null);
            return resource ?? throw new ObjectDisposedException(nameof(AsyncDisposableOwnership<T>));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var resource = Interlocked.Exchange(ref _resource, null);
            if (resource is not null)
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
