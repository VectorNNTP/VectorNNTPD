namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>Immutable authentication identity for an NNTP session (distinct from authorization).</summary>
    public sealed class NntpAuthenticationState
    {
        /// <summary>Unauthenticated session (no identity).</summary>
        public static NntpAuthenticationState Unauthenticated { get; } = new(isAuthenticated: false, username: null);

        private NntpAuthenticationState(bool isAuthenticated, string? username)
        {
            IsAuthenticated = isAuthenticated;
            Username = username;
        }

        /// <summary>Gets a value indicating whether the peer completed AUTHINFO successfully.</summary>
        public bool IsAuthenticated { get; }

        /// <summary>Gets the authenticated username when <see cref="IsAuthenticated"/> is <see langword="true"/>.</summary>
        public string? Username { get; }

        /// <summary>Creates an authenticated identity snapshot.</summary>
        public static NntpAuthenticationState ForUser(string username)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            return new(isAuthenticated: true, username: username);
        }
    }
}
