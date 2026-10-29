namespace VectorNNTPD.ReaderService.History
{
    /// <summary>
    /// Binary Redis keys for HistoryDB: ASCII namespace prefix plus the 32-byte BLAKE3 digest.
    /// </summary>
    /// <remarks>
    /// Format: <c>nntpd:hist:</c> (11 bytes) concatenated with the digest. The digest is not
    /// hex- or base64-encoded. Conversion to <c>byte[]</c> happens at the Redis API boundary.
    /// </remarks>
    public static class HistoryRedisKeys
    {
        /// <summary>ASCII namespace isolating HistoryDB keys from other Redis consumers.</summary>
        public static ReadOnlySpan<byte> NamespacePrefix => "nntpd:hist:"u8;

        /// <summary>Total Redis key length in bytes.</summary>
        public static int KeyLength => NamespacePrefix.Length + HistoryDigest.Length;

        /// <summary>Builds the deterministic HistoryDB Redis key for <paramref name="digest"/>.</summary>
        public static byte[] Create(in HistoryDigest digest)
        {
            var key = new byte[KeyLength];
            NamespacePrefix.CopyTo(key);
            digest.CopyTo(key.AsSpan(NamespacePrefix.Length));
            return key;
        }
    }
}
