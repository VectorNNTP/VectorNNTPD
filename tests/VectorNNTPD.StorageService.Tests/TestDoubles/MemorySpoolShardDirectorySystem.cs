using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>In-memory shard directories. Thread-safe for the initializer's worker pool.</summary>
    internal sealed class MemorySpoolShardDirectorySystem : ISpoolShardDirectorySystem
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private int _inFlight;
        private int _maxInFlight;

        /// <summary>When set, the first create throws <see cref="IOException"/> with this message.</summary>
        internal string? FailMessage { get; init; }

        /// <summary>Highest number of overlapping <see cref="CreateDirectory"/> calls.</summary>
        internal int MaxInFlight
        {
            get
            {
                lock (_gate)
                {
                    return _maxInFlight;
                }
            }
        }

        /// <summary>Directories currently recorded.</summary>
        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _directories.Count;
                }
            }
        }

        /// <inheritdoc />
        public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
        {
            if (FailMessage is not null)
            {
                throw new IOException(FailMessage);
            }

            lock (_gate)
            {
                _inFlight++;
                if (_inFlight > _maxInFlight)
                {
                    _maxInFlight = _inFlight;
                }
            }

            try
            {
                lock (_gate)
                {
                    if (!_directories.Add(fullPath))
                    {
                        return SpoolDirectoryCreateResult.AlreadyPresent;
                    }

                    return SpoolDirectoryCreateResult.Created;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }
    }
}
