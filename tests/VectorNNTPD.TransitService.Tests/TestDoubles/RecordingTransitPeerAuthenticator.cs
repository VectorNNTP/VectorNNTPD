using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Counts Transit AUTHINFO invocations. Never logs credentials.</summary>
internal sealed class RecordingTransitPeerAuthenticator : ITransitPeerAuthenticator
{
    public int AuthenticateCount { get; private set; }

    public NntpAuthenticationResult Authenticate(
        TransitPeerPolicy? policy,
        string username,
        string password,
        NntpAuthorization currentAuthorization)
    {
        AuthenticateCount++;
        return TransitPeerAuthenticator.Instance.Authenticate(policy, username, password, currentAuthorization);
    }
}
