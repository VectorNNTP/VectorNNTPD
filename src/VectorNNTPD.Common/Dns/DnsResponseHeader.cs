using System.Buffers.Binary;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Shared DNS response header checks (transaction ID, QR, OPCODE, TC, RCODE, and optionally AA).
    /// </summary>
    internal static class DnsResponseHeader
    {
        /// <summary>
        /// Mask requiring QR=1, OPCODE=0, TC=0, and RCODE=0 (AA/RD/RA/Z ignored by this mask).
        /// </summary>
        private const ushort ResponseSanityMask = 0xFA0F;

        /// <summary>Expected masked value: response with NoError and no truncation.</summary>
        private const ushort ResponseSanityValue = 0x8000;

        /// <summary>DNS header AA (authoritative answer) flag (RFC 1035 §4.1.1).</summary>
        public const ushort AuthoritativeAnswerFlag = 0x0400;

        /// <summary>DNS header TC (truncated) flag.</summary>
        public const ushort TruncatedFlag = 0x0200;

        /// <summary>
        /// Returns <see langword="true"/> when the buffer has a header, matching ID, and sane response flags.
        /// </summary>
        /// <param name="buffer">DNS message.</param>
        /// <param name="expectedId">Transaction id that must match.</param>
        /// <param name="questionCount">QDCOUNT on success.</param>
        /// <param name="answerCount">ANCOUNT on success.</param>
        /// <param name="authorityCount">NSCOUNT on success.</param>
        /// <param name="additionalCount">ARCOUNT on success.</param>
        /// <param name="requireAuthoritativeAnswer">
        /// When <see langword="true"/>, also requires AA=1. Used for direct authoritative TXT proof;
        /// bootstrap/recursive NS and address lookups leave this <see langword="false"/>.
        /// </param>
        internal static bool TryReadCounts(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            out ushort questionCount,
            out ushort answerCount,
            out ushort authorityCount,
            out ushort additionalCount,
            bool requireAuthoritativeAnswer = false)
        {
            questionCount = 0;
            answerCount = 0;
            authorityCount = 0;
            additionalCount = 0;

            if (buffer.Length < DnsWireFormat.HeaderSize)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(buffer) != expectedId)
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            if ((flags & ResponseSanityMask) != ResponseSanityValue)
            {
                return false;
            }

            if (requireAuthoritativeAnswer && (flags & AuthoritativeAnswerFlag) == 0)
            {
                return false;
            }

            questionCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
            answerCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[6..]);
            authorityCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[8..]);
            additionalCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[10..]);
            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the UDP response header indicates truncation (TC).
        /// </summary>
        internal static bool IsTruncated(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < DnsWireFormat.HeaderSize)
            {
                return true;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            return (flags & TruncatedFlag) != 0;
        }

        /// <summary>
        /// Advances past the question section without validating QNAME/QTYPE/QCLASS.
        /// </summary>
        /// <remarks>
        /// Used by bootstrap NS/A/AAAA parsers. TXT validation uses
        /// <see cref="DnsTxtParser"/> which reads and checks the echoed question.
        /// </remarks>
        internal static bool TrySkipQuestions(ReadOnlySpan<byte> span, ref int offset, ushort questionCount)
        {
            for (int q = 0; q < questionCount; q++)
            {
                if (!DnsNameCodec.TrySkipName(span, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormat.QuestionSuffixSize > span.Length)
                {
                    return false;
                }

                offset += DnsWireFormat.QuestionSuffixSize;
            }

            return true;
        }
    }
}
