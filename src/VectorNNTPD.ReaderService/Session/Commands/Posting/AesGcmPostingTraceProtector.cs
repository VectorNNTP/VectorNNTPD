using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>
    /// AES-256-GCM protector for POST <c>X-Trace</c> (envelope version <c>v1</c>).
    /// </summary>
    /// <remarks>
    /// Token envelope: <c>v1.</c> followed by Base64url of nonce, ciphertext, and tag.
    /// Envelope version is independent of the inner payload version and of key rotation.
    /// Inner payload version 1 is the pre-username layout (still decryptable).
    /// Inner payload version 2 appends a big-endian u16 length and UTF-8 username.
    /// <see cref="Protect"/> always writes payload version 2 with the current key.
    /// <see cref="TryUnprotect"/> tries the current key, then the optional previous key.
    /// This type does not log keys, tokens, or decrypted payloads.
    /// </remarks>
    public sealed class AesGcmPostingTraceProtector : IPostingTraceProtector
    {
        /// <summary>Header-safe envelope prefix for the current token version.</summary>
        public const string TokenPrefix = "v1.";

        private const int NonceLength = 12;
        private const int TagLength = 16;
        private const byte LegacyPayloadVersion = 1;
        private const byte CurrentPayloadVersion = 2;
        internal const int MaxAuthenticatedUsernameBytes = 256;
        private static readonly byte[] AssociatedData = "VectorNNTP.XTrace.v1"u8.ToArray();
        private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly byte[] _currentKey;
        private readonly byte[]? _previousKey;

        /// <summary>Creates a protector from already-decoded AES-256 keys.</summary>
        public AesGcmPostingTraceProtector(byte[] currentKey, byte[]? previousKey = null)
        {
            ArgumentNullException.ThrowIfNull(currentKey);
            if (currentKey.Length != XTraceKeyParser.KeyLength)
            {
                throw new ArgumentException("X-Trace key must be 32 bytes.", nameof(currentKey));
            }

            if (previousKey is not null && previousKey.Length != XTraceKeyParser.KeyLength)
            {
                throw new ArgumentException("X-Trace previous key must be 32 bytes.", nameof(previousKey));
            }

            _currentKey = currentKey;
            _previousKey = previousKey;
        }

        /// <summary>Creates a protector from validated <see cref="NntpdOptions"/>.</summary>
        public static AesGcmPostingTraceProtector Create(NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!XTraceKeyParser.TryDecode(options.XTraceKey, out var current))
            {
                throw new InvalidOperationException("XTraceKey is missing or is not a 32-byte AES-256 key.");
            }

            byte[]? previous = null;
            if (!string.IsNullOrWhiteSpace(options.XTracePreviousKey)
                && !XTraceKeyParser.TryDecode(options.XTracePreviousKey, out previous))
            {
                throw new InvalidOperationException("XTracePreviousKey is not a 32-byte AES-256 key.");
            }

            return new AesGcmPostingTraceProtector(current, previous);
        }

        /// <summary>Creates a protector from options monitored at resolve time.</summary>
        public static AesGcmPostingTraceProtector Create(IOptions<NntpdOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(options.Value);
        }

        /// <inheritdoc />
        public string Protect(PostingTracePayload payload) => EncryptPacked(EncodePayload(payload));

        /// <inheritdoc />
        public bool TryUnprotect(ReadOnlySpan<char> token, out PostingTracePayload payload)
        {
            payload = default;
            if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var encoded = token[TokenPrefix.Length..];
            var max = Base64Url.GetMaxDecodedLength(encoded.Length);
            if (max < NonceLength + TagLength + 1)
            {
                return false;
            }

            Span<byte> packed = max <= 256 ? stackalloc byte[max] : new byte[max];
            int written;
            try
            {
                if (!Base64Url.TryDecodeFromChars(encoded, packed, out written)
                    || written < NonceLength + TagLength + 1)
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            packed = packed[..written];
            var nonce = packed[..NonceLength];
            var tag = packed[^TagLength..];
            var ciphertext = packed[NonceLength..^TagLength];
            return TryDecrypt(nonce, ciphertext, tag, _currentKey, out payload)
                || (_previousKey is not null && TryDecrypt(nonce, ciphertext, tag, _previousKey, out payload));
        }

        private static bool TryDecrypt(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            byte[] key,
            out PostingTracePayload payload)
        {
            payload = default;
            Span<byte> plaintext = stackalloc byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            }
            catch (CryptographicException)
            {
                return false;
            }

            return TryDecodePayload(plaintext, out payload);
        }

        /// <summary>
        /// Protects a pre-username payload (inner version 1) for backward-compatibility tests.
        /// Production <see cref="Protect"/> always writes inner version 2.
        /// </summary>
        internal string ProtectLegacyV1(PostingTracePayload payload) =>
            EncryptPacked(EncodeLegacyV1(payload));

        /// <summary>Encrypts caller-supplied plaintext for malformed-payload tests.</summary>
        internal string ProtectRawForTests(ReadOnlySpan<byte> plaintext) => EncryptPacked(plaintext);

        private string EncryptPacked(ReadOnlySpan<byte> plaintext)
        {
            Span<byte> nonce = stackalloc byte[NonceLength];
            RandomNumberGenerator.Fill(nonce);
            Span<byte> ciphertext = stackalloc byte[plaintext.Length];
            Span<byte> tag = stackalloc byte[TagLength];
            using (var aes = new AesGcm(_currentKey, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            }

            Span<byte> packed = stackalloc byte[NonceLength + plaintext.Length + TagLength];
            nonce.CopyTo(packed);
            ciphertext.CopyTo(packed[NonceLength..]);
            tag.CopyTo(packed[(NonceLength + plaintext.Length)..]);
            return TokenPrefix + Base64Url.EncodeToString(packed);
        }

        private static byte[] EncodePayload(PostingTracePayload payload)
        {
            var usernameBytes = EncodeUsername(payload.AuthenticatedUsername);
            var address = payload.Address.GetAddressBytes();
            var length = FixedFieldLength(address.Length) + 2 + usernameBytes.Length;
            var buffer = new byte[length];
            var offset = WriteFixedFields(buffer, CurrentPayloadVersion, payload, address);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
                buffer.AsSpan(offset, 2),
                (ushort)usernameBytes.Length);
            offset += 2;
            usernameBytes.CopyTo(buffer.AsSpan(offset));
            return buffer;
        }

        private static byte[] EncodeLegacyV1(PostingTracePayload payload)
        {
            var address = payload.Address.GetAddressBytes();
            var buffer = new byte[FixedFieldLength(address.Length)];
            WriteFixedFields(buffer, LegacyPayloadVersion, payload, address);
            return buffer;
        }

        private static int FixedFieldLength(int addressLength) => 1 + 1 + addressLength + 2 + 8 + 16;

        private static int WriteFixedFields(
            Span<byte> buffer,
            byte version,
            PostingTracePayload payload,
            ReadOnlySpan<byte> address)
        {
            var offset = 0;
            buffer[offset++] = version;
            buffer[offset++] = (byte)address.Length;
            address.CopyTo(buffer[offset..]);
            offset += address.Length;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buffer[offset..(offset + 2)], (ushort)payload.Port);
            offset += 2;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(
                buffer[offset..(offset + 8)],
                payload.InjectedAtUtc.ToUnixTimeSeconds());
            offset += 8;
            payload.TraceId.TryWriteBytes(buffer[offset..(offset + 16)]);
            return offset + 16;
        }

        private static byte[] EncodeUsername(string? username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return [];
            }

            var bytes = Utf8Strict.GetBytes(username);
            if (bytes.Length > MaxAuthenticatedUsernameBytes || ContainsNul(bytes))
            {
                throw new ArgumentException("Authenticated username is not encodable in X-Trace.", nameof(username));
            }

            return bytes;
        }

        private static bool ContainsNul(ReadOnlySpan<byte> value)
        {
            foreach (var b in value)
            {
                if (b == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryDecodePayload(ReadOnlySpan<byte> plaintext, out PostingTracePayload payload)
        {
            payload = default;
            if (plaintext.Length < 1 + 1 + 4 + 2 + 8 + 16)
            {
                return false;
            }

            var version = plaintext[0];
            var addressLength = plaintext[1];
            if (addressLength is not (4 or 16))
            {
                return false;
            }

            var fixedLength = FixedFieldLength(addressLength);
            if (plaintext.Length < fixedLength)
            {
                return false;
            }

            var offset = 2;
            IPAddress address;
            try
            {
                address = new IPAddress(plaintext.Slice(offset, addressLength));
            }
            catch (ArgumentException)
            {
                return false;
            }

            offset += addressLength;
            var port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(plaintext.Slice(offset, 2));
            offset += 2;
            var unix = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(plaintext.Slice(offset, 8));
            offset += 8;
            var traceId = new Guid(plaintext.Slice(offset, 16));
            offset += 16;
            DateTimeOffset injected;
            try
            {
                injected = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            string? username = null;
            if (version == LegacyPayloadVersion)
            {
                if (plaintext.Length != fixedLength)
                {
                    return false;
                }
            }
            else if (version == CurrentPayloadVersion)
            {
                if (plaintext.Length < fixedLength + 2)
                {
                    return false;
                }

                var userLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(plaintext.Slice(offset, 2));
                offset += 2;
                if (userLength > MaxAuthenticatedUsernameBytes || plaintext.Length != fixedLength + 2 + userLength)
                {
                    return false;
                }

                if (userLength > 0)
                {
                    var userBytes = plaintext.Slice(offset, userLength);
                    if (ContainsNul(userBytes))
                    {
                        return false;
                    }

                    try
                    {
                        username = Utf8Strict.GetString(userBytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }

            payload = new PostingTracePayload(address, port, injected, traceId, username);
            return true;
        }
    }
}
