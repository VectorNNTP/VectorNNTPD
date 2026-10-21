using System.Buffers.Binary;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// HELLO payload encode and decode, including magic, length, and maximum-frame checks.
    /// </summary>
    public sealed class VatpHelloTests
    {
        /// <summary>
        /// An encoded HELLO on stream 0 decodes to <see cref="VatpProtocol.DefaultMaxFramePayload"/> with <see cref="VatpErrorCode.None"/>.
        /// </summary>
        [Fact]
        public void EncodeDecode_ValidHello_Succeeds()
        {
            var encoded = VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var parsed = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
            Assert.Equal(VatpFrameType.Hello, parsed.Frame!.Value.Header.Type);
            Assert.Equal(0u, parsed.Frame.Value.Header.StreamId);

            Assert.True(VatpHello.TryDecode(parsed.Frame.Value.Payload, out var hello, out var error));
            Assert.Equal(VatpErrorCode.None, error);
            Assert.Equal(VatpProtocol.DefaultMaxFramePayload, hello.MaxFramePayload);
        }

        /// <summary>
        /// ParseOneFrame accepts a correctly encoded HELLO (valid magic and non-zero maxFramePayload).
        /// </summary>
        [Fact]
        public void ParseOneFrame_ValidHello_Succeeds()
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(1024));
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(VatpErrorCode.None, result.Error);
            Assert.Equal(frame.Length, result.ConsumedBytes);
            Assert.True(VatpHello.TryDecode(result.Frame!.Value.Payload, out var hello, out var decodeError));
            Assert.Equal(VatpErrorCode.None, decodeError);
            Assert.Equal(1024u, hello.MaxFramePayload);
        }

        /// <summary>
        /// A 12-byte all-zero HELLO payload is rejected by the parser (magic is not <c>VNATP01\0</c>).
        /// </summary>
        [Fact]
        public void ParseOneFrame_AllZeroHelloPayload_IsRejected()
        {
            var frame = BuildHelloFrame(new byte[VatpProtocol.HelloPayloadLength]);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.InvalidHello, result.Error);
            Assert.Equal(frame.Length, result.ConsumedBytes);
        }

        /// <summary>
        /// Replacing the first magic byte rejects the payload as <see cref="VatpErrorCode.InvalidHello"/>.
        /// </summary>
        [Fact]
        public void Decode_BadMagic_Fails()
        {
            Span<byte> payload = stackalloc byte[12];
            VatpHello.Encode(payload, 1024);
            payload[0] = (byte)'X';
            Assert.False(VatpHello.TryDecode(payload, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidHello, error);
        }

        /// <summary>
        /// ParseOneFrame rejects a HELLO whose magic does not match <see cref="VatpProtocol.HelloMagic"/>.
        /// </summary>
        [Theory]
        [InlineData(0, (byte)'X')]
        [InlineData(7, (byte)0x01)]
        public void ParseOneFrame_InvalidHelloMagic_IsRejected(int magicIndex, byte replacement)
        {
            Span<byte> payload = stackalloc byte[VatpProtocol.HelloPayloadLength];
            VatpHello.Encode(payload, 1024);
            payload[magicIndex] = replacement;
            var frame = BuildHelloFrame(payload);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.InvalidHello, result.Error);
        }

        /// <summary>
        /// A 12-byte payload with the HELLO magic and a maximum frame payload of zero is <see cref="VatpErrorCode.InvalidMaxFramePayload"/>.
        /// </summary>
        [Fact]
        public void Decode_ZeroMaxFrame_Fails()
        {
            Span<byte> payload = stackalloc byte[12];
            VatpProtocol.HelloMagic.CopyTo(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8, 4), 0);
            Assert.False(VatpHello.TryDecode(payload, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidMaxFramePayload, error);
        }

        /// <summary>
        /// ParseOneFrame rejects a HELLO whose advertised maxFramePayload is zero.
        /// </summary>
        [Fact]
        public void ParseOneFrame_ZeroMaxFramePayload_IsRejected()
        {
            Span<byte> payload = stackalloc byte[VatpProtocol.HelloPayloadLength];
            VatpProtocol.HelloMagic.CopyTo(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8, 4), 0);
            var frame = BuildHelloFrame(payload);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.InvalidMaxFramePayload, result.Error);
            Assert.Equal(frame.Length, result.ConsumedBytes);
        }

        /// <summary>
        /// A payload shorter than the HELLO layout is <see cref="VatpErrorCode.InvalidHello"/>.
        /// </summary>
        [Fact]
        public void Decode_Truncated_Fails()
        {
            Assert.False(VatpHello.TryDecode("VNATP01"u8, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidHello, error);
        }

        /// <summary>
        /// Truncated and oversized HELLO declared lengths are rejected as <see cref="VatpErrorCode.InvalidHello"/>.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        [InlineData(11)]
        [InlineData(13)]
        public void ParseOneFrame_HelloWrongPayloadLength_IsRejected(int payloadLength)
        {
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.Hello, 0, (uint)payloadLength).WriteTo(header);
            var frame = VatpTestArticles.Concat(header, new byte[payloadLength]);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.InvalidHello, result.Error);
        }

        /// <summary>
        /// A HELLO frame whose payload length is 8 instead of 12 is rejected by the frame parser as <see cref="VatpErrorCode.InvalidHello"/>.
        /// </summary>
        [Fact]
        public void Parser_HelloWrongPayloadLength_Fails()
        {
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.Hello, 0, 8).WriteTo(header);
            var frame = VatpTestArticles.Concat(header, new byte[8]);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpErrorCode.InvalidHello, result.Error);
        }

        /// <summary>
        /// ParseOneFrame and <see cref="VatpHello.TryDecode(ReadOnlySpan{byte}, out VatpHello.HelloPayload, out VatpErrorCode)"/>
        /// agree on validity and error codes for representative HELLO payloads.
        /// </summary>
        [Theory]
        [InlineData("valid", VatpErrorCode.None)]
        [InlineData("all-zero", VatpErrorCode.InvalidHello)]
        [InlineData("bad-magic", VatpErrorCode.InvalidHello)]
        [InlineData("zero-max", VatpErrorCode.InvalidMaxFramePayload)]
        public void ParseOneFrame_And_TryDecode_Agree_On_HelloPayloadValidity(string kind, VatpErrorCode expectedError)
        {
            Span<byte> payload = stackalloc byte[VatpProtocol.HelloPayloadLength];
            switch (kind)
            {
                case "valid":
                    VatpHello.Encode(payload, VatpProtocol.DefaultMaxFramePayload);
                    break;
                case "all-zero":
                    payload.Clear();
                    break;
                case "bad-magic":
                    VatpHello.Encode(payload, 2048);
                    payload[0] = (byte)'Z';
                    break;
                case "zero-max":
                    VatpProtocol.HelloMagic.CopyTo(payload);
                    BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8, 4), 0);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown HELLO fixture kind.");
            }

            var decodeOk = VatpHello.TryDecode(payload, out _, out var decodeError);
            var frame = BuildHelloFrame(payload);
            var parsed = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);

            if (expectedError == VatpErrorCode.None)
            {
                Assert.True(decodeOk);
                Assert.Equal(VatpErrorCode.None, decodeError);
                Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
                Assert.Equal(VatpErrorCode.None, parsed.Error);
            }
            else
            {
                Assert.False(decodeOk);
                Assert.Equal(expectedError, decodeError);
                Assert.Equal(VatpFrameParseStatus.Invalid, parsed.Status);
                Assert.Equal(expectedError, parsed.Error);
            }
        }

        /// <summary>Builds a stream-0 HELLO frame around an exact 12-byte payload.</summary>
        private static byte[] BuildHelloFrame(ReadOnlySpan<byte> payload)
        {
            Assert.Equal(VatpProtocol.HelloPayloadLength, payload.Length);
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.Hello, VatpProtocol.ConnectionStreamId, (uint)payload.Length)
                .WriteTo(header);
            return VatpTestArticles.Concat(header, payload.ToArray());
        }
    }
}
