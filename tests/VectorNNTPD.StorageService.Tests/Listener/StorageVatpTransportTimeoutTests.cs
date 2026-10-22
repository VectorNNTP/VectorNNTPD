using VectorNNTPD.StorageService.Listener;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    public sealed class StorageVatpTransportTimeoutTests
    {
        [Fact]
        public async Task Successive_reads_complete()
        {
            await using var transport = new StreamStorageVatpTransport(new ScriptedStream(2), TimeSpan.FromSeconds(2));
            Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
            Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
        }

        [Fact]
        public async Task Second_stalled_read_still_times_out_after_a_successful_read()
        {
            await using var transport = new StreamStorageVatpTransport(new ScriptedStream(1), TimeSpan.FromMilliseconds(80));
            Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await transport.ReadAsync(new byte[4], CancellationToken.None));
        }

        [Fact]
        public async Task Write_during_a_blocked_read_does_not_cancel_the_read()
        {
            var stream = new HoldThenReleaseStream();
            await using var transport = new StreamStorageVatpTransport(stream, TimeSpan.FromSeconds(2));
            var read = transport.ReadAsync(new byte[4], CancellationToken.None).AsTask();
            await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.WriteAsync(new byte[] { 9, 8, 7 }, CancellationToken.None);
            Assert.False(read.IsCompleted);
            Assert.Equal(3, stream.Written);
            stream.ReleaseRead(1);
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public async Task Suspended_read_does_not_time_out_until_resumed()
        {
            var stream = new HoldThenReleaseStream();
            await using var transport = new StreamStorageVatpTransport(stream, TimeSpan.FromMilliseconds(80));
            var read = transport.ReadAsync(new byte[4], CancellationToken.None).AsTask();
            await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.SetReadProgressSuspended(true);

            // The delay is the subject: a suspended read must outlive the no-progress timeout.
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.False(read.IsCompleted);
            transport.SetReadProgressSuspended(false);
            await Assert.ThrowsAsync<TimeoutException>(() => read);
        }

        [Fact]
        public async Task Stalled_write_times_out_without_cancelling_a_suspended_read()
        {
            var stream = new HoldThenReleaseStream { BlockWrites = true };
            await using var transport = new StreamStorageVatpTransport(stream, TimeSpan.FromMilliseconds(80));
            var read = transport.ReadAsync(new byte[4], CancellationToken.None).AsTask();
            await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.SetReadProgressSuspended(true);
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await transport.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));
            Assert.False(read.IsCompleted);
            stream.ReleaseRead(1);
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public async Task Caller_cancellation_is_not_reported_as_an_idle_timeout()
        {
            await using var transport = new StreamStorageVatpTransport(new ScriptedStream(0), TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await transport.ReadAsync(new byte[4], cts.Token));
        }

        [Fact]
        public async Task Independent_transports_keep_separate_timeout_state_and_share_process_io_counters()
        {
            // Io-split counters are intentionally process-wide; timeout CTS ownership stays per instance.
            StreamStorageVatpTransport.ResetIoSplit();
            await using var left = new StreamStorageVatpTransport(new ScriptedStream(2), TimeSpan.FromSeconds(2));
            var rightStream = new MemoryStream();
            rightStream.Write([1, 2, 3]);
            rightStream.Position = 0;
            await using var right = new StreamStorageVatpTransport(rightStream, TimeSpan.FromSeconds(2));

            Assert.Equal(1, await left.ReadAsync(new byte[4], CancellationToken.None));
            Assert.Equal(3, await right.ReadAsync(new byte[4], CancellationToken.None));
            await right.WriteAsync(new byte[] { 9 }, CancellationToken.None);

            var (timeoutSeconds, streamReadSeconds) = StreamStorageVatpTransport.ReadIoSplit();
            Assert.True(timeoutSeconds >= 0);
            Assert.True(streamReadSeconds >= 0);
            Assert.True(timeoutSeconds + streamReadSeconds > 0);

            var tls = StreamStorageVatpTransport.ReadTlsThreadCost();
            Assert.True(tls.ReadSameThread + tls.ReadHops >= 2);
            Assert.True(tls.WriteSameThread + tls.WriteHops >= 1);

            await left.DisposeAsync();
            Assert.Equal(0, await right.ReadAsync(new byte[4], CancellationToken.None));

            StreamStorageVatpTransport.ResetIoSplit();
            var afterReset = StreamStorageVatpTransport.ReadIoSplit();
            Assert.Equal(0, afterReset.TimeoutSetupSeconds);
            Assert.Equal(0, afterReset.StreamReadSeconds);
        }

        [Fact]
        public async Task Concurrent_reads_on_separate_transports_do_not_corrupt_instance_lifecycle()
        {
            // One outstanding read per transport (contract); both transports may run in parallel.
            StreamStorageVatpTransport.ResetIoSplit();
            await using var left = new StreamStorageVatpTransport(new ScriptedStream(8), TimeSpan.FromSeconds(2));
            await using var right = new StreamStorageVatpTransport(new ScriptedStream(8), TimeSpan.FromSeconds(2));

            for (var i = 0; i < 8; i++)
            {
                var pair = await Task.WhenAll(
                    left.ReadAsync(new byte[4], CancellationToken.None).AsTask(),
                    right.ReadAsync(new byte[4], CancellationToken.None).AsTask());
                Assert.Equal(1, pair[0]);
                Assert.Equal(1, pair[1]);
            }

            var tls = StreamStorageVatpTransport.ReadTlsThreadCost();
            Assert.Equal(16, tls.ReadSameThread + tls.ReadHops);
        }

        private sealed class ScriptedStream : Stream
        {
            private readonly int _immediateReads;
            private int _reads;

            public ScriptedStream(int immediateReads) => _immediateReads = immediateReads;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_reads++ < _immediateReads)
                {
                    buffer.Span[0] = 1;
                    return 1;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }

        private sealed class HoldThenReleaseStream : Stream
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _releaseByte;

            public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Written { get; private set; }

            public bool BlockWrites { get; init; }

            public void ReleaseRead(int value)
            {
                _releaseByte = value;
                _release.TrySetResult();
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => Written += count;

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (BlockWrites)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                Written += buffer.Length;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                buffer.Span[0] = (byte)_releaseByte;
                return 1;
            }
        }
    }
}
