using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;

namespace VectorNNTPD.Common.Tests.Networking.Transport
{
    /// <summary>
    /// Focused regressions for <see cref="NntpConnectionStream"/> pipe ownership and cancellation.
    /// </summary>
    public sealed class NntpConnectionStreamTests
    {
        /// <summary>
        /// <see cref="PipeReader.CancelPendingRead"/> must still pair with <c>AdvanceTo</c> so a later
        /// read can observe newly written bytes.
        /// </summary>
        [Fact]
        public async Task ReadAsync_CancelPendingRead_advances_and_subsequent_read_works()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            duplex.InboundReader.CancelPendingRead();
            var cancelBuffer = new byte[4];
#pragma warning disable CA2022 // Exception path: read is expected to throw before filling the buffer.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await stream.ReadAsync(cancelBuffer));
#pragma warning restore CA2022

            await duplex.Inbound.WriteAsync(new byte[] { 9 });
            await duplex.Inbound.FlushAsync();
            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer.AsMemory()));
            Assert.Equal(9, buffer[0]);
        }

        /// <summary>
        /// Cancellation while data is buffered must not consume those bytes; the next read returns them.
        /// </summary>
        [Fact]
        public async Task ReadAsync_cancel_with_buffered_data_does_not_skip_bytes()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            await duplex.Inbound.WriteAsync(new byte[] { 1, 2, 3, 4 });
            await duplex.Inbound.FlushAsync();
            duplex.InboundReader.CancelPendingRead();

            var cancelBuffer = new byte[4];
#pragma warning disable CA2022 // Exception path: read is expected to throw before filling the buffer.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await stream.ReadAsync(cancelBuffer));
#pragma warning restore CA2022

            var buffer = new byte[4];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset));
                Assert.True(read > 0);
                offset += read;
            }

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
        }

        /// <summary>Peer-complete (empty completed read) returns 0 and stays at EOF.</summary>
        [Fact]
        public async Task ReadAsync_completed_empty_pipe_returns_zero()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            await duplex.Inbound.CompleteAsync();
            Assert.Equal(0, await stream.ReadAsync(new byte[8]));
            Assert.Equal(0, await stream.ReadAsync(new byte[8]));
        }

        /// <summary>Partial consume leaves remainder for the next read.</summary>
        [Fact]
        public async Task ReadAsync_partial_consume_preserves_remainder()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            await duplex.Inbound.WriteAsync(new byte[] { 10, 20, 30 });
            await duplex.Inbound.FlushAsync();

            var first = new byte[2];
            Assert.Equal(2, await stream.ReadAsync(first));
            Assert.Equal(new byte[] { 10, 20 }, first);

            var second = new byte[2];
            Assert.Equal(1, await stream.ReadAsync(second));
            Assert.Equal(30, second[0]);
        }

        /// <summary>Write into a completed output pipe fails without hanging.</summary>
        [Fact]
        public async Task WriteAsync_completed_output_throws_IOException()
        {
            await using var duplex = new PipeDuplex();
            await duplex.OutboundReader.CompleteAsync();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            await Assert.ThrowsAsync<IOException>(
                async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }));
        }

        /// <summary>Large write chunks and is fully readable from the outbound pipe.</summary>
        [Fact]
        public async Task WriteAsync_large_payload_is_fully_flushed()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            var payload = new byte[200 * 1024];
            Random.Shared.NextBytes(payload);
            var readTask = ReadExactAsync(duplex.OutboundReader, payload.Length);

            await stream.WriteAsync(payload);

            var got = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, got);
        }

        /// <summary>Pre-cancelled write token fails without hanging the caller.</summary>
        [Fact]
        public async Task WriteAsync_pre_cancelled_token_throws()
        {
            await using var duplex = new PipeDuplex();
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token));
        }

        /// <summary>
        /// After a chunk is <c>Advance</c>d, completing the outbound reader fails the in-flight
        /// flush, but already-committed bytes remain — the adapter cannot roll them back.
        /// </summary>
        [Fact]
        public async Task WriteAsync_failure_after_advance_leaves_committed_bytes_readable()
        {
            var options = new PipeOptions(
                pauseWriterThreshold: 64,
                resumeWriterThreshold: 32,
                useSynchronizationContext: false);
            await using var duplex = new PipeDuplex(options);
            await using var stream = new NntpConnectionStream(duplex, ownsConnection: false);

            var payload = new byte[256];
            payload.AsSpan().Fill(0xAB);
            var writeTask = stream.WriteAsync(payload).AsTask();

            // First Advance is visible; examine without consuming so FlushAsync stays paused.
            var first = await duplex.OutboundReader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.Buffer.IsEmpty);
            var committed = (int)first.Buffer.Length;
            var committedBytes = new byte[committed];
            first.Buffer.Slice(0, committed).CopyTo(committedBytes);
            duplex.OutboundReader.AdvanceTo(first.Buffer.Start, first.Buffer.End);

            await duplex.OutboundReader.CompleteAsync();

            await Assert.ThrowsAsync<IOException>(
                () => writeTask.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(committed > 0);
            Assert.All(committedBytes, b => Assert.Equal((byte)0xAB, b));
        }

        private static async Task<byte[]> ReadExactAsync(PipeReader reader, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var result = await reader.ReadAsync();
                var sequence = result.Buffer;
                var toCopy = (int)Math.Min(sequence.Length, count - offset);
                sequence.Slice(0, toCopy).CopyTo(buffer.AsMemory(offset).Span);
                offset += toCopy;
                reader.AdvanceTo(sequence.GetPosition(toCopy));
                if (toCopy == 0 && result.IsCompleted)
                {
                    throw new EndOfStreamException();
                }
            }

            return buffer;
        }

        private sealed class PipeDuplex : INntpConnection
        {
            private readonly Pipe _inbound;
            private readonly Pipe _outbound;
            private readonly CancellationTokenSource _cts = new();
            private int _completed;

            public PipeDuplex(PipeOptions? options = null)
            {
                options ??= new PipeOptions(useSynchronizationContext: false);
                _inbound = new Pipe(options);
                _outbound = new Pipe(options);
                ClientIdentity = ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 0));
            }

            public PipeWriter Inbound => _inbound.Writer;

            public PipeReader InboundReader => _inbound.Reader;

            public PipeReader OutboundReader => _outbound.Reader;

            public PipeReader Input => _inbound.Reader;

            public PipeWriter Output => _outbound.Writer;

            public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

            public EndPoint? LocalEndPoint => null;

            public ConnectionClientIdentity ClientIdentity { get; }

            public bool IsTls => false;

            public bool IsCompressed => false;

            public CancellationToken ConnectionClosed => _cts.Token;

            public bool IsCompleted => Volatile.Read(ref _completed) == 1;

            public long OutboundIdleVersion => 0;

            public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
            {
                tlsVersion = string.Empty;
                cipher = string.Empty;
                return false;
            }

            public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task WaitForOutboundDeliveryAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task WaitForOutboundDeliveryAndPauseReadsAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CompleteAsync(Exception? exception = null)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    _cts.Cancel();
                }

                return Task.CompletedTask;
            }

            public Task UpgradeToTlsAsync(
                ITlsCertificateContextProvider certificateProvider,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async ValueTask DisposeAsync()
            {
                await _inbound.Writer.CompleteAsync();
                await _inbound.Reader.CompleteAsync();
                await _outbound.Writer.CompleteAsync();
                await _outbound.Reader.CompleteAsync();
                _cts.Dispose();
            }
        }
    }
}
