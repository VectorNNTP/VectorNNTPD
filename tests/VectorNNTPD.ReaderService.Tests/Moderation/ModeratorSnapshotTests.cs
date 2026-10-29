using System.Text;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Moderation;

public sealed class ModeratorSnapshotTests
{
    [Fact]
    public void FirstMatch_WinsOverLaterGeneralPattern()
    {
        var auth = ModeratorTestSnapshot.Create(
            ("comp.example.moderated", "specific@example.com", "mod-specific"),
            ("comp.example.*", "general@example.com", "mod-general"));

        Assert.True(auth.TryResolve(Bytes("comp.example.moderated"), out var identity));
        Assert.Equal("specific@example.com", identity.Address);
        Assert.Equal("mod-specific", identity.Username);
    }

    [Fact]
    public void Resolve_UnknownGroup_Fails()
    {
        var auth = ModeratorTestSnapshot.Create(("group.a", "a@example.com", "moderator-a"));
        Assert.False(auth.TryResolve(Bytes("group.b"), out _));
    }

    [Fact]
    public void CanApprove_RequiresMatchingUsernameAndAddress()
    {
        var auth = Standard();
        Assert.True(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("moderator-b@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorB, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.NormalUser, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(null, Bytes("moderator-a@example.com"), Bytes("group.a")));
    }

    [Fact]
    public void CanApprove_AddressComparison_IsCaseInsensitive()
    {
        var auth = Standard();
        Assert.True(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("Moderator-A@Example.COM"), Bytes("group.a")));
    }

    [Fact]
    public void UsernameComparison_IsOrdinal()
    {
        var auth = Standard();
        Assert.False(auth.CanApprove("Moderator-A", Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.True(auth.IsAuthenticatedModerator(MapNntpAuthenticationProvider.ModeratorA));
        Assert.False(auth.IsAuthenticatedModerator("Moderator-A"));
        Assert.False(auth.IsAuthenticatedModerator(MapNntpAuthenticationProvider.NormalUser));
    }

    [Fact]
    public void TryAuthorizeApproval_AllModeratedTargetsMustBeCovered()
    {
        var auth = Standard();
        Assert.True(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com"],
            ["group.a"],
            out _));
        Assert.False(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com"],
            ["group.a", "group.b"],
            out var detail));
        Assert.Equal("unauthorized moderator principal", detail);
    }

    [Fact]
    public void TryAuthorizeApproval_RejectsExtraUnrelatedIdentity()
    {
        var auth = Standard();
        Assert.False(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com", "other@example.com"],
            ["group.a"],
            out var detail));
        Assert.Equal("conflicting Approved", detail);
    }

