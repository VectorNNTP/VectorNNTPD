using System.Buffers.Binary;
using System.Text;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Parses TXT resource records from DNS response packets for ACME DNS-01 visibility checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A successful parse requires: matching transaction id; QR=1, OPCODE=0, TC=0, RCODE=0;
    /// AA=1 when <c>requireAuthoritativeAnswer</c> is set (authoritative TXT proof path);
    /// exactly one question whose QNAME/QTYPE/QCLASS echo the TXT/IN query for
    /// <c>expectedOwnerName</c>; and answer-section integrity through every RR.
    /// </para>
    /// <para>
    /// Only TXT/IN answers whose owner name equals <c>expectedOwnerName</c> (DNS case-insensitive)
    /// contribute payloads. TXT digests under a different owner never count. Multi-segment TXT RDATA
    /// is concatenated per RR (RFC 1035 character-strings). Malformed TXT RDATA fails the entire
    /// parse rather than returning partial results. Whitespace and presentation quotes are not stripped.
    /// </para>
    /// </remarks>
    internal static class DnsTxtParser
    {
        /// <summary>
        /// Parses TXT answers from a DNS response and returns concatenated character-string bytes per RR.
        /// </summary>
        /// <param name="buffer">DNS response.</param>
        /// <param name="expectedId">Transaction id that must match the response.</param>
        /// <param name="expectedOwnerName">Queried TXT owner name; question and answer owners must match it.</param>
        /// <param name="results">Cleared, then filled with one concatenated TXT payload per accepted answer.</param>
        /// <param name="requireAuthoritativeAnswer">
        /// When <see langword="true"/> (default), requires AA=1. Direct authoritative nameserver queries
        /// for DNS-01 propagation use this. Leave <see langword="false"/> only for non-authoritative proof paths.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the response is a well-formed match for the query (possibly with zero TXT answers).
        /// <see langword="false"/> on header, question, owner-name, compression, or RDATA failures; <paramref name="results"/> is cleared.
        /// </returns>
        internal static bool TryParseTxtRecords(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            string expectedOwnerName,
            List<byte[]> results,
            bool requireAuthoritativeAnswer = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerName);
            ArgumentNullException.ThrowIfNull(results);
            results.Clear();

            if (!TryBeginAnswerScan(
                    buffer,
                    expectedId,
                    expectedOwnerName,
                    requireAuthoritativeAnswer,
                    out ushort anCount,
                    out int offset))
            {
                return false;
            }

            for (int a = 0; a < anCount; a++)
            {
                if (!TryReadAnswerOwnerAndFields(
                        buffer,
                        ref offset,
                        out string ownerName,
                        out ushort rrType,
                        out ushort rrClass,
                        out ushort rdLength))
                {
                    results.Clear();
                    return false;
                }

                if (offset + rdLength > buffer.Length)
                {
                    results.Clear();
                    return false;
                }

                if (rrType == DnsRecordType.Txt && rrClass == DnsRecordType.ClassIn)
                {
                    if (!TryReadTxtRdata(buffer, ref offset, rdLength, out byte[] txtBytes))
                    {
                        results.Clear();
                        return false;
                    }

                    if (DnsNameCodec.NamesEqual(ownerName, expectedOwnerName))
                    {
                        results.Add(txtBytes);
                    }
                }
                else
                {
                    offset += rdLength;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when any TXT RR under <paramref name="expectedOwnerName"/> equals
        /// <paramref name="expectedTxt"/> exactly (concatenated character-string bytes).
        /// </summary>
        internal static bool ResponseContainsTxt(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            string expectedOwnerName,
            ReadOnlySpan<byte> expectedTxt,
            bool requireAuthoritativeAnswer = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerName);

            if (!TryBeginAnswerScan(
                    buffer,
                    expectedId,
                    expectedOwnerName,
                    requireAuthoritativeAnswer,
                    out ushort anCount,
                    out int offset))
            {
                return false;
            }

            // Scan every answer RR. A match is remembered, but a later malformed TXT fails the
            // whole response so partial success cannot satisfy DNS-01 validation.
            bool matched = false;
            for (int a = 0; a < anCount; a++)
            {
                if (!TryReadAnswerOwnerAndFields(
                        buffer,
                        ref offset,
                        out string ownerName,
                        out ushort rrType,
                        out ushort rrClass,
                        out ushort rdLength))
                {
                    return false;
                }

                if (offset + rdLength > buffer.Length)
                {
                    return false;
                }

                if (rrType == DnsRecordType.Txt && rrClass == DnsRecordType.ClassIn)
                {
                    TxtCompareResult compare = CompareTxtRdata(buffer, ref offset, rdLength, expectedTxt);
                    if (compare == TxtCompareResult.Malformed)
                    {
                        return false;
                    }

                    if (compare == TxtCompareResult.Match
                        && DnsNameCodec.NamesEqual(ownerName, expectedOwnerName))
                    {
                        matched = true;
                    }
                }
                else
                {
                    offset += rdLength;
                }
            }

            return matched;
        }

        /// <summary>
        /// Parses TXT answers as ASCII strings (lossy for non-ASCII wire data).
        /// </summary>
        /// <param name="buffer">DNS response.</param>
        /// <param name="expectedId">Transaction id that must match the response.</param>
        /// <param name="expectedOwnerName">Queried TXT owner name.</param>
        /// <param name="requireAuthoritativeAnswer">When <see langword="true"/>, requires AA=1.</param>
        /// <returns>One ASCII string per accepted TXT answer, or empty when the packet is not well-formed.</returns>
        internal static List<string> ParseTxtStrings(
            byte[] buffer,
            ushort expectedId,
            string expectedOwnerName,
            bool requireAuthoritativeAnswer = true)
        {
            List<string> results = [];
            List<byte[]> raw = [];
            if (!TryParseTxtRecords(buffer, expectedId, expectedOwnerName, raw, requireAuthoritativeAnswer))
            {
                return results;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                results.Add(Encoding.ASCII.GetString(raw[i]));
            }

            return results;
        }

        /// <summary>
        /// Validates the header and echoed question, then positions <paramref name="offset"/> at the first answer RR.
        /// </summary>
        private static bool TryBeginAnswerScan(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            string expectedOwnerName,
            bool requireAuthoritativeAnswer,
            out ushort anCount,
            out int offset)
        {
            anCount = 0;
            offset = DnsWireFormat.HeaderSize;

            if (!DnsResponseHeader.TryReadCounts(
                    buffer,
                    expectedId,
                    out ushort qdCount,
                    out anCount,
                    out _,
                    out _,
                    requireAuthoritativeAnswer))
            {
                return false;
            }

            // ACME TXT queries send a single question; require an exact echo.
            if (qdCount != 1)
            {
                return false;
            }

            if (!DnsNameCodec.TryReadDomainName(buffer, ref offset, out string questionName))
            {
                return false;
            }

            if (offset + DnsWireFormat.QuestionSuffixSize > buffer.Length)
            {
                return false;
            }

            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
            ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
            offset += DnsWireFormat.QuestionSuffixSize;

            if (qtype != DnsRecordType.Txt || qclass != DnsRecordType.ClassIn)
            {
                return false;
            }

            if (!DnsNameCodec.NamesEqual(questionName, expectedOwnerName))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads one answer RR owner name and TYPE/CLASS/TTL/RDLENGTH; leaves <paramref name="offset"/> at RDATA.
        /// </summary>
        private static bool TryReadAnswerOwnerAndFields(
            ReadOnlySpan<byte> buffer,
            ref int offset,
            out string ownerName,
            out ushort rrType,
            out ushort rrClass,
            out ushort rdLength)
        {
            ownerName = string.Empty;
            rrType = 0;
            rrClass = 0;
            rdLength = 0;

            if (!DnsNameCodec.TryReadDomainName(buffer, ref offset, out ownerName))
            {
                return false;
            }

            if (offset + DnsWireFormat.ResourceRecordFixedFieldsSize > buffer.Length)
            {
                return false;
            }

            rrType = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
            rrClass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
            rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 8)..]);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            return true;
        }

        /// <summary>
        /// Concatenates the character-strings in one TXT RDATA and advances <paramref name="offset"/> to the end of that RDATA.
        /// </summary>
        /// <param name="span">DNS message.</param>
        /// <param name="offset">Start of RDATA. On success, the first byte after this RDATA.</param>
        /// <param name="rdLength">RDLENGTH.</param>
        /// <param name="txtBytes">Concatenated character-string bytes (may be empty when RDLENGTH is 0).</param>
        /// <returns><see langword="false"/> when a character-string does not fit; <paramref name="offset"/> is unspecified on failure.</returns>
        private static bool TryReadTxtRdata(
            ReadOnlySpan<byte> span,
            ref int offset,
            ushort rdLength,
            out byte[] txtBytes)
        {
            txtBytes = [];
            int rdEnd = offset + rdLength;
            if (rdEnd > span.Length)
            {
                return false;
            }

            if (rdLength == 0)
            {
                offset = rdEnd;
                txtBytes = [];
                return true;
            }

            int totalLength = 0;
            int scan = offset;
            while (scan < rdEnd)
            {
                if (scan >= span.Length)
                {
                    return false;
                }

                int strLen = span[scan++];
                if (scan + strLen > span.Length || scan + strLen > rdEnd)
                {
                    return false;
                }

                totalLength += strLen;
                scan += strLen;
            }

            if (scan != rdEnd)
            {
                return false;
            }

            txtBytes = new byte[totalLength];
            int write = 0;
            while (offset < rdEnd)
            {
                int strLen = span[offset++];
                span.Slice(offset, strLen).CopyTo(txtBytes.AsSpan(write));
                write += strLen;
                offset += strLen;
            }

            return true;
        }

        /// <summary>
        /// Compares concatenated TXT character-strings to <paramref name="expectedTxt"/> and always advances past the RDATA on a well-formed layout.
        /// </summary>
        private static TxtCompareResult CompareTxtRdata(
            ReadOnlySpan<byte> span,
            ref int offset,
            ushort rdLength,
            ReadOnlySpan<byte> expectedTxt)
        {
            int rdEnd = offset + rdLength;
            if (rdEnd > span.Length)
            {
                return TxtCompareResult.Malformed;
            }

            if (rdLength == 0)
            {
                offset = rdEnd;
                return expectedTxt.Length == 0 ? TxtCompareResult.Match : TxtCompareResult.Mismatch;
            }

            int compareIndex = 0;
            int scan = offset;
            while (scan < rdEnd)
            {
                if (scan >= span.Length)
                {
                    return TxtCompareResult.Malformed;
                }

                int strLen = span[scan++];
                if (scan + strLen > span.Length || scan + strLen > rdEnd)
                {
                    return TxtCompareResult.Malformed;
                }

                ReadOnlySpan<byte> segment = span.Slice(scan, strLen);
                if (compareIndex + segment.Length > expectedTxt.Length
                    || !segment.SequenceEqual(expectedTxt.Slice(compareIndex, segment.Length)))
                {
                    // Still verify the remainder of RDATA is well-formed before classifying as mismatch.
                    scan += strLen;
                    while (scan < rdEnd)
                    {
                        if (scan >= span.Length)
                        {
                            return TxtCompareResult.Malformed;
                        }

                        int restLen = span[scan++];
                        if (scan + restLen > span.Length || scan + restLen > rdEnd)
                        {
                            return TxtCompareResult.Malformed;
                        }

                        scan += restLen;
                    }

                    if (scan != rdEnd)
                    {
                        return TxtCompareResult.Malformed;
                    }

                    offset = rdEnd;
                    return TxtCompareResult.Mismatch;
                }

                compareIndex += segment.Length;
                scan += strLen;
            }

            if (scan != rdEnd)
            {
                return TxtCompareResult.Malformed;
            }

            offset = rdEnd;
            return compareIndex == expectedTxt.Length ? TxtCompareResult.Match : TxtCompareResult.Mismatch;
        }

        /// <summary>Result of comparing one TXT RDATA to an expected digest.</summary>
        private enum TxtCompareResult
        {
            /// <summary>Concatenated character-strings equal the expected bytes.</summary>
            Match,

            /// <summary>RDATA is well-formed but does not equal the expected bytes.</summary>
            Mismatch,

            /// <summary>Character-string lengths are inconsistent with RDLENGTH or the packet bounds.</summary>
            Malformed,
        }
    }
}
