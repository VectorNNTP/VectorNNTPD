using System.Diagnostics;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// DIAGNOSTIC-ONLY reusable contiguous buffer for experimental TX write aggregation.
    /// </summary>
    /// <remarks>
    /// One buffer is allocated for the connection lifetime. Copies are measured when a
    /// <see cref="TransportIoSession"/> is supplied. The buffer remains valid until
    /// <see cref="Clear"/> after the transport write has completed.
    /// </remarks>
    internal sealed class TxWriteCoalescer
    {
        private readonly byte[] _buffer;
        private int _count;

        /// <summary>Creates a coalescer whose buffer is exactly <paramref name="targetBytes"/>.</summary>
        public TxWriteCoalescer(int targetBytes)
        {
            if (!TxWriteGranularityExperiment.IsAllowed(targetBytes))
            {
                throw new ArgumentOutOfRangeException(nameof(targetBytes), targetBytes, "Target must be an allowlisted write-granularity size.");
            }

            TargetBytes = targetBytes;
            _buffer = GC.AllocateUninitializedArray<byte>(targetBytes);
        }

        /// <summary>Configured aggregate size in bytes.</summary>
        public int TargetBytes { get; }

        /// <summary>Bytes currently held in the reusable buffer.</summary>
        public int PendingCount => _count;

        /// <summary>Unused bytes before the next transport write must be issued.</summary>
        public int RemainingCapacity => TargetBytes - _count;

        /// <summary>Gets whether the buffer is full and must be written before more copies.</summary>
        public bool IsFull => _count >= TargetBytes;

        /// <summary>Pending bytes. Valid until <see cref="Clear"/>.</summary>
        public ReadOnlyMemory<byte> PendingMemory => _buffer.AsMemory(0, _count);

        /// <summary>
        /// Copies as many bytes as fit from <paramref name="source"/>. Returns the number copied.
        /// </summary>
        public int Copy(ReadOnlySpan<byte> source, TransportIoSession? io)
        {
            var take = Math.Min(source.Length, RemainingCapacity);
            if (take == 0)
            {
                return 0;
            }

            var started = io is null ? 0L : Stopwatch.GetTimestamp();
            source[..take].CopyTo(_buffer.AsSpan(_count, take));
            _count += take;
            if (io is not null)
            {
                io.RecordCopy(take, started, Stopwatch.GetTimestamp());
            }

            return take;
        }

        /// <summary>Drops pending bytes after a successful transport write (or a terminal failure).</summary>
        public void Clear() => _count = 0;
    }
}
