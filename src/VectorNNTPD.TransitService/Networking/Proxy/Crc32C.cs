namespace VectorNNTPD.TransitService.Networking.Proxy
{
    /// <summary>
    /// CRC32c (Castagnoli) for PROXY v2 <c>PP2_TYPE_CRC32C</c> (RFC 4960 Appendix B).
    /// </summary>
    internal static class Crc32C
    {
        // Reflected Castagnoli polynomial 0x1EDC6F41 → 0x82F63B78.
        private const uint Polynomial = 0x82F63B78u;
        private static readonly uint[] Table = BuildTable();

        /// <summary>Computes CRC32c of <paramref name="data"/> (final XOR applied).</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                }

                table[i] = crc;
            }

            return table;
        }
    }
}
