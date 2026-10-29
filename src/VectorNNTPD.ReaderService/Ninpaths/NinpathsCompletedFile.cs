namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>An opened completed Path-survey file held across gzip/deletion.</summary>
    internal sealed class NinpathsCompletedFile : IDisposable
    {
        /// <summary>Initializes a handoff of <paramref name="path"/> with an already-open <paramref name="stream"/>.</summary>
        public NinpathsCompletedFile(string path, FileStream stream)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(stream);
            Path = path;
            Stream = stream;
        }

        /// <summary>Gets the completed file path supplied by Serilog.</summary>
        public string Path { get; }

        /// <summary>Gets the sequential reader. Opened with share-read/write/delete.</summary>
        public FileStream Stream { get; }

        /// <inheritdoc />
        public void Dispose() => Stream.Dispose();
    }
}
