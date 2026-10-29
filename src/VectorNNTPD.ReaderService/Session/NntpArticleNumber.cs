namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>Validates RFC 3977 <c>article-number</c> tokens (<c>1*16DIGIT</c>).</summary>
    public static class NntpArticleNumber
    {
        /// <summary>Maximum article-number length in octets (RFC 3977 §9.2).</summary>
        public const int MaxDigits = 16;

        /// <summary>
        /// Returns whether <paramref name="value"/> is a syntactically valid article number.
        /// Does not imply that an article exists.
        /// </summary>
        public static bool IsSyntax(ReadOnlySpan<byte> value)
        {
            if (value.Length is < 1 or > MaxDigits)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var b = value[i];
                if (b is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
