using System.Buffers;
using System.Collections.Concurrent;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>Production filesystem adapter for <see cref="SpoolArticlePublisher"/>.</summary>
    internal sealed class FileSpoolArticleFiles : ISpoolArticleFiles
    {
        /// <summary>Leaf directories this process has already created. A missing directory is removed and created again.</summary>
        private readonly ConcurrentDictionary<string, byte> _createdDirectories = new(StringComparer.Ordinal);
        /// <inheritdoc />
        public bool Exists(string path) => File.Exists(path);

        /// <inheritdoc />
        public long Length(string path) => new FileInfo(path).Length;

        /// <inheritdoc />
        public bool ContentsEqual(string path, ReadOnlySpan<byte> content)
        {
            if (new FileInfo(path).Length != content.Length)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var rented = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                var offset = 0;
                while (offset < content.Length)
                {
                    var count = stream.Read(rented, 0, Math.Min(rented.Length, content.Length - offset));
                    if (count == 0 || !rented.AsSpan(0, count).SequenceEqual(content.Slice(offset, count)))
                    {
                        return false;
                    }

                    offset += count;
                }

                return stream.ReadByte() < 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <inheritdoc />
        public void CreateDirectory(string path)
        {
            if (_createdDirectories.ContainsKey(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            _createdDirectories.TryAdd(path, 0);
        }

        /// <inheritdoc />
        public void Delete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <inheritdoc />
        public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
        {
            try
            {
                WriteNew(path, content, cancellationToken);
            }
            catch (DirectoryNotFoundException)
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is null)
                {
                    throw;
                }

                _createdDirectories.TryRemove(parent, out _);
                Directory.CreateDirectory(parent);
                _createdDirectories.TryAdd(parent, 0);
                WriteNew(path, content, cancellationToken);
            }
        }

        /// <summary>Creates <paramref name="path"/> and writes <paramref name="content"/> without a device flush.</summary>
        /// <param name="path">Temporary article path. Must not already exist.</param>
        /// <param name="content">Rewritten article bytes.</param>
        /// <param name="cancellationToken">Cancels before the file is opened and after the write, before the cache flush.</param>
        private static void WriteNew(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            stream.Write(content);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: false);
        }

        /// <inheritdoc />
        public void MoveNew(string source, string destination) => File.Move(source, destination, overwrite: true);
    }
}
