using System.Runtime.CompilerServices;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>ASCII helpers for NNTP command parsing. RFC 3977 §3.1 SP/TAB. No culture.</summary>
    internal static class NntpAscii
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\t';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FoldUpper(byte b)
        {
            if (b >= (byte)'a' && b <= (byte)'z')
            {
                return (byte)(b - 32);
            }

            return b;
        }

        public static bool EqualsFolded(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upperAscii)
        {
            if (value.Length != upperAscii.Length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (FoldUpper(value[i]) != upperAscii[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static int SkipWhitespace(ReadOnlySpan<byte> line, int index)
        {
            while (index < line.Length && IsWhitespace(line[index]))
            {
                index++;
            }

            return index;
        }

        public static int SkipToken(ReadOnlySpan<byte> line, int index)
        {
            while (index < line.Length && !IsWhitespace(line[index]))
            {
                index++;
            }

            return index;
        }

        public static int TrimEndWhitespace(ReadOnlySpan<byte> line, int start, int end)
        {
            while (end > start && IsWhitespace(line[end - 1]))
            {
                end--;
            }

            return end;
        }

        public static int CountTokens(ReadOnlySpan<byte> line, int start, int end)
        {
            int count = 0;
            int i = start;
            while (i < end)
            {
                if (IsWhitespace(line[i]))
                {
                    i++;
                    continue;
                }

                count++;
                i = SkipToken(line, i);
            }

            return count;
        }
    }
}
