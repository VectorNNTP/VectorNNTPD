namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

/// <summary>
/// Deterministic regression coverage for <c>ConnectionByteTransport</c> quiescence admission.
/// </summary>
public sealed class ConnectionByteTransportQuiescenceTests
{
    [Fact]
    public async Task AfterPauseReads_TryCommitReadToApplication_StashesClientHello()
    {
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        await transport.PauseReadsAsync(CancellationToken.None);

        var clientHello = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x04, 0x01, 0x00, 0x00, 0x00 };
        Assert.False(transport.TryCommitReadToApplication(clientHello));

        var prefix = transport.TakePendingUpgradePrefix();
        Assert.Equal(clientHello, prefix.ToArray());
    }

    [Fact]
    public async Task Quiesce_DuringHistoricalAdmitWindow_DoesNotStartStreamIoAfterIdle()
    {
        // Reproduces the pre-fix window: WaitUntilActive observed Active, then Quiesce ran and
        // declared idle before outstanding++. With atomic admission, stream I/O must not start.
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        var quiesceCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterActiveBeforeAdmitProbe = async () =>
        {
            await transport.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
            quiesceCompleted.TrySetResult();
        };

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readTask = transport.ReadAsync(new byte[16], readCts.Token).AsTask();

        await quiesceCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.ReadStarts);
        Assert.Equal(0, stream.ConcurrentOps);

        Assert.False(readTask.IsCompleted);
        transport.AbortQuiesceForConnectionTeardown();
        await Assert.ThrowsAnyAsync<Exception>(() => readTask);
        Assert.Equal(0, stream.ReadStarts);
    }

    [Fact]
    public async Task Quiesce_WaitsForAdmittedRead_UntilStreamReadReturns()
    {
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReadReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnReadAsync = async ct =>
        {
            readEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancel was requested, but the operation still owns the stream until we return.
                cancelObserved.TrySetResult();
                await allowReadReturn.Task.ConfigureAwait(false);
                throw;
            }

            return 0;
        };

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readTask = transport.ReadAsync(new byte[8], readCts.Token).AsTask();
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var quiesceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var quiesceTask = transport.QuiesceAsync(quiesceCts.Token);

        await cancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(quiesceTask.IsCompleted);
        Assert.Equal(1, stream.ConcurrentOps);

        allowReadReturn.TrySetResult();
        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.ConcurrentOps);
        await Assert.ThrowsAnyAsync<Exception>(() => readTask);
    }

    [Fact]
    public async Task Quiesce_WaitsForAdmittedWrite_UntilStreamWriteReturns()
    {
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = async _ =>
        {
            writeEntered.TrySetResult();
            await releaseWrite.Task.ConfigureAwait(false);
        };

        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writeTask = transport.WriteAsync(new byte[] { 1, 2, 3 }, writeCts.Token).AsTask();
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var quiesceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var quiesceTask = transport.QuiesceAsync(quiesceCts.Token);
        await Task.Yield();
        Assert.False(quiesceTask.IsCompleted);
        Assert.Equal(1, stream.ConcurrentOps);

        releaseWrite.TrySetResult();
        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(5));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.ConcurrentOps);
    }

    [Fact]
    public async Task QuiescedIdle_SimulatedHandshake_CannotOverlapPlaintextStreamIo()
    {
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        await transport.QuiesceAsync(CancellationToken.None);
        Assert.Same(transport.RateLimiter, transport.TakeQuiescedStreamForTlsWrap());
        Assert.Equal(0, stream.ConcurrentOps);

        stream.ArmPostQuiesceTracking();

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blockedRead = transport.ReadAsync(new byte[16], readCts.Token).AsTask();
        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blockedWrite = transport.WriteAsync(new byte[] { 1 }, writeCts.Token).AsTask();

        // Ownership window equivalent to AuthenticateAsServerAsync: stream must stay untouched.
        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(0, stream.PostQuiesceIoStarts);
            Assert.Equal(0, stream.ConcurrentOps);
            Assert.False(blockedRead.IsCompleted);
            Assert.False(blockedWrite.IsCompleted);
            await Task.Yield();
        }

        transport.AbortQuiesceForConnectionTeardown();
        await Assert.ThrowsAnyAsync<Exception>(() => blockedRead);
        await Assert.ThrowsAnyAsync<Exception>(() => blockedWrite);
        Assert.Equal(0, stream.PostQuiesceIoStarts);
        Assert.Equal(0, stream.ReadStarts);
        Assert.Equal(0, stream.WriteStarts);
    }

    [Fact]
    public async Task Quiesce_RepeatedAdmitRace_NeverStartsIoAfterIdle()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            await using var stream = new ControllableStream();
            await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
                stream,
                isTls: false);

            var quiesced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AfterActiveBeforeAdmitProbe = async () =>
            {
                await transport.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
                quiesced.TrySetResult();
            };

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var readTask = transport.ReadAsync(new byte[8], readCts.Token).AsTask();
            await quiesced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, stream.ReadStarts);
            Assert.Equal(0, stream.ConcurrentOps);

            transport.AbortQuiesceForConnectionTeardown();
            await Assert.ThrowsAnyAsync<Exception>(() => readTask);
            Assert.Equal(0, stream.ReadStarts);
        }
    }

    [Fact]
    public async Task QuiescedIdle_WriteAdmitRace_NeverStartsWriteAfterIdle()
    {
        await using var stream = new ControllableStream();
        await using var transport = new VectorNNTPD.Common.Networking.Transport.ConnectionByteTransport(
            stream,
            isTls: false);

        var quiesced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterActiveBeforeAdmitProbe = async () =>
        {
            await transport.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
            quiesced.TrySetResult();
        };

        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writeTask = transport.WriteAsync(new byte[] { 9 }, writeCts.Token).AsTask();
        await quiesced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stream.WriteStarts);
        Assert.Equal(0, stream.ConcurrentOps);

        transport.AbortQuiesceForConnectionTeardown();
        await Assert.ThrowsAnyAsync<Exception>(() => writeTask);
        Assert.Equal(0, stream.WriteStarts);
    }

    private sealed class ControllableStream : Stream
    {
        private int _concurrentOps;
        private int _readStarts;
        private int _writeStarts;
        private int _postQuiesceIoStarts;
        private bool _trackPostQuiesce;

        public Func<CancellationToken, ValueTask<int>>? OnReadAsync { get; set; }
        public Func<CancellationToken, ValueTask>? OnWriteAsync { get; set; }

        public int ConcurrentOps => Volatile.Read(ref _concurrentOps);
        public int ReadStarts => Volatile.Read(ref _readStarts);
        public int WriteStarts => Volatile.Read(ref _writeStarts);
        public int PostQuiesceIoStarts => Volatile.Read(ref _postQuiesceIoStarts);

        public void ArmPostQuiesceTracking() => _trackPostQuiesce = true;

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

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readStarts);
            if (_trackPostQuiesce)
            {
                Interlocked.Increment(ref _postQuiesceIoStarts);
            }

            Interlocked.Increment(ref _concurrentOps);
            try
            {
                if (OnReadAsync is { } onRead)
                {
                    return await onRead(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentOps);
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeStarts);
            if (_trackPostQuiesce)
            {
                Interlocked.Increment(ref _postQuiesceIoStarts);
            }

            Interlocked.Increment(ref _concurrentOps);
            try
            {
                if (OnWriteAsync is { } onWrite)
                {
                    await onWrite(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentOps);
            }
        }
    }
}
