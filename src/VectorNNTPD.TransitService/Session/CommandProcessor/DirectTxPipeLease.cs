using System.IO.Pipelines;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// DIAGNOSTIC-ONLY exclusive lease of the existing TX <see cref="PipeWriter"/>.
    /// </summary>
    /// <remarks>
    /// SPEEDTEST holds this while writing the synthetic payload so the ResponseWriter pump
    /// cannot produce to the same pipe. Dispose returns ownership to the Channel pump.
    /// Does not construct a pipe, socket, or transport.
    /// </remarks>
    internal sealed class DirectTxPipeLease : IAsyncDisposable
    {
        private readonly NntpResponseWriter _writer;
        private int _disposed;

        internal DirectTxPipeLease(NntpResponseWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            _writer = writer;
        }

        /// <summary>Gets the existing session TX <see cref="PipeWriter"/> (not a new pipe).</summary>
        public PipeWriter PipeWriter => _writer.UnderlyingOutput;

        /// <summary>Gets direct-path <c>FlushAsync</c> calls recorded on this writer.</summary>
        public long FlushCount => _writer.DirectPipeFlushCount;

        /// <summary>Copies <paramref name="payload"/> to the existing TX pipe and flushes.</summary>
        public ValueTask WriteAndFlushAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            _writer.WriteDirectToOutputAsync(payload, cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _writer.ReleaseDirectTxPipe();
            }

            return ValueTask.CompletedTask;
        }
    }
}
