namespace VectorNNTPD.Common.Acme
{
    /// <summary>Atomic filesystem helpers for ACME state (temp + replace).</summary>
    internal static class AtomicFile
    {
        /// <summary>
        /// Writes <paramref name="data"/> to a new temp file in the destination directory, flushes it, then replaces <paramref name="path"/>.
        /// The temp file is deleted if the write or replace throws.
        /// </summary>
        /// <param name="path">Final path. The parent directory is created when missing.</param>
        /// <param name="data">Bytes to persist. May be empty.</param>
        internal static void WriteBytes(string path, ReadOnlySpan<byte> data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Join(
                directory ?? ".",
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        /// <summary>UTF-8 encodes <paramref name="text"/> and writes it with <see cref="WriteBytes"/>.</summary>
        /// <param name="path">Final path.</param>
        /// <param name="text">Text to persist, including any trailing newline the caller supplied.</param>
        internal static void WriteText(string path, string text) =>
            WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Deletes <paramref name="path"/> when it exists.
        /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are ignored.
        /// </summary>
        /// <param name="path">File to delete.</param>
        internal static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
