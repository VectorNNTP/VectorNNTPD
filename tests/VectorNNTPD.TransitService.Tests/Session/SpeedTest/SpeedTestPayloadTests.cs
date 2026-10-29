using System.Runtime.InteropServices;
using VectorNNTPD.TransitService.Session.SpeedTest;

namespace VectorNNTPD.TransitService.Tests.Session.SpeedTest;

public sealed class SpeedTestPayloadTests
{
    [Fact]
    public void Chunk_IsBoundedAndReusable()
    {
        Assert.Equal(64 * 1024, SpeedTestPayload.ChunkBytes);
        Assert.Equal(SpeedTestPayload.ChunkBytes, SpeedTestPayload.Chunk.Length);
        Assert.True(MemoryMarshal.TryGetArray(SpeedTestPayload.Chunk, out var first));
        Assert.True(MemoryMarshal.TryGetArray(SpeedTestPayload.Chunk, out var second));
        Assert.Same(first.Array, second.Array);
    }

    [Fact]
    public void Lines_NeverStartWithDot()
    {
        var span = SpeedTestPayload.Chunk.Span;
        for (var offset = 0; offset < span.Length; offset += SpeedTestPayload.LineBytes)
        {
            Assert.NotEqual((byte)'.', span[offset]);
            Assert.Equal((byte)'#', span[offset]);
            Assert.Equal((byte)'\r', span[offset + SpeedTestPayload.LineBytes - 2]);
            Assert.Equal((byte)'\n', span[offset + SpeedTestPayload.LineBytes - 1]);
        }
    }

    [Fact]
    public void Take_IsCrlfAligned_AndEmptyBelowOneLine()
    {
        Assert.True(SpeedTestPayload.Take(SpeedTestPayload.LineBytes - 1).IsEmpty);
        Assert.Equal(SpeedTestPayload.LineBytes, SpeedTestPayload.Take(SpeedTestPayload.LineBytes).Length);
        Assert.Equal(SpeedTestPayload.LineBytes, SpeedTestPayload.Take(SpeedTestPayload.LineBytes + 100).Length);
        Assert.Equal(SpeedTestPayload.ChunkBytes, SpeedTestPayload.Take(SpeedTestPayload.ChunkBytes).Length);
        Assert.Equal(SpeedTestPayload.ChunkBytes, SpeedTestPayload.Take(SpeedTestPayload.ChunkBytes + 1).Length);
    }
}
