using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Session.Authentication
{
    /// <summary>Validates AUTHINFO USER/PASS against an identified Transit peer policy.</summary>
    public interface ITransitPeerAuthenticator
    {
        /// <summary>
        /// Authenticates <paramref name="username"/> / <paramref name="password"/> against
        /// <paramref name="policy"/>. On success, <paramref name="currentAuthorization"/> gains
        /// <see cref="NntpAuthorization.AuthorizedTransit"/> and keeps its peer identity.
        /// Reader and posting privileges are not granted. A blank username or password on the
        /// peer disables this gate (<see cref="TransitPeerPolicy.HasPeerCredentials"/>); it is not a mismatch.
        /// </summary>
        NntpAuthenticationResult Authenticate(
            TransitPeerPolicy? policy,
            string username,
            string password,
            NntpAuthorization currentAuthorization);
    }
}
