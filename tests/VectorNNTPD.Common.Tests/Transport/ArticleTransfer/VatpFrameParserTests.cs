using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// <see cref="VatpFrameParser.ParseOneFrame(byte[], uint)"/> completeness, framing boundaries, stream-id rules, and oversized declared payloads.
    /// </summary>
    public sealed class VatpFrameParserTests
    {
        /// <summary>
        /// A complete END frame parses as success, consumes 16 bytes, and preserves type and stream id 42.
        /// </summary>
        [Fact]
        public void ParseOneFrame_CompleteFrame_Succeeds()
        {
            var encoded = VatpFrameEncoder.EncodeEnd(42);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(16, result.ConsumedBytes);
            Assert.Equal(VatpFrameType.End, result.Frame!.Value.Header.Type);
            Assert.Equal(42u, result.Frame.Value.Header.StreamId);
        }

        /// <summary>
        /// Eight leading header bytes are <see cref="VatpFrameParseStatus.Incomplete"/> and consume nothing.
        /// </summary>
        [Fact]
        public void ParseOneFrame_PartialHeader_IsIncomplete()
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1));
            var result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, 8).ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
            Assert.Equal(0, result.ConsumedBytes);
        }

        /// <summary>
        /// A DATA header plus four of 32 declared payload bytes is <see cref="VatpFrameParseStatus.Incomplete"/>.
        /// </summary>
        [Fact]
        public void ParseOneFrame_PartialPayload_IsIncomplete()
        {
            var payload = new byte[32];
            var encoded = VatpFrameEncoder.EncodeData(1, payload);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, 20).ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
        }

        /// <summary>
        /// Feeding a WINDOW frame one byte at a time stays incomplete until the final byte, which succeeds and consumes the whole frame.
        /// </summary>
        [Fact]
        public void ParseOneFrame_ByteAtATime_EventuallySucceeds()
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(9, 1024));
            VatpFrameParseResult result = default;
            for (var i = 1; i <= frame.Length; i++)
            {
                result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, i).ToArray(), VatpProtocol.DefaultMaxFramePayload);
                if (i < frame.Length)
                {
                    Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
                }
            }

            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(frame.Length, result.ConsumedBytes);
        }

        /// <summary>
        /// A CANCEL frame split into segments of <paramref name="chunkSize"/> still parses as success.
        /// </summary>
        /// <param name="chunkSize">Maximum bytes per <see cref="System.Buffers.ReadOnlySequence{T}"/> segment.</param>
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(15)]
        [InlineData(17)]
        public void ParseOneFrame_FragmentedSequence_Succeeds(int chunkSize)
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(3));
            var sequence = VatpTestArticles.Fragment(frame, chunkSize);
            var result = VatpFrameParser.ParseOneFrame(in sequence, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(VatpFrameType.Cancel, result.Frame!.Value.Header.Type);
        }

        /// <summary>
        /// After a complete DATA frame, a following partial DATA frame is left <see cref="VatpFrameParseStatus.Incomplete"/> and the first parse consumes only the complete frame.
        /// </summary>
        [Fact]
        public void ParseOneFrame_CompleteThenPartial_LeavesIncompleteTail()
        {
            var complete = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(1, new byte[64], fin: false));
            var next = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(1, new byte[64], fin: true));
            var buffer = VatpTestArticles.Concat(complete, next.AsSpan(0, 20).ToArray());

            var one = VatpFrameParser.ParseOneFrame(buffer, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, one.Status);
            Assert.Equal(complete.Length, one.ConsumedBytes);

            var remaining = buffer.AsMemory((int)one.ConsumedBytes).ToArray();
            var two = VatpFrameParser.ParseOneFrame(remaining, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, two.Status);
        }

        /// <summary>
        /// A declared payload length of <see cref="uint.MaxValue"/> is <see cref="VatpFrameParseStatus.Invalid"/> with <see cref="VatpErrorCode.FrameTooLarge"/> against a 1024-byte maximum.
        /// </summary>
        [Fact]
        public void ParseOneFrame_MalformedPayloadLengthOverflow_Rejected()
        {
            var header = new byte[16];
            // PayloadLength = uint.MaxValue would overflow Header+Payload as checked long path;
            // parser rejects via FrameTooLarge against maxFramePayload first.
            VatpFrameHeader.Create(VatpFrameType.Data, 1, payloadLength: uint.MaxValue).WriteTo(header);
            var result = VatpFrameParser.ParseOneFrame(header, maxFramePayload: 1024);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.FrameTooLarge, result.Error);
        }

        /// <summary>
        /// Two frames in one buffer are parsed independently: the first END on stream 1, then the remaining CANCEL on stream 2.
        /// </summary>
        [Fact]
        public void ParseOneFrame_MultipleFramesInOneBuffer_ParsesFirstThenSecond()
        {
            var first = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1));
            var second = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(2));
            var buffer = VatpTestArticles.Concat(first, second);

            var one = VatpFrameParser.ParseOneFrame(buffer, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, one.Status);
            Assert.Equal(1u, one.Frame!.Value.Header.StreamId);

            var remaining = buffer.AsMemory((int)one.ConsumedBytes);
            var two = VatpFrameParser.ParseOneFrame(remaining.ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, two.Status);
            Assert.Equal(2u, two.Frame!.Value.Header.StreamId);
            Assert.Equal(VatpFrameType.Cancel, two.Frame.Value.Header.Type);
        }

        /// <summary>
        /// An END frame on stream 0 is rejected as <see cref="VatpErrorCode.InvalidStreamId"/>.
        /// </summary>
        [Fact]
        public void ParseOneFrame_TransferFrameOnStreamZero_Rejected()
        {
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.End, 0, 0).WriteTo(header);
            var result = VatpFrameParser.ParseOneFrame(header, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpErrorCode.InvalidStreamId, result.Error);
        }

        /// <summary>
        /// A HELLO frame whose stream id is rewritten to 1 is rejected as <see cref="VatpErrorCode.InvalidStreamId"/>.
        /// </summary>
        [Fact]
        public void ParseOneFrame_HelloOnNonZeroStream_Rejected()
        {
            var hello = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(64 * 1024));
            hello[4] = 0;
            hello[5] = 0;
            hello[6] = 0;
            hello[7] = 1; // StreamId = 1
            var result = VatpFrameParser.ParseOneFrame(hello, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpErrorCode.InvalidStreamId, result.Error);
        }

        /// <summary>
        /// A header that declares a 5 MiB payload, with no payload bytes present, is <see cref="VatpErrorCode.FrameTooLarge"/> and allocates less than 64 KiB on the calling thread.
        /// </summary>
        [Fact]
        public void ParseOneFrame_DoesNotAllocateArticleSizedBufferForOversizedDeclaredPayload()
        {
            // Declares a huge payload but only supplies a header; must be Incomplete, not allocate.
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.Data, 1, payloadLength: 5 * 1024 * 1024).WriteTo(header);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = VatpFrameParser.ParseOneFrame(header, VatpProtocol.DefaultMaxFramePayload);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.FrameTooLarge, result.Error);
            Assert.True(allocated < 64 * 1024, $"allocated {allocated}");
        }

        /// <summary>
        /// Empty DATA (including non-FIN) remains accepted at the frame-parser boundary; stream state rejects empty non-FIN.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParseOneFrame_EmptyData_Succeeds_AtParserBoundary(bool fin)
        {
            var encoded = VatpFrameEncoder.EncodeData(1, ReadOnlyMemory<byte>.Empty, fin);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(VatpFrameType.Data, result.Frame!.Value.Header.Type);
            Assert.Equal(0u, result.Frame!.Value.Header.PayloadLength);
            Assert.Equal(fin, result.Frame!.Value.Header.HasFin);
        }
    }
}
