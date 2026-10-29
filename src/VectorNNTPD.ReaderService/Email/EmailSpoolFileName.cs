using System.Security.Cryptography;

namespace VectorNNTPD.ReaderService.Email
{
    /// <summary>Generates opaque spool filenames: lowercase hex MD5 of a new UUID.</summary>
    internal static class EmailSpoolFileName
    {
        /// <summary>Creates a new 32-character lowercase hex identifier.</summary>
        public static string NewId()
        {
            var hash = MD5.HashData(Guid.NewGuid().ToByteArray());
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>Returns whether <paramref name="name"/> is a 32-character lowercase hex id.</summary>
        public static bool IsId(ReadOnlySpan<char> name)
        {
            if (name.Length != 32)
            {
                return false;
            }

            foreach (var ch in name)
            {
                var hex = ch is >= '0' and <= '9' or >= 'a' and <= 'f';
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
