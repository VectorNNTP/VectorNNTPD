using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.TransitService.Tests.NntpDb;

public sealed class MySqlNntpDbConnectionTests
{
    [Fact]
    public async Task OpenAsync_MalformedConnectionString_ThrowsConfigurationException()
    {
        var factory = new MySqlNntpDbConnectionFactory();
        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => factory.OpenAsync(TestHostFactory.MalformedNntpDbConnectionString, CancellationToken.None));

        Assert.Contains("NntpDB connection string is invalid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("initialization string", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public async Task OpenAsync_MalformedConnectionString_DoesNotExposePassword()
    {
        var factory = new MySqlNntpDbConnectionFactory();
        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => factory.OpenAsync(
                TestHostFactory.MalformedNntpDbConnectionStringWithPassword,
                CancellationToken.None));

        Assert.DoesNotContain(TestHostFactory.FakeNntpDbPassword, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.FakeNntpDbPassword, ex.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.FakeNntpDbPassword, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConvertSelectOneScalar_AcceptsInt32()
    {
        Assert.Equal(1, MySqlNntpDbConnection.ConvertSelectOneScalar(1));
    }

    [Fact]
    public void ConvertSelectOneScalar_AcceptsMySqlInt64()
    {
        Assert.Equal(1, MySqlNntpDbConnection.ConvertSelectOneScalar(1L));
    }

    [Theory]
    [InlineData((ulong)4_294_967_295)]
    public void ConvertUnsignedCount_PreservesUInt32Max(ulong expected)
    {
        Assert.Equal(expected, MySqlNntpDbConnection.ConvertUnsignedCount(uint.MaxValue, "count_high", "g"));
    }

    [Fact]
    public void ConvertUnsignedCount_PreservesUInt64()
    {
        Assert.Equal(ulong.MaxValue, MySqlNntpDbConnection.ConvertUnsignedCount(ulong.MaxValue, "count_high", "g"));
    }

    [Fact]
    public void ConvertUnsignedCount_RejectsNegativeAndFloatingPoint()
    {
        Assert.Throws<VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueException>(
            () => MySqlNntpDbConnection.ConvertUnsignedCount(-1, "count_low", "g"));
        Assert.Throws<VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueException>(
            () => MySqlNntpDbConnection.ConvertUnsignedCount(1.5d, "count_low", "g"));
    }

    [Theory]
    [InlineData((byte)'y')]
    [InlineData((byte)'n')]
    [InlineData((byte)'m')]
    [InlineData((byte)'x')]
    [InlineData((byte)'j')]
    public void ConvertPostingStatus_AcceptsSingleOctet(byte status)
    {
        Assert.Equal(status, MySqlNntpDbConnection.ConvertPostingStatus(status, "g"));
        Assert.Equal(status, MySqlNntpDbConnection.ConvertPostingStatus(((char)status).ToString(), "g"));
        Assert.Equal(status, MySqlNntpDbConnection.ConvertPostingStatus((char)status, "g"));
    }

    [Theory]
    [InlineData((byte)'z')]
    [InlineData((byte)'Y')]
    [InlineData((byte)'=')]
    public void ConvertPostingStatus_RejectsUnknownAndEquals(byte status)
    {
        Assert.Throws<VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueException>(
            () => MySqlNntpDbConnection.ConvertPostingStatus(status, "g"));
        Assert.Throws<VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueException>(
            () => MySqlNntpDbConnection.ConvertPostingStatus(((char)status).ToString(), "g"));
    }

    [Fact]
    public void ConvertPostingStatus_RejectsEqualsAliasString()
    {
        var ex = Assert.Throws<VectorNNTPD.TransitService.Newsgroups.NewsgroupCatalogueException>(
            () => MySqlNntpDbConnection.ConvertPostingStatus("=other.group", "g"));
        Assert.Contains("=<newsgroup>", ex.Message, StringComparison.Ordinal);
    }
}
