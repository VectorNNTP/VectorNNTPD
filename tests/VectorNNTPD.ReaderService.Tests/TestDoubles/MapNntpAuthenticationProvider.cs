using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;

namespace VectorNNTPD.ReaderService.Tests.TestDoubles;

/// <summary>
/// AUTHINFO USER/PASS map used by moderation and ordinary POST tests.
/// Passwords are never logged or asserted.
/// </summary>
public sealed class MapNntpAuthenticationProvider : INntpAuthenticationProvider
{
    /// <summary>Ordinary authenticated poster who is not a moderator.</summary>
    public const string NormalUser = "normal-user";

    /// <summary>Moderator principal for group A mappings.</summary>
    public const string ModeratorA = "moderator-a";

    /// <summary>Moderator principal for group B mappings.</summary>
    public const string ModeratorB = "moderator-b";

    /// <summary>Password for <see cref="NormalUser"/>.</summary>
    public const string NormalUserPassword = "normal-user-secret";

    /// <summary>Password for <see cref="ModeratorA"/>.</summary>
    public const string ModeratorAPassword = "moderator-a-secret";

    /// <summary>Password for <see cref="ModeratorB"/>.</summary>
    public const string ModeratorBPassword = "moderator-b-secret";

    /// <summary>Authenticated reader + poster privileges (no transit, no control-cancel).</summary>
    public static NntpAuthorization PosterPrivileges { get; } = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    private readonly Dictionary<string, (string Password, NntpAuthorization Authorization)> _accounts =
        new(StringComparer.Ordinal);

    /// <summary>Creates the standard normal-user / moderator-a / moderator-b map.</summary>
    public static MapNntpAuthenticationProvider CreateStandard()
    {
        var provider = new MapNntpAuthenticationProvider();
        provider.Add(NormalUser, NormalUserPassword, PosterPrivileges);
        provider.Add(ModeratorA, ModeratorAPassword, PosterPrivileges);
        provider.Add(ModeratorB, ModeratorBPassword, PosterPrivileges);
        return provider;
    }

    /// <summary>Registers one username/password and the privileges granted on success.</summary>
    public void Add(string username, string password, NntpAuthorization authorization)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(authorization);
        _accounts[username] = (password, authorization);
    }

    /// <inheritdoc />
    public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        _ = cancellationToken;
        if (_accounts.TryGetValue(username, out var account) && account.Password == password)
        {
            return ValueTask.FromResult(NntpAuthenticationResult.Success(username, account.Authorization));
        }

        return ValueTask.FromResult(NntpAuthenticationResult.Failed);
    }
}
