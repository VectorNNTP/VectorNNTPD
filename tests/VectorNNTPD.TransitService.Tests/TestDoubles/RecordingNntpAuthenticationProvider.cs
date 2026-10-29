using System.Net;
using VectorNNTPD.TransitService.Session.Authentication;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Counts reader-provider AUTHINFO invocations. Never logs passwords.</summary>
internal sealed class RecordingNntpAuthenticationProvider : INntpAuthenticationProvider
{
    private readonly INntpAuthenticationProvider _inner;

    public RecordingNntpAuthenticationProvider(INntpAuthenticationProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public int AuthenticateCount { get; private set; }

    public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        AuthenticateCount++;
        return _inner.AuthenticateAsync(username, password, cancellationToken);
    }

    public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        IPAddress clientIp,
        CancellationToken cancellationToken = default)
    {
        AuthenticateCount++;
        return _inner.AuthenticateAsync(username, password, clientIp, cancellationToken);
    }
}
