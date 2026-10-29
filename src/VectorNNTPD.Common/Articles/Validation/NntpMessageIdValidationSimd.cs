using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VectorNNTPD.Common.Articles.Validation
{
    /// <summary>
    /// One-pass RFC 5536 <c>msg-id</c> scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state machine accepts <c>dot-atom-text "@" (dot-atom-text / no-fold-literal)</c> between
    /// the angle brackets. A <see cref="Vector128{T}"/> step classifies 16 octets when the hardware
    /// supports it and at least 16 interior octets remain. It consumes the leading <c>atext</c> or
    /// <c>mdtext</c> prefix of that window. Dots, <c>@</c>, brackets, and shorter tails stay on the
    /// scalar path.
    /// </para>
    /// <para>
    /// <c>no-fold-literal</c> is <c>"[" *mdtext "]"</c>. It is not an IP-address grammar.
    /// <c>@</c> is legal <c>mdtext</c>, so a second <c>@</c> octet is valid only inside the literal.
    /// </para>
    /// </remarks>
    internal static class NntpMessageIdValidationSimd
    {
        /// <summary>Byte lanes in one <see cref="Vector128{T}"/> of <see cref="byte"/>.</summary>
        private const int Vector128ByteCount = 16;

        /// <summary>Low 16 bits of a <see cref="Vector128{T}"/> lane mask.</summary>
        private const uint Vector128LaneMask = 0xFFFF;

        /// <summary>
        /// Returns whether <paramref name="messageId"/> is an RFC 5536 <c>msg-id</c> whose brackets
        /// were already checked.
        /// </summary>
        /// <param name="messageId">Full token, length 3–250, starting with <c>&lt;</c> and ending with <c>&gt;</c>.</param>
        /// <returns>
        /// <see langword="true"/> when the interior is <c>id-left "@" id-right</c> with
        /// <c>id-left</c> as <c>dot-atom-text</c> and <c>id-right</c> as <c>dot-atom-text</c> or
        /// <c>no-fold-literal</c>.
        /// </returns>
        internal static bool IsRfc5536MsgId(ReadOnlySpan<byte> messageId)
        {
            var end = messageId.Length - 1;
            var index = 1;
            if (!TryConsumeDotAtom(messageId, end, ref index))
            {
                return false;
            }

            if ((uint)index >= (uint)end || messageId[index] != (byte)'@')
            {
                return false;
            }

            index++;
            if ((uint)index >= (uint)end)
            {
                return false;
            }

            if (messageId[index] == (byte)'[')
            {
                return TryConsumeNoFoldLiteral(messageId, end, ref index);
            }

            return TryConsumeDotAtom(messageId, end, ref index) && index == end;
        }

        /// <summary>Consumes one <c>dot-atom-text</c> and leaves <paramref name="index"/> on the following octet.</summary>
        /// <param name="messageId">Full token.</param>
        /// <param name="end">Index of the closing <c>&gt;</c>. Not included in the atom.</param>
        /// <param name="index">Current interior index. Advanced past the atom on success.</param>
        /// <returns><see langword="false"/> when the text is not <c>1*atext *("." 1*atext)</c>.</returns>
        private static bool TryConsumeDotAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if (!TryConsumeAtom(messageId, end, ref index))
            {
                return false;
            }

            while ((uint)index < (uint)end && messageId[index] == (byte)'.')
            {
                index++;
                if (!TryConsumeAtom(messageId, end, ref index))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Consumes <c>1*atext</c>.</summary>
        /// <param name="messageId">Full token.</param>
        /// <param name="end">Index of the closing <c>&gt;</c>.</param>
        /// <param name="index">Current index. Advanced past the atom on success.</param>
        /// <returns><see langword="false"/> when no <c>atext</c> is available.</returns>
        private static bool TryConsumeAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            var start = index;
            while (true)
            {
                if (!TryConsumeAtext(messageId, end, ref index))
                {
                    break;
                }
            }

            return index > start;
        }

        /// <summary>
        /// Consumes <c>"[" *mdtext "]"</c> through the end of the token.
        /// </summary>
        /// <param name="messageId">Full token.</param>
        /// <param name="end">Index of the closing <c>&gt;</c>.</param>
        /// <param name="index">Index of the opening <c>[</c>.</param>
        /// <returns>
        /// <see langword="true"/> when the literal is closed and nothing remains before the final <c>&gt;</c>.
        /// An empty <c>mdtext</c> is accepted.
        /// </returns>
        private static bool TryConsumeNoFoldLiteral(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            index++;
            while (true)
            {
                if (!TryConsumeMdtext(messageId, end, ref index))
                {
                    break;
                }
            }

            if ((uint)index >= (uint)end || messageId[index] != (byte)']')
            {
                return false;
            }

            index++;
            return index == end;
        }

        /// <summary>Consumes one or more leading <c>atext</c> octets, using a 16-byte vector when one fits.</summary>
        /// <param name="messageId">Full token.</param>
        /// <param name="end">Index of the closing <c>&gt;</c>.</param>
        /// <param name="index">Current index. Advanced by the consumed prefix.</param>
        /// <returns><see langword="false"/> when the current octet is not <c>atext</c>.</returns>
        /// <remarks>
        /// A zero lane mask, and any mask whose first lane is clear, is a zero leading count.
        /// That case returns before <see cref="BitOperations.TrailingZeroCount(uint)"/>.
        /// Native AOT lowers that count through a zero-extended NOT and BSF, which returns
        /// success without advancing <paramref name="index"/>.
        /// </remarks>
        private static bool TryConsumeAtext(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if ((uint)index >= (uint)end)
            {
                return false;
            }

            if (Vector128.IsHardwareAccelerated && index <= end - Vector128ByteCount)
            {
                ref var origin = ref MemoryMarshal.GetReference(messageId);
                var laneBits = AtextLaneBits(Vector128.LoadUnsafe(ref origin, (nuint)index));
                if (laneBits == 0 || (laneBits & 1u) == 0)
                {
                    return false;
                }

                index += LeadingMaskCount(laneBits);
                return true;
            }

            if (!NntpMessageIdCharClasses.IsAtext(messageId[index]))
            {
                return false;
            }

            index++;
            return true;
        }

        /// <summary>Consumes one or more leading <c>mdtext</c> octets, using a 16-byte vector when one fits.</summary>
        /// <param name="messageId">Full token.</param>
        /// <param name="end">Index of the closing <c>&gt;</c>.</param>
        /// <param name="index">Current index. Advanced by the consumed prefix.</param>
        /// <returns><see langword="false"/> when the current octet is not <c>mdtext</c>.</returns>
        /// <remarks>
        /// A zero lane mask, and any mask whose first lane is clear, returns before
        /// <see cref="BitOperations.TrailingZeroCount(uint)"/>. See <see cref="TryConsumeAtext"/>.
        /// </remarks>
        private static bool TryConsumeMdtext(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if ((uint)index >= (uint)end)
            {
                return false;
            }

            if (Vector128.IsHardwareAccelerated && index <= end - Vector128ByteCount)
            {
                ref var origin = ref MemoryMarshal.GetReference(messageId);
                var laneBits = MdtextLaneBits(Vector128.LoadUnsafe(ref origin, (nuint)index));
                if (laneBits == 0 || (laneBits & 1u) == 0)
                {
                    return false;
                }

                index += LeadingMaskCount(laneBits);
                return true;
            }

            if (!NntpMessageIdCharClasses.IsMdtext(messageId[index]))
            {
                return false;
            }

            index++;
            return true;
        }

        /// <summary>Returns the number of set low bits before the first clear bit in a 16-lane mask.</summary>
        /// <param name="laneBits">Low 16 bits are lane 0 through lane 15. Higher bits are ignored.</param>
        /// <returns>A count from 0 through 16.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LeadingMaskCount(uint laneBits) =>
            BitOperations.TrailingZeroCount(~(laneBits & Vector128LaneMask));

        /// <summary>Returns a 16-bit mask of lanes that are RFC 5322 <c>atext</c>.</summary>
        /// <param name="chunk">Sixteen candidate octets.</param>
        /// <returns>Bit 0 is lane 0. A set bit means that lane is <c>atext</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint AtextLaneBits(Vector128<byte> chunk)
        {
            var match = InRange(chunk, 0x21, 0x21);
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x23, 0x27));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x2A, 0x2B));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x2D, 0x2D));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x2F, 0x2F));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x30, 0x39));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x3D, 0x3D));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x3F, 0x3F));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x41, 0x5A));
            match = Vector128.BitwiseOr(match, InRange(chunk, 0x5E, 0x7E));
            return Vector128.ExtractMostSignificantBits(match);
        }

        /// <summary>Returns a 16-bit mask of lanes that are RFC 5536 <c>mdtext</c>.</summary>
        /// <param name="chunk">Sixteen candidate octets.</param>
        /// <returns>Bit 0 is lane 0. A set bit means that lane is <c>mdtext</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint MdtextLaneBits(Vector128<byte> chunk)
        {
            var match = InRange(chunk, 33, 61);
            match = Vector128.BitwiseOr(match, InRange(chunk, 63, 90));
            match = Vector128.BitwiseOr(match, InRange(chunk, 94, 126));
            return Vector128.ExtractMostSignificantBits(match);
        }

        /// <summary>Returns <c>0xFF</c> in each lane whose unsigned value is inside <paramref name="low"/>–<paramref name="high"/>.</summary>
        /// <param name="chunk">Sixteen candidate octets.</param>
        /// <param name="low">Inclusive lower bound.</param>
        /// <param name="high">Inclusive upper bound.</param>
        /// <returns>An all-ones lane where the octet is in range, otherwise zero.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> InRange(Vector128<byte> chunk, byte low, byte high) =>
            Vector128.BitwiseAnd(
                Vector128.GreaterThanOrEqual(chunk, Vector128.Create(low)),
                Vector128.LessThanOrEqual(chunk, Vector128.Create(high)));
    }
}
