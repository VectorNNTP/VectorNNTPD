using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Newsgroups;

public sealed class NewsgroupSnapshotTests
{
    [Fact]
    public void Create_MapsFields_PreservesUnsignedCountsAndPostingStatus()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition(
                "misc.test",
                "General testing",
                CountHigh: 4_294_967_295UL,
                CountLow: 1,
                NewsgroupPostingStatus.Moderated),
        ]);

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal("misc.test", group.GroupName);
        Assert.Equal("General testing", group.GroupDescription);
        Assert.Equal(4_294_967_295UL, group.CountHigh);
        Assert.Equal(1UL, group.CountLow);
        Assert.Equal(NewsgroupPostingStatus.Moderated, group.PostingStatus);
        Assert.Equal(4_294_967_295UL, group.EstimatedCount);
    }

    [Fact]
    public void Create_SortsDeterministically_ByOrdinalIgnoreCaseThenOrdinal()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("comp.risks", "", 2, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("alt.test", "", 2, 1, NewsgroupPostingStatus.Prohibited),
            new NewsgroupDefinition("Misc.Test", "", 2, 1, NewsgroupPostingStatus.Moderated),
        ]);

        Assert.Equal(["alt.test", "comp.risks", "Misc.Test"], snapshot.Groups.Select(g => g.GroupName));
    }

    [Fact]
    public void TryGet_IsOrdinalCaseInsensitive_AndPreservesOriginalName()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.TEST", "desc", 10, 2, NewsgroupPostingStatus.Allowed),
        ]);

        Assert.True(snapshot.TryGet("MISC.test"u8, out var group));
        Assert.Equal("misc.TEST", group.GroupName);
        Assert.True(group.GroupNameBytes.Span.SequenceEqual("misc.TEST"u8));
    }

    [Theory]
    [InlineData(NewsgroupPostingStatus.Allowed, (byte)'y', "group.example 100 1 y\r\n")]
    [InlineData(NewsgroupPostingStatus.Prohibited, (byte)'n', "group.example 100 1 n\r\n")]
    [InlineData(NewsgroupPostingStatus.Moderated, (byte)'m', "group.example 100 1 m\r\n")]
    [InlineData(NewsgroupPostingStatus.NoPostingOrPeerArticles, (byte)'x', "group.example 100 1 x\r\n")]
    [InlineData(NewsgroupPostingStatus.PeerOnly, (byte)'j', "group.example 100 1 j\r\n")]
    public void ListActiveLine_EmitsEachSupportedStatusOctet(
        NewsgroupPostingStatus status,
        byte expectedOctet,
        string expectedLine)
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("group.example", "", 100, 1, status),
        ]);

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(status, group.PostingStatus);
        Assert.Equal(expectedOctet, (byte)group.PostingStatus);
        Assert.True(group.ListActiveLine.Span.SequenceEqual(Encoding.ASCII.GetBytes(expectedLine)));
    }

    [Fact]
    public void ListActiveLine_IsExactUtf8WithCrlf()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "ignored", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
        ]);

        var line = Assert.Single(snapshot.Groups).ListActiveLine;
        Assert.True(line.Span.SequenceEqual("misc.test 3002322 3000234 y\r\n"u8));
        Assert.True(snapshot.ListActiveComplete.Span.SequenceEqual(
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "misc.test 3002322 3000234 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator)));
        Assert.True(Assert.Single(snapshot.Groups).ListCountsLine.Span.SequenceEqual(
            "misc.test 3002322 3000234 2089 y\r\n"u8));
        Assert.True(snapshot.ListCountsComplete.Span.SequenceEqual(
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "misc.test 3002322 3000234 2089 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator)));
    }

    [Fact]
    public void ListNewsgroupsLine_EncodesUtf8Description()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "Café résumé £", 1, 1, NewsgroupPostingStatus.Allowed),
        ]);

        var expected = Encode("misc.test Café résumé £\r\n");
        Assert.True(Assert.Single(snapshot.Groups).ListNewsgroupsLine.Span.SequenceEqual(expected));
        Assert.True(snapshot.ListNewsgroupsComplete.Span.SequenceEqual(
            Concat(NntpResponses.ListOfNewsgroupsFollows, expected, NntpResponses.MultilineTerminator)));
    }

    [Fact]
    public void PrecomputedLines_AreStableReferences()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("a.test", "d", 2, 1, NewsgroupPostingStatus.Allowed),
        ]);

        var group = Assert.Single(snapshot.Groups);
        Assert.True(group.ListActiveLine.Equals(snapshot.Groups[0].ListActiveLine));
        Assert.True(snapshot.ListActiveComplete.Equals(snapshot.ListActiveComplete));
        Assert.True(MemoryMarshal.TryGetArray(group.ListActiveLine, out var first));
        Assert.True(MemoryMarshal.TryGetArray(snapshot.Groups[0].ListActiveLine, out var second));
        Assert.Same(first.Array, second.Array);
    }

    [Fact]
    public void Create_FromDatabaseRows_UsesExactQueryFields()
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NntpGroupRow("tx.natives.recovery", "Texas", 89, 56, (byte)'n'),
        ]);

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(NewsgroupPostingStatus.Prohibited, group.PostingStatus);
        Assert.True(group.ListActiveLine.Span.SequenceEqual("tx.natives.recovery 89 56 n\r\n"u8));
    }

    [Theory]
    [InlineData((byte)'x', NewsgroupPostingStatus.NoPostingOrPeerArticles, "group.example 100 1 x\r\n")]
    [InlineData((byte)'j', NewsgroupPostingStatus.PeerOnly, "group.example 100 1 j\r\n")]
    public void Create_PreservesExtendedStatus_AndListActiveBytes(
        byte statusOctet,
        NewsgroupPostingStatus expected,
        string expectedLine)
    {
        var snapshot = NewsgroupSnapshot.Create(
        [
            new NntpGroupRow("group.example", "", 100, 1, statusOctet),
        ]);

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(expected, group.PostingStatus);
        Assert.Equal(statusOctet, (byte)group.PostingStatus);
        Assert.True(group.ListActiveLine.Span.SequenceEqual(Encoding.ASCII.GetBytes(expectedLine)));
    }

    [Fact]
    public void Create_RejectsInvalidPostingStatus_AndDoesNotPublishPartial()
    {
        var ex = Assert.Throws<NewsgroupCatalogueException>(() => NewsgroupSnapshot.Create(
        [
            new NntpGroupRow("good.group", "", 1, 1, (byte)'y'),
            new NntpGroupRow("bad.group", "", 1, 1, (byte)'z'),
        ]));
        Assert.Contains("posting_status", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsEqualsAliasForm()
    {
        var ex = Assert.Throws<NewsgroupCatalogueException>(() => NewsgroupSnapshot.Create(
        [
            new NntpGroupRow("alias.group", "", 1, 1, (byte)'='),
        ]));
        Assert.Contains("posting_status", ex.Message, StringComparison.Ordinal);
        Assert.False(NewsgroupPostingStatusOctets.IsSupported((byte)'='));
    }

    [Fact]
    public void Create_RejectsDuplicateCaseInsensitiveNames()
    {
        Assert.Throws<NewsgroupCatalogueException>(() => NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "", 1, 1, NewsgroupPostingStatus.Allowed),
            new NewsgroupDefinition("MISC.TEST", "", 2, 1, NewsgroupPostingStatus.Allowed),
        ]));
    }

    [Fact]
    public void PublishedSnapshot_RemainsUnchanged_WhenAnotherIsConstructed()
    {
        var first = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("old.group", "old", 5, 1, NewsgroupPostingStatus.Allowed),
        ]);
        var captured = first;
        var second = NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("new.group", "new", 9, 2, NewsgroupPostingStatus.Moderated),
        ]);

        Assert.Equal("old.group", Assert.Single(captured.Groups).GroupName);
        Assert.Equal("new.group", Assert.Single(second.Groups).GroupName);
        Assert.True(captured.ListActiveComplete.Span.SequenceEqual(
            Concat(
                NntpResponses.ListOfNewsgroupsFollows,
                "old.group 5 1 y\r\n"u8.ToArray(),
                NntpResponses.MultilineTerminator)));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0UL)]
    [InlineData(5UL, 5UL, 1UL)]
    [InlineData(4000UL, 3999UL, 0UL)]
    [InlineData(234UL, 567UL, 334UL)]
    [InlineData(0UL, ulong.MaxValue, ulong.MaxValue)]
    public void EstimateCount_FollowsRfcEmptyAndSpanRules(ulong low, ulong high, ulong expected)
    {
        Assert.Equal(expected, NewsgroupSnapshot.EstimateCount(low, high));
    }

    [Fact]
    public void Empty_HasTerminatorOnlyListBodies()
    {
        Assert.Empty(NewsgroupSnapshot.Empty.Groups);
        Assert.True(NewsgroupSnapshot.Empty.ListActiveComplete.Span.SequenceEqual(
            Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator)));
        Assert.True(NewsgroupSnapshot.Empty.ListCountsComplete.Span.SequenceEqual(
            Concat(NntpResponses.ListOfNewsgroupsFollows, NntpResponses.MultilineTerminator)));
    }

    private static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Concat(ReadOnlyMemory<byte> first, ReadOnlySpan<byte> second, ReadOnlyMemory<byte> third)
    {
        var result = new byte[first.Length + second.Length + third.Length];
        first.Span.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        third.Span.CopyTo(result.AsSpan(first.Length + second.Length));
        return result;
    }

    private static byte[] Concat(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        var result = new byte[first.Length + second.Length];
        first.Span.CopyTo(result);
        second.Span.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}
