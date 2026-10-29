namespace VectorNNTPD.TransitService.Session.Authentication
{
    /// <summary>
    /// Default authentication provider that rejects all credentials.
    /// </summary>
    /// <remarks>
    /// Register a real <see cref="INntpAuthenticationProvider"/> in DI when an account backend exists.
    /// Cleartext AUTHINFO policy is controlled by <c>Nntpd:AllowCleartextAuth</c>, not this type.
    /// </remarks>
    public sealed class DenyAllNntpAuthenticationProvider : INntpAuthenticationProvider
    {
        /// <summary>Shared singleton instance.</summary>
        public static DenyAllNntpAuthenticationProvider Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(username);
            ArgumentNullException.ThrowIfNull(password);
            _ = cancellationToken;
            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }
}
