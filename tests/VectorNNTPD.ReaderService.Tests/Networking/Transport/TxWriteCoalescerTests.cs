using System.Buffers;
using VectorNNTPD.ReaderService.Networking.Transport;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Exact-byte and flush-shape contracts for the diagnostic TX write coalescer.
/// </summary>
public sealed class TxWriteCoalescerTests
{
    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(128 * 1024)]
    [InlineData(256 * 1024)]
    [InlineData(512 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public void Copy_ExactTarget_FillsOnce_PreservesBytes(int target)
    {
        var coalescer = new TxWriteCoalescer(target);
        var source = Pattern(target, seed: 7);
        Assert.Equal(target, coalescer.Copy(source, io: null));
        Assert.True(coalescer.IsFull);
        Assert.Equal(0, coalescer.RemainingCapacity);
        Assert.True(source.AsSpan().SequenceEqual(coalescer.PendingMemory.Span));
        coalescer.Clear();
        Assert.Equal(0, coalescer.PendingCount);
    }

    [Fact]
    public void Copy_MultipleSegments_PreservesOrder_NoDuplicationOrLoss()
    {
        const int target = 256 * 1024;
        var coalescer = new TxWriteCoalescer(target);
        var expected = Pattern(target, seed: 11);
        var copied = 0;
        foreach (var chunk in expected.Chunk(64 * 1024))
        {
            copied += coalescer.Copy(chunk, io: null);
        }

        Assert.Equal(target, copied);
        Assert.True(coalescer.IsFull);
        Assert.True(expected.AsSpan().SequenceEqual(coalescer.PendingMemory.Span));
    }

    [Fact]
    public void Copy_PartialFinalAggregate_LeavesRemainder()
    {
        var coalescer = new TxWriteCoalescer(128 * 1024);
        var first = Pattern(64 * 1024, seed: 3);
        var tail = Pattern(100, seed: 9);
        Assert.Equal(first.Length, coalescer.Copy(first, io: null));
        Assert.False(coalescer.IsFull);
        Assert.Equal(tail.Length, coalescer.Copy(tail, io: null));
        Assert.Equal(first.Length + tail.Length, coalescer.PendingCount);
        Assert.True(first.AsSpan().SequenceEqual(coalescer.PendingMemory.Span[..first.Length]));
        Assert.True(tail.AsSpan().SequenceEqual(coalescer.PendingMemory.Span[first.Length..]));
    }

    [Fact]
    public void Copy_DoesNotOverflow_WhenSegmentExceedsRemaining()
    {
        var coalescer = new TxWriteCoalescer(64 * 1024);
        var oversized = Pattern((64 * 1024) + 16, seed: 5);
        Assert.Equal(64 * 1024, coalescer.Copy(oversized, io: null));
        Assert.True(coalescer.IsFull);
        Assert.Equal(0, coalescer.Copy(oversized.AsSpan(64 * 1024), io: null));
        Assert.True(oversized.AsSpan(0, 64 * 1024).SequenceEqual(coalescer.PendingMemory.Span));
    }

    [Fact]
    public async Task Drain_FlushesFullAggregates_ThenPartialRemainder()
    {
        const int target = 128 * 1024;
        var coalescer = new TxWriteCoalescer(target);
        var payload = Pattern((128 * 1024) + 64, seed: 13);
        var sequence = ToSequence(payload.AsMemory(0, 64 * 1024), payload.AsMemory(64 * 1024, 64 * 1024), payload.AsMemory(128 * 1024));
        var writes = new List<byte[]>();

        await DrainAsync(sequence, coalescer, writes, flushRemaining: true, CancellationToken.None);

        Assert.Equal(2, writes.Count);
        Assert.Equal(target, writes[0].Length);
        Assert.Equal(64, writes[1].Length);
        Assert.Equal(payload, writes.SelectMany(static w => w).ToArray());
        Assert.Equal(0, coalescer.PendingCount);
    }

    [Fact]
    public async Task Drain_WithoutFlushRemaining_HoldsPartialAcrossCalls()
    {
        var coalescer = new TxWriteCoalescer(256 * 1024);
        var first = Pattern(64 * 1024, seed: 17);
        var second = Pattern(64 * 1024, seed: 19);
        var writes = new List<byte[]>();

        await DrainAsync(new ReadOnlySequence<byte>(first), coalescer, writes, flushRemaining: false, CancellationToken.None);
        Assert.Empty(writes);
        Assert.Equal(first.Length, coalescer.PendingCount);

        await DrainAsync(new ReadOnlySequence<byte>(second), coalescer, writes, flushRemaining: false, CancellationToken.None);
        Assert.Empty(writes);
        Assert.Equal(first.Length + second.Length, coalescer.PendingCount);

        await DrainAsync(ReadOnlySequence<byte>.Empty, coalescer, writes, flushRemaining: true, CancellationToken.None);
        Assert.Single(writes);
        Assert.Equal(first.Concat(second).ToArray(), writes[0]);
    }

    [Fact]
    public async Task Drain_TransportFailure_DoesNotClear_AndDoesNotWriteTwice()
    {
        var coalescer = new TxWriteCoalescer(64 * 1024);
        var payload = Pattern(64 * 1024, seed: 23);
        var writes = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await TxWriteCoalesceTestDrain.RunAsync(
                new ReadOnlySequence<byte>(payload),
                coalescer,
                writeAsync: (_, _) =>
                {
                    writes++;
                    throw new IOException("simulated transport failure");
                },
                flushRemaining: true,
                io: null,
                CancellationToken.None);
        });

        Assert.Equal(1, writes);
        Assert.Equal(payload.Length, coalescer.PendingCount);
    }

    [Fact]
    public async Task Drain_Cancellation_DoesNotWrite()
    {
        var coalescer = new TxWriteCoalescer(64 * 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var writes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await TxWriteCoalesceTestDrain.RunAsync(
                new ReadOnlySequence<byte>(Pattern(64 * 1024, seed: 29)),
                coalescer,
                writeAsync: (_, token) =>
                {
                    writes++;
                    token.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                flushRemaining: true,
                io: null,
                cts.Token);
        });

        Assert.Equal(0, writes);
        Assert.Equal(0, coalescer.PendingCount);
    }

    [Fact]
    public async Task Drain_WriteCanceled_LeavesPendingUnwritten()
    {
        var coalescer = new TxWriteCoalescer(64 * 1024);
        var writes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await TxWriteCoalesceTestDrain.RunAsync(
                new ReadOnlySequence<byte>(Pattern(64 * 1024, seed: 29)),
                coalescer,
                writeAsync: (_, _) =>
                {
                    writes++;
                    throw new OperationCanceledException();
                },
                flushRemaining: true,
                io: null,
                CancellationToken.None);
        });

        Assert.Equal(1, writes);
        Assert.Equal(64 * 1024, coalescer.PendingCount);
    }

    [Fact]
    public void RecordCopy_IsMeasuredSeparately()
    {
        var io = new TransportIoSession();
        var coalescer = new TxWriteCoalescer(64 * 1024);
        var source = Pattern(64 * 1024, seed: 31);
        Assert.Equal(source.Length, coalescer.Copy(source, io));
        var snap = io.Snapshot();
        Assert.Equal(1, snap.Coalesce.CopyCount);
        Assert.Equal(source.Length, snap.Coalesce.CopyBytes);
        Assert.True(snap.Coalesce.CopyTime >= TimeSpan.Zero);
        Assert.Single(snap.Coalesce.CopyUs);
    }

    [Fact]
    public void Constructor_RejectsNonAllowlistedTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TxWriteCoalescer(96 * 1024));
    }

    private static async Task DrainAsync(
        ReadOnlySequence<byte> buffer,
        TxWriteCoalescer coalescer,
        List<byte[]> writes,
        bool flushRemaining,
        CancellationToken cancellationToken)
    {
        await TxWriteCoalesceTestDrain.RunAsync(
            buffer,
            coalescer,
            writeAsync: (memory, _) =>
            {
                writes.Add(memory.ToArray());
                return ValueTask.CompletedTask;
            },
            flushRemaining,
            io: null,
            cancellationToken);
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i * 31));
        }

        return bytes;
    }

    private static ReadOnlySequence<byte> ToSequence(params ReadOnlyMemory<byte>[] parts)
    {
        if (parts.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (parts.Length == 1)
        {
            return new ReadOnlySequence<byte>(parts[0]);
        }

        var first = new BufferSegment(parts[0]);
        var last = first;
        for (var i = 1; i < parts.Length; i++)
        {
            last = last.Append(parts[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }
}

/// <summary>
/// Test-only drain that mirrors <c>NntpConnection</c> aggregate copy/write without a live pipe.
/// </summary>
internal static class TxWriteCoalesceTestDrain
{
    public static async ValueTask RunAsync(
        ReadOnlySequence<byte> buffer,
        TxWriteCoalescer coalescer,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeAsync,
        bool flushRemaining,
        TransportIoSession? io,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var segment in buffer)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var offset = 0;
            while (offset < segment.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (coalescer.RemainingCapacity == 0)
                {
                    await writeAsync(coalescer.PendingMemory, cancellationToken).ConfigureAwait(false);
                    coalescer.Clear();
                }

                offset += coalescer.Copy(segment.Span[offset..], io);
            }
        }

        if (coalescer.IsFull || (flushRemaining && coalescer.PendingCount > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writeAsync(coalescer.PendingMemory, cancellationToken).ConfigureAwait(false);
            coalescer.Clear();
        }
    }
}
