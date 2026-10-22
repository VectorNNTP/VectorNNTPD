namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Fixed three-nibble shard tree for one spool. Depth is not configuration.
    /// Changing it would make existing articles undiscoverable, so a future layout needs an explicit migration.
    /// </summary>
    /// <remarks>
    /// Each level is one lowercase hexadecimal digit (fan-out 16).
    /// Three levels produce 4,096 leaves. Intermediate directories are part of initialization:
    /// 16 + 256 + 4,096 = 4,368 directories per spool.
    /// N spools create N times that many directories and do not reduce the per-spool article count.
    /// At 22 decimal TB and 750 KiB, an even split puts 6,993 articles in a leaf, and 2,505 leaves would hold 6,994.
    /// Two levels would put about 111,897 articles in one directory. Four levels would create 69,904 directories to hold about 437 articles each.
    /// </remarks>
    internal static class SpoolShardLayout
    {
        /// <summary>Hexadecimal fan-out of one level.</summary>
        internal const int Fanout = 16;

        /// <summary>Directory depth. Fixed for the life of this layout.</summary>
        internal const int Levels = 3;

        /// <summary>Leaf directories in one spool: 16^3.</summary>
        internal const int LeafCount = 4_096;

        /// <summary>Every directory in one spool, including intermediate levels.</summary>
        internal const int DirectoryCount = 16 + 256 + LeafCount;

        /// <summary>
        /// Maximum directory creates in flight across all spools.
        /// One task is not created per directory. Each spool receives at least one worker,
        /// and workers share this limit so spools start together without unbounded parallelism.
        /// </summary>
        internal const int InitializationConcurrency = 32;

        private static readonly char[] Hex = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f'];

        /// <summary>Breadth-first relative directories. Parents occupy earlier indexes than their children.</summary>
        internal static readonly string[] RelativeDirectories = Build();

        /// <summary>Start index and count of <see cref="RelativeDirectories"/> for each level, parents first.</summary>
        internal static IReadOnlyList<(int Start, int Count)> LevelRanges => _levelRanges;

        private static readonly (int Start, int Count)[] _levelRanges =
        [
            (0, 16),
            (16, 256),
            (16 + 256, LeafCount),
        ];

        /// <summary>Builds the breadth-first relative paths once.</summary>
        /// <returns>4,368 relative directory paths.</returns>
        private static string[] Build()
        {
            var all = new string[DirectoryCount];
            var index = 0;
            for (var a = 0; a < Fanout; a++)
            {
                all[index++] = Hex[a].ToString();
            }

            for (var a = 0; a < Fanout; a++)
            {
                for (var b = 0; b < Fanout; b++)
                {
                    all[index++] = string.Concat(Hex[a], "/", Hex[b]);
                }
            }

            for (var a = 0; a < Fanout; a++)
            {
                for (var b = 0; b < Fanout; b++)
                {
                    for (var c = 0; c < Fanout; c++)
                    {
                        all[index++] = string.Concat(Hex[a], "/", Hex[b], "/", Hex[c]);
                    }
                }
            }

            if (index != DirectoryCount)
            {
                throw new InvalidOperationException("Spool shard directory table was not fully built.");
            }

            return all;
        }
    }
}
