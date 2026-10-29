using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>Constant-time ASCII password comparison used by AUTHINFO PASS, PLAIN, and LOGIN.</summary>
    internal static class NntpPasswordComparer
    {
        private const int StackallocThresholdBytes = 4096;

        /// <summary>
        /// Returns <see langword="true"/> only when both strings are non-null ASCII of equal length
        /// and identical content.
        /// </summary>
        public static bool EqualsAscii(string? storedPassword, string? suppliedPassword)
        {
            if (storedPassword is null || suppliedPassword is null)
            {
                return false;
            }

            if (!IsAscii(storedPassword) || !IsAscii(suppliedPassword))
            {
                return false;
            }

            var storedLength = storedPassword.Length;
            var suppliedLength = suppliedPassword.Length;
            var maxLength = Math.Max(storedLength, suppliedLength);
            if (maxLength <= StackallocThresholdBytes)
            {
                Span<byte> left = stackalloc byte[maxLength];
                Span<byte> right = stackalloc byte[maxLength];
                left.Clear();
                right.Clear();
                Encoding.ASCII.GetBytes(storedPassword, left);
                Encoding.ASCII.GetBytes(suppliedPassword, right);
                return CryptographicOperations.FixedTimeEquals(left, right) && storedLength == suppliedLength;
            }

            var leftArray = ArrayPool<byte>.Shared.Rent(maxLength);
            var rightArray = ArrayPool<byte>.Shared.Rent(maxLength);
            try
            {
                var left = leftArray.AsSpan(0, maxLength);
                var right = rightArray.AsSpan(0, maxLength);
                left.Clear();
                right.Clear();
                Encoding.ASCII.GetBytes(storedPassword, left);
                Encoding.ASCII.GetBytes(suppliedPassword, right);
                return CryptographicOperations.FixedTimeEquals(left, right) && storedLength == suppliedLength;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(leftArray, clearArray: true);
                ArrayPool<byte>.Shared.Return(rightArray, clearArray: true);
            }
        }

        /// <summary>Returns whether <paramref name="value"/> contains only ASCII code points.</summary>
        public static bool IsAscii(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var c in value)
            {
                if (c > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
