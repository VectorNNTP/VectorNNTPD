using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.Session.Authentication;

public sealed class NewsmasterNntpAuthenticationProviderTests
{
    [Fact]
    public void Create_WithoutCredentials_IsDenyAll()
    {
        var provider = NewsmasterNntpAuthenticationProvider.Create(TestHostFactory.CreateValidOptions());
        Assert.Same(DenyAllNntpAuthenticationProvider.Instance, provider);
    }

    [Fact]
    public async Task Authenticate_MatchingCredentials_GrantsControlCancel()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterUser = "newsmaster";
        options.NewsmasterPassword = "unit-test-newsmaster-password";
        var provider = NewsmasterNntpAuthenticationProvider.Create(options);

        var result = await provider.AuthenticateAsync("newsmaster", "unit-test-newsmaster-password");
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Authorization);
        Assert.True(result.Authorization!.ControlCancelPermitted);
        Assert.True(result.Authorization.PostingPermitted);
        Assert.False(result.Authorization.AuthorizedTransit);
        Assert.True(result.Authorization.With(isAuthenticated: true).ControlCancelPermitted);
    }

    [Fact]
    public async Task Authenticate_WrongPassword_FailsWithoutPrivileges()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterUser = "newsmaster";
        options.NewsmasterPassword = "unit-test-newsmaster-password";
        var provider = NewsmasterNntpAuthenticationProvider.Create(options);

        var result = await provider.AuthenticateAsync("newsmaster", "wrong");
        Assert.False(result.Succeeded);
        Assert.Null(result.Authorization);
    }

    [Fact]
    public async Task Authenticate_WrongUser_Fails()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.NewsmasterUser = "newsmaster";
        options.NewsmasterPassword = "unit-test-newsmaster-password";
        var provider = NewsmasterNntpAuthenticationProvider.Create(options);

        var result = await provider.AuthenticateAsync("other", "unit-test-newsmaster-password");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Unauthenticated_DoesNotPermitControlCancel()
    {
        Assert.False(NntpAuthorization.Unauthenticated.ControlCancelPermitted);
        Assert.False(NntpAuthorization.TrustedTransitPeer.ControlCancelPermitted);
    }
}
