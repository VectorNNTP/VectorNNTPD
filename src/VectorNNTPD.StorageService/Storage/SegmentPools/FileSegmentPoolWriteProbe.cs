namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Exclusive create, flush, read-back, and delete probe. Does not change permissions or mounts.
    /// </summary>
    internal sealed class FileSegmentPoolWriteProbe : ISegmentPoolWriteProbe
    {
        private static readonly byte[] Payload = "vectornntp-segment-pool-probe-v1"u8.ToArray();

        private readonly Action<string> _delete;

        /// <summary>Initializes a probe that deletes with <see cref="File.Delete"/>.</summary>
        public FileSegmentPoolWriteProbe()
            : this(File.Delete)
        {
        }

        /// <summary>Initializes a probe with an explicit delete operation so tests can force cleanup failure.</summary>
        /// <param name="delete">Deletes the probe file. Exceptions become cleanup failures.</param>
        internal FileSegmentPoolWriteProbe(Action<string> delete)
        {
            ArgumentNullException.ThrowIfNull(delete);
            _delete = delete;
        }

        /// <inheritdoc />
        public string? Probe(string canonicalDirectory, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectory);
            var name = ".vectornntp-pool-probe-" + Guid.NewGuid().ToString("N");
            if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                return "probe file name escaped the pool directory.";
            }

            var full = Path.GetFullPath(Path.Combine(canonicalDirectory, name));
            var root = SegmentPoolPaths.WithTrailingSeparator(SegmentPoolPaths.Normalize(canonicalDirectory));
            if (!full.StartsWith(root, SegmentPoolPaths.Comparison))
            {
                return $"probe path '{full}' escaped pool directory '{canonicalDirectory}'.";
            }

            string? failure = null;
            var created = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(
                    full,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 64,
                    FileOptions.WriteThrough))
                {
                    created = true;
                    stream.Write(Payload);
                    stream.Flush(flushToDisk: true);
                }

                var read = File.ReadAllBytes(full);
                if (!read.AsSpan().SequenceEqual(Payload))
                {
                    failure = "read-back did not match the probe payload.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = "write/read probe failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (created || File.Exists(full))
                {
                    try
                    {
                        _delete(full);
                        if (File.Exists(full))
                        {
                            const string cleanup = "probe cleanup left the temporary file in place.";
                            failure = failure is null ? cleanup : failure + " " + cleanup;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var cleanup = "probe cleanup failed: " + ex.GetType().Name + ": " + ex.Message;
                        failure = failure is null ? cleanup : failure + " " + cleanup;
                    }
                }
            }

            return failure;
        }
    }
}
