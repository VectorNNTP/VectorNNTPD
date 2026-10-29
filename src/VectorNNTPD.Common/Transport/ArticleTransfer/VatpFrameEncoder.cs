using System.Buffers.Binary;

namespace VectorNNTPD.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Deterministic encoders for VATP v1 frames. Payload-bearing frames keep header and
    /// payload as separate memories so transports can gather-write without copying.
    /// </summary>
    internal static class VatpFrameEncoder
    {
        /// <summary>Header plus optional payload memories (no frame-sized concat).</summary>
        internal readonly record struct EncodedFrame(ReadOnlyMemory<byte> Header, ReadOnlyMemory<byte> Payload);

        /// <summary>Encodes a HELLO header+payload into caller buffers or returns owned memories.</summary>
        internal static EncodedFrame EncodeHello(uint maxFramePayload)
        {
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (maxFramePayload == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFramePayload));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            var payload = new byte[VatpProtocol.HelloPayloadLength];
            VatpProtocol.HelloMagic.CopyTo(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), maxFramePayload);
            return Encode(VatpFrameType.Hello, VatpProtocol.ConnectionStreamId, payload, flags: 0);
        }

        /// <summary>Encodes OPEN with RequestId GUID and 32-byte ArticleId.</summary>
        internal static EncodedFrame EncodeOpen(uint streamId, Guid requestId, ReadOnlySpan<byte> articleId)
        {
            ThrowIfConnectionStream(streamId);
            if (articleId.Length != VatpProtocol.ArticleIdLength)
            {
                throw new ArgumentException($"ArticleId must be {VatpProtocol.ArticleIdLength} bytes.", nameof(articleId));
            }

            var payload = new byte[VatpProtocol.OpenPayloadLength];
            if (!requestId.TryWriteBytes(payload.AsSpan(0, VatpProtocol.RequestIdLength)))
            {
                throw new InvalidOperationException("Failed to write RequestId GUID bytes.");
            }

            articleId.CopyTo(payload.AsSpan(VatpProtocol.RequestIdLength, VatpProtocol.ArticleIdLength));
            return Encode(VatpFrameType.Open, streamId, payload, flags: 0);
        }

        /// <summary>Encodes STORE with a 32-byte ArticleId.</summary>
        internal static EncodedFrame EncodeStore(uint streamId, ReadOnlySpan<byte> articleId)
        {
            ThrowIfConnectionStream(streamId);
            if (articleId.Length != VatpProtocol.StorePayloadLength)
            {
                throw new ArgumentException(
                    $"ArticleId must be {VatpProtocol.StorePayloadLength} bytes.",
                    nameof(articleId));
            }

            var payload = new byte[VatpProtocol.StorePayloadLength];
            articleId.CopyTo(payload);
            return Encode(VatpFrameType.Store, streamId, payload, flags: 0);
        }

        /// <summary>Encodes RESULT with one <c>ArticleAcceptOutcome</c> byte.</summary>
        internal static EncodedFrame EncodeResult(uint streamId, byte outcome)
        {
            ThrowIfConnectionStream(streamId);
            var payload = new byte[VatpProtocol.ResultPayloadLength];
            payload[0] = outcome;
            return Encode(VatpFrameType.Result, streamId, payload, flags: 0);
        }

        /// <summary>Encodes META from a prebuilt 76-byte payload.</summary>
        internal static EncodedFrame EncodeMeta(uint streamId, ReadOnlyMemory<byte> metaPayload)
        {
            ThrowIfConnectionStream(streamId);
            if (metaPayload.Length != VatpProtocol.MetaPayloadLength)
            {
                throw new ArgumentException($"META payload must be {VatpProtocol.MetaPayloadLength} bytes.", nameof(metaPayload));
            }

            return Encode(VatpFrameType.Meta, streamId, metaPayload, flags: 0);
        }

        /// <summary>Encodes DATA with optional FIN. Payload memory is not copied.</summary>
        internal static EncodedFrame EncodeData(uint streamId, ReadOnlyMemory<byte> payload, bool fin = false)
        {
            ThrowIfConnectionStream(streamId);
            // ReadOnlyMemory<byte>.Length is int; Encode checks payload length via checked uint cast.
            var flags = fin ? VatpProtocol.FlagFin : 0u;
            return Encode(VatpFrameType.Data, streamId, payload, flags);
        }

        /// <summary>Encodes an empty END frame.</summary>
        internal static EncodedFrame EncodeEnd(uint streamId)
        {
            ThrowIfConnectionStream(streamId);
            return Encode(VatpFrameType.End, streamId, ReadOnlyMemory<byte>.Empty, flags: 0);
        }

        /// <summary>Encodes CANCEL.</summary>
        internal static EncodedFrame EncodeCancel(uint streamId)
        {
            ThrowIfConnectionStream(streamId);
            return Encode(VatpFrameType.Cancel, streamId, ReadOnlyMemory<byte>.Empty, flags: 0);
        }

        /// <summary>Encodes WINDOW credit add.</summary>
        internal static EncodedFrame EncodeWindow(uint streamId, uint addCredit)
        {
            ThrowIfConnectionStream(streamId);
            var payload = new byte[VatpProtocol.WindowPayloadLength];
            BinaryPrimitives.WriteUInt32BigEndian(payload, addCredit);
            return Encode(VatpFrameType.Window, streamId, payload, flags: 0);
        }

        /// <summary>Encodes FAIL with error code and optional ASCII reason.</summary>
        internal static EncodedFrame EncodeFail(uint streamId, VatpErrorCode errorCode, ReadOnlySpan<byte> asciiReason = default)
        {
            if (asciiReason.Length > VatpProtocol.FailMaxReasonLength)
            {
                throw new ArgumentException(
                    $"FAIL reason must be at most {VatpProtocol.FailMaxReasonLength} bytes.",
                    nameof(asciiReason));
            }

            var payload = new byte[VatpProtocol.FailMinPayloadLength + asciiReason.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)errorCode);
            asciiReason.CopyTo(payload.AsSpan(2));
            return Encode(VatpFrameType.Fail, streamId, payload, flags: 0);
        }

        /// <summary>Writes only the 16-byte header into <paramref name="destination"/>.</summary>
        private static void WriteHeader(
            Span<byte> destination,
            VatpFrameType type,
            uint streamId,
            uint payloadLength,
            uint flags = 0) =>
            VatpFrameHeader.Create(type, streamId, payloadLength, flags).WriteTo(destination);

        /// <summary>Encodes header + payload as separate memories.</summary>
        internal static EncodedFrame Encode(
            VatpFrameType type,
            uint streamId,
            ReadOnlyMemory<byte> payload,
            uint flags)
        {
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            VatpFrameHeader.Create(type, streamId, checked((uint)payload.Length), flags).WriteTo(header);
            return new EncodedFrame(header, payload);
        }

        /// <summary>Concatenates header and payload into one owned buffer (tests / small frames).</summary>
        internal static byte[] ToSingleBuffer(in EncodedFrame frame)
        {
            var result = new byte[frame.Header.Length + frame.Payload.Length];
            frame.Header.Span.CopyTo(result);
            frame.Payload.Span.CopyTo(result.AsSpan(frame.Header.Length));
            return result;
        }

        /// <summary>Rejects the connection-level stream id.</summary>
        /// <param name="streamId">Stream id from a transfer frame.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="streamId"/> is <see cref="VatpProtocol.ConnectionStreamId"/> (0).</exception>
        private static void ThrowIfConnectionStream(uint streamId)
        {
            if (streamId == VatpProtocol.ConnectionStreamId)
            {
                throw new ArgumentOutOfRangeException(nameof(streamId), "Transfer frames require StreamId != 0.");
            }
        }
    }
}
