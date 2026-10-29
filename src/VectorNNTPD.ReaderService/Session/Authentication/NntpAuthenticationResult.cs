using VectorNNTPD.ReaderService.Authentication;

namespace VectorNNTPD.ReaderService.Session.Authentication
{
    /// <summary>Classifies a non-success authentication outcome for wire mapping.</summary>
    public enum NntpAuthenticationFailureKind
    {
        /// <summary>Success, or unused.</summary>
        None,

        /// <summary>Invalid credentials, disabled account, or mechanism denied. Wire 481.</summary>
        InvalidCredentials,

        /// <summary>Backend/store failure. Wire 503. Must not be presented as a bad password.</summary>
        TransientFailure,

        /// <summary>Session admission rejected. Wire 481 Too many sessions.</summary>
        TooManySessions,

        /// <summary>Source-IP admission rejected. Wire 481 Too many source addresses.</summary>
        TooManySourceAddresses,
    }

    /// <summary>Result of a credential verification attempt.</summary>
    public sealed class NntpAuthenticationResult
    {
        /// <summary>Failed authentication (no identity, no authorization).</summary>
        public static NntpAuthenticationResult Failed { get; } = new(
            succeeded: false,
            username: null,
            authorization: null,
            NntpAuthenticationFailureKind.InvalidCredentials,
            policy: null);

        /// <summary>Backend failure. Distinct from invalid credentials.</summary>
        public static NntpAuthenticationResult TransientFailure { get; } = new(
            succeeded: false,
            username: null,
            authorization: null,
            NntpAuthenticationFailureKind.TransientFailure,
            policy: null);

        /// <summary>Admission rejected: too many concurrent sessions.</summary>
        public static NntpAuthenticationResult TooManySessions { get; } = new(
            succeeded: false,
            username: null,
            authorization: null,
            NntpAuthenticationFailureKind.TooManySessions,
            policy: null);

        /// <summary>Admission rejected: too many distinct source addresses.</summary>
        public static NntpAuthenticationResult TooManySourceAddresses { get; } = new(
            succeeded: false,
            username: null,
            authorization: null,
            NntpAuthenticationFailureKind.TooManySourceAddresses,
            policy: null);

        private NntpAuthenticationResult(
            bool succeeded,
            string? username,
            NntpAuthorization? authorization,
            NntpAuthenticationFailureKind failure,
            NntpAccountPolicy? policy)
        {
            Succeeded = succeeded;
            Username = username;
            Authorization = authorization;
            Failure = failure;
            Policy = policy;
        }

        /// <summary>Gets a value indicating whether credentials were accepted.</summary>
        public bool Succeeded { get; }

        /// <summary>Gets the authenticated identity username when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
        public string? Username { get; }

        /// <summary>
        /// Gets authorization granted by the provider on success.
        /// Authentication success does not imply any particular privilege flags.
        /// </summary>
        public NntpAuthorization? Authorization { get; }

        /// <summary>Gets the failure classification when <see cref="Succeeded"/> is <see langword="false"/>.</summary>
        public NntpAuthenticationFailureKind Failure { get; }

        /// <summary>Gets account policy after a successful MySQL authentication.</summary>
        public NntpAccountPolicy? Policy { get; }

        /// <summary>Creates a successful result with explicit authorization (flags are provider-defined).</summary>
        public static NntpAuthenticationResult Success(
            string username,
            NntpAuthorization authorization,
            NntpAccountPolicy? policy = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentNullException.ThrowIfNull(authorization);
            return new(succeeded: true, username: username, authorization: authorization, NntpAuthenticationFailureKind.None, policy);
        }
    }
}
