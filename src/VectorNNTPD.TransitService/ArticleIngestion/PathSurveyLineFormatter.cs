namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Serializes one INN-style Path-survey observation as <c>Path: {canonical-path}\n</c>.
    /// </summary>
    /// <remarks>
    /// The wire format is an application invariant. This type copies
    /// <c>ArticleRecord.Path</c> bytes as-is: it does not parse Path, invent hops,
    /// or add Message-ID, Newsgroups, size, timestamp, peer, or disposition.
    /// </remarks>
    internal static class PathSurveyLineFormatter
    {
        /// <summary>ASCII <c>Path: </c> prefix including the header-style space.</summary>
        internal static ReadOnlySpan<byte> Prefix => "Path: "u8;

        /// <summary>Formats <paramref name="canonicalPath"/> into <paramref name="destination"/> as one LF-terminated line.</summary>
        /// <returns>Bytes written.</returns>
        public static int Write(Span<byte> destination, ReadOnlySpan<byte> canonicalPath)
        {
            var prefix = Prefix;
            prefix.CopyTo(destination);
            var written = prefix.Length;
            canonicalPath.CopyTo(destination[written..]);
            written += canonicalPath.Length;
            destination[written++] = (byte)'\n';
            return written;
        }

        /// <summary>Byte count required to format <paramref name="canonicalPath"/> including the trailing LF.</summary>
        public static int RequiredLength(ReadOnlySpan<byte> canonicalPath) =>
            Prefix.Length + canonicalPath.Length + 1;
    }
}