    [Fact]
    public void TryAuthorizeApproval_SameModeratorOnTwoPatterns_Succeeds()
    {
        var auth = ModeratorTestSnapshot.Create(
            ("group.a", "shared@example.com", "moderator-a"),
            ("group.b", "shared@example.com", "moderator-a"));
        Assert.True(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["shared@example.com"],
            ["group.a", "group.b"],
            out _));
    }

    [Fact]
    public void EmptyCatalogue_ResolvesNothing()
    {
        var auth = ModeratorSnapshot.Empty;
        Assert.False(auth.TryResolve(Bytes("group.a"), out _));
        Assert.False(auth.TryAuthorizeApproval("moderator-a", ["a@example.com"], ["group.a"], out var detail));
        Assert.Equal("unresolved moderator", detail);
    }

    [Theory]
    [InlineData("fido7.some.group", "fido7-some-group@fido7.org")]
    [InlineData("perl.foo.bar", "news-moderator-perl-foo-bar@perl.org")]
    [InlineData("comp.test", "comp-test@moderators.isc.org")]
    public void InnTemplate_ExpandsPercentSWithDotsToDashes(string group, string expected)
    {
        Assert.Equal(expected, ModeratorAddressTemplate.Expand(AddressFor(group), Bytes(group)));
    }

    [Fact]
    public void InnRoutingCatalogue_FirstMatchAndCatchAll()
    {
        var auth = InnRouting();
        Assert.True(auth.TryResolve(Bytes("fido7.some.group"), out var fido7));
        Assert.Equal("fido7-some-group@fido7.org", fido7.Address);
        Assert.Equal(string.Empty, fido7.Username);
        Assert.True(auth.TryResolve(Bytes("perl.foo.bar"), out var perl));
        Assert.Equal("news-moderator-perl-foo-bar@perl.org", perl.Address);
        Assert.True(auth.TryResolve(Bytes("comp.test"), out var catchAll));
        Assert.Equal("comp-test@moderators.isc.org", catchAll.Address);
        Assert.Equal("*", catchAll.Pattern);
    }

    [Fact]
    public void InnRoutingCatalogue_DoesNotAuthorizeInjection()
    {
        var auth = InnRouting();
        Assert.False(auth.IsAuthenticatedModerator("moderator-a"));
        Assert.False(auth.CanApprove("moderator-a", Bytes("fido7-some-group@fido7.org"), Bytes("fido7.some.group")));
        Assert.False(auth.TryAuthorizeApproval(
            "moderator-a",
            ["fido7-some-group@fido7.org"],
            ["fido7.some.group"],
            out var detail));
        Assert.Equal("unauthorized moderator principal", detail);
    }

    [Fact]
    public void RoutingOnlyEntry_CompileDoesNotDropMissingUsername()
    {
        var auth = ModeratorTestSnapshot.Create(("fido7.*", "%s@fido7.org", string.Empty));
        Assert.True(auth.TryResolve(Bytes("fido7.announce"), out var identity));
        Assert.Equal("fido7-announce@fido7.org", identity.Address);
        Assert.Equal(string.Empty, identity.Username);
    }

    [Fact]
    public void StaticMailbox_IsUsedLiterally()
    {
        var auth = ModeratorTestSnapshot.Create(("comp.foo.*", "moderator@example.org", "MODERATOR01"));
        Assert.True(auth.TryResolve(Bytes("comp.foo.test"), out var identity));
        Assert.Equal("moderator@example.org", identity.Address);
        Assert.True(auth.CanApprove("MODERATOR01", Bytes("moderator@example.org"), Bytes("comp.foo.test")));
    }

    [Fact]
    public void Create_PreservesModeratorIdOrder_NotAlphabetical()
    {
        var auth = ModeratorSnapshot.Create(
        [
            new NntpModeratorRow(2, "zzz.*", "later@example.com", "later"),
            new NntpModeratorRow(1, "aaa.*", "first@example.com", "first"),
        ]);
        Assert.True(auth.TryResolve(Bytes("zzz.group"), out var identity));
        Assert.Equal("later@example.com", identity.Address);
    }

    [Fact]
    public void DatabaseFailureDoesNotAuthorize_EmptySnapshot()
    {
        Assert.False(ModeratorSnapshot.Empty.TryAuthorizeApproval(
            "moderator-a",
            ["a@example.com"],
            ["group.a"],
            out _));
    }

    private static ModeratorSnapshot InnRouting() =>
        ModeratorTestSnapshot.Create(
            ("fido7.*", "%s@fido7.org", string.Empty),
            ("fj.*", "%s@moderators.fj-news.org", string.Empty),
            ("medlux.*", "%s@news.medlux.ru", string.Empty),
            ("nl.*", "%s@nl.news-admin.org", string.Empty),
            ("perl.*", "news-moderator-%s@perl.org", string.Empty),
            ("relcom.*", "%s@moderators.relcom.ru", string.Empty),
            ("si.*", "%s@arnes.si", string.Empty),
            ("*", "%s@moderators.isc.org", string.Empty));

    private static string AddressFor(string group) =>
        group.StartsWith("fido7.", StringComparison.Ordinal) ? "%s@fido7.org"
        : group.StartsWith("perl.", StringComparison.Ordinal) ? "news-moderator-%s@perl.org"
        : "%s@moderators.isc.org";

    private static ModeratorSnapshot Standard() =>
        ModeratorTestSnapshot.Create(
            ("group.a", "moderator-a@example.com", MapNntpAuthenticationProvider.ModeratorA),
            ("group.b", "moderator-b@example.com", MapNntpAuthenticationProvider.ModeratorB));

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
}
