using VectorNNTPD.TransitService.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

public sealed class SocketPayloadSenderTests
{
    [Fact]
    public async Task SendAllAsync_LoopsUntilEntireBufferSent()
    {
        var payload = new byte[100];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var scripted = new Queue<int>([37, 41, 22]);
        var transmitted = new List<byte>(payload.Length);

        await SocketPayloadSender.SendAllAsync(
            (memory, _, _) =>
            {
                Assert.False(memory.IsEmpty);
                var take = scripted.Dequeue();
                Assert.True(take <= memory.Length);
                transmitted.AddRange(memory.Span[..take].ToArray());
                return ValueTask.FromResult(take);
            },
            payload,
            state: null,
            CancellationToken.None);

        Assert.Empty(scripted);
        Assert.Equal(payload, transmitted);
    }

    [Fact]
    public async Task SendAllAsync_ZeroByteSend_ThrowsWithoutSpinning()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<IOException>(async () =>
            await SocketPayloadSender.SendAllAsync(
                (_, _, _) =>
                {
                    calls++;
                    return ValueTask.FromResult(0);
                },
                new byte[16],
                state: null,
                CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Contains("zero bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAllAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SocketPayloadSender.SendAllAsync(
                static (_, _, _) => ValueTask.FromResult(1),
                new byte[8],
                state: null,
                cts.Token));
    }

    [Fact]
    public async Task SendAllAsync_DoesNotAdvancePastFailure_WhenUsedWithPipeSemantics()
    {
        // Simulate the connection send contract: only mark consumed after full success.
        var payload = new byte[10];
        Random.Shared.NextBytes(payload);
        var sent = new List<byte>();
        var calls = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
            await SocketPayloadSender.SendAllAsync(
                (memory, _, _) =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        sent.AddRange(memory.Span[..4].ToArray());
                        return ValueTask.FromResult(4);
                    }

                    return ValueTask.FromResult(0);
                },
                payload,
                state: null,
                CancellationToken.None));

        Assert.Equal(4, sent.Count);
        Assert.Equal(payload.AsSpan(0, 4).ToArray(), sent.ToArray());
    }

    [Fact]
    public async Task SendAllAsync_ThrowAfterPartial_PropagatesWithoutClaimingSuccess()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        var sent = 0;

        var ex = await Assert.ThrowsAsync<IOException>(async () =>
            await SocketPayloadSender.SendAllAsync(
                (memory, _, _) =>
                {
                    if (sent > 0)
                    {
                        throw new IOException("peer reset after partial send");
                    }

                    sent += memory.Length >= 3 ? 3 : memory.Length;
                    return ValueTask.FromResult(3);
                },
                payload,
                state: null,
                CancellationToken.None));

        Assert.Equal(3, sent);
        Assert.Contains("peer reset", ex.Message, StringComparison.Ordinal);
    }
}
