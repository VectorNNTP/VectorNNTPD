namespace VectorNNTPD.TransitService.Ninpaths
{
    /// <summary>
    /// Streaming Path-survey reader that applies INN <c>procpaths</c> / <c>fgets</c> line semantics.
    /// </summary>
    /// <remarks>
    /// Reads sequentially with a fixed buffer. Does not load the file, does not
    /// retain Path strings, and does not allocate per article beyond interned
    /// unique sites. Matches INN <c>fgets(buf, MAXLINE)</c>, <c>isspace</c>,
    /// case-insensitive <c>Path: </c>, last-element chop, over-length token abort,
    /// and the one-window bogus-line skip (not skip-until-newline).
    /// </remarks>
    public static class NinpathsLogReader
    {
        private static ReadOnlySpan<byte> PathPrefix => "Path: "u8;

        /// <summary>
        /// Aggregates every valid Path line from <paramref name="stream"/> into
        /// a new <see cref="NinpathsStatistics"/> instance.
        /// </summary>
        public static NinpathsStatistics Read(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var stats = new NinpathsStatistics();
            Read(stream, stats);
            return stats;
        }

        /// <summary>Aggregates Path lines from <paramref name="stream"/> into <paramref name="stats"/>.</summary>
        public static void Read(Stream stream, NinpathsStatistics stats)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(stats);

            var window = new byte[NinpathsConstants.MaxFgetsChars];
            var block = new byte[NinpathsConstants.ReadBufferSize];
            var blockLength = 0;
            var blockOffset = 0;
            var valid = true;
            while (true)
            {
                var n = ReadFgets(stream, window, block, ref blockLength, ref blockOffset);
                if (n <= 0)
                {
                    break;
                }

                ProcessFgetsWindow(window.AsSpan(0, n), stats, ref valid);
            }
        }

        /// <summary>Applies INN <c>procpaths</c> to one <c>fgets</c> window (newline included when present).</summary>
        internal static void ProcessFgetsWindow(ReadOnlySpan<byte> window, NinpathsStatistics stats, ref bool valid)
        {
            var body = window;
            if (StartsWithPathPrefix(body))
            {
                body = body[PathPrefix.Length..];
            }

            var end = 0;
            for (; end < body.Length; end++)
            {
                if (IsSpace(body[end]))
                {
                    break;
                }
            }

            if (end >= body.Length)
            {
                valid = false;
                return;
            }

            if (!valid)
            {
                valid = true;
                return;
            }

            var path = body[..end];
            var lastBang = path.LastIndexOf((byte)'!');
            var chopped = lastBang < 0 ? ReadOnlySpan<byte>.Empty : path[..lastBang];
            stats.AddChoppedPath(chopped);
            stats.AddValidArticle();
        }

        private static int ReadFgets(
            Stream stream,
            byte[] window,
            byte[] block,
            ref int blockLength,
            ref int blockOffset)
        {
            var written = 0;
            while (written < window.Length)
            {
                if (blockOffset >= blockLength)
                {
                    blockLength = stream.Read(block, 0, block.Length);
                    blockOffset = 0;
                    if (blockLength == 0)
                    {
                        return written;
                    }
                }

                var b = block[blockOffset++];
                window[written++] = b;
                if (b == (byte)'\n')
                {
                    break;
                }
            }

            return written;
        }

        private static bool StartsWithPathPrefix(ReadOnlySpan<byte> line)
        {
            var prefix = PathPrefix;
            if (line.Length < prefix.Length)
            {
                return false;
            }

            for (var i = 0; i < prefix.Length; i++)
            {
                var a = line[i];
                var b = prefix[i];
                if (a == b)
                {
                    continue;
                }

                if (a is >= (byte)'A' and <= (byte)'Z')
                {
                    a += 32;
                }

                if (b is >= (byte)'A' and <= (byte)'Z')
                {
                    b += 32;
                }

                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>C <c>isspace</c> on <c>unsigned char</c> (C locale).</summary>
        internal static bool IsSpace(byte value) =>
            value is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20;
    }
}
