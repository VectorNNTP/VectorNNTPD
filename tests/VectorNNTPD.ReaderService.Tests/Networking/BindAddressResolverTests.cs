using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking;

namespace VectorNNTPD.ReaderService.Tests.Networking;

public sealed class BindAddressResolverTests
{
    [Fact]
    public void Resolve_ExplicitIpv4AndIpv6_ReturnsBoth()
    {
        var v4 = IPAddress.Parse("198.18.0.66");
        var v6 = IPAddress.Parse("2001:db8::66");
        var resolver = CreateResolver(v4, v6);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [v4.ToString(), v6.ToString()];

        var resolved = resolver.Resolve(options);

        Assert.Equal(2, resolved.All.Count);
        Assert.Single(resolved.IPv4);
        Assert.Single(resolved.IPv6);
        Assert.Contains(v4, resolved.IPv4);
        Assert.Contains(v6, resolved.IPv6);
    }

    [Fact]
    public void Resolve_WildcardStar_ExpandsEligibleInterfaceAddresses()
    {
        var publicV4 = IPAddress.Parse("203.0.113.10");
        var privateV4 = IPAddress.Parse("10.0.0.5");
        var v6 = IPAddress.Parse("2001:db8::5");
        var loopback = IPAddress.Loopback;
        var linkLocal = IPAddress.Parse("169.254.10.10");
        var multicast = IPAddress.Parse("224.0.0.1");

        var resolver = CreateResolver(publicV4, privateV4, v6, loopback, linkLocal, multicast);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var resolved = resolver.Resolve(options);

        Assert.Equal(3, resolved.All.Count);
        Assert.Contains(publicV4, resolved.IPv4);
        Assert.Contains(privateV4, resolved.IPv4);
        Assert.Contains(v6, resolved.IPv6);
        Assert.DoesNotContain(loopback, resolved.All);
        Assert.DoesNotContain(linkLocal, resolved.All);
        Assert.DoesNotContain(multicast, resolved.All);
    }

    [Fact]
    public void Resolve_Ipv4AnyWildcard_ExpandsOnlyIpv4()
    {
        var v4 = IPAddress.Parse("198.18.0.20");
        var v6 = IPAddress.Parse("2001:db8::20");
        var resolver = CreateResolver(v4, v6);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["0.0.0.0"];

        var resolved = resolver.Resolve(options);

        Assert.Single(resolved.All);
        Assert.Equal(v4, resolved.IPv4[0]);
        Assert.Empty(resolved.IPv6);
    }

    [Fact]
    public void Resolve_Ipv6AnyWildcard_ExpandsOnlyIpv6()
    {
        var v4 = IPAddress.Parse("198.18.0.21");
        var v6 = IPAddress.Parse("2001:db8::21");
        var resolver = CreateResolver(v4, v6);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["::"];

        var resolved = resolver.Resolve(options);

        Assert.Single(resolved.All);
        Assert.Equal(v6, resolved.IPv6[0]);
        Assert.Empty(resolved.IPv4);
    }

    [Fact]
    public void Resolve_Duplicates_AreEliminated()
    {
        var v4 = IPAddress.Parse("198.18.0.30");
        var resolver = CreateResolver(v4);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [v4.ToString(), v4.ToString(), "*"];

        var resolved = resolver.Resolve(options);

        Assert.Single(resolved.All);
        Assert.Equal(v4, resolved.IPv4[0]);
    }

    [Fact]
    public void Resolve_ExplicitLoopbackOnly_YieldsEmptySet()
    {
        var resolver = CreateResolver(IPAddress.Loopback);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];

        var resolved = resolver.Resolve(options);

        Assert.False(resolved.HasAny);
        Assert.Empty(resolved.All);
    }

    [Fact]
    public void Resolve_NoEligibleAddressesOnWildcard_YieldsEmptySet()
    {
        var resolver = CreateResolver(IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("169.254.1.1"));
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var resolved = resolver.Resolve(options);

        Assert.False(resolved.HasAny);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("203.0.113.1", true)]
    [InlineData("2001:db8::1", true)]
    public void IsEligibleForDns_ClassifiesAddresses(string text, bool expected)
    {
        var address = IPAddress.Parse(text);
        Assert.Equal(expected, IpAddressEligibility.IsEligibleForDns(address));
    }

    private static BindAddressResolver CreateResolver(params IPAddress[] local)
    {
        return new BindAddressResolver(
            new FakeLocalIpAddressAssignee(local),
            NullLogger<BindAddressResolver>.Instance);
    }
}
