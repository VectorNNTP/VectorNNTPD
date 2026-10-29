using System.Security.Cryptography;
using System.Text;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Session.Authentication
{
    /// <summary>
    /// AUTHINFO USER/PASS provider that recognizes only the configured newsmaster account.
    /// </summary>
    /// <remarks>
    /// Success grants posting plus <see cref="NntpAuthorization.ControlCancelPermitted"/> so the
    /// newsmaster can submit a well-formed <c>Control: cancel</c> article. It does not grant
    /// transit or streaming privileges and does not accept any other Control verb. Passwords are
    /// compared in constant time and are never logged.
    /// When newsmaster credentials are not configured, <see cref="Create"/> returns
    /// <see cref="DenyAllNntpAuthenticationProvider"/>.
    /// </remarks>
    public sealed class NewsmasterNntpAuthenticationProvider : INntpAuthenticationProvider
    {
        private readonly string _username;
        private readonly byte[] _passwordUtf8;

        /// <summary>
        /// Privileges granted to the configured newsmaster: authenticated reader + poster with
        /// cancel-control permission only.
        /// </summary>
        public static NntpAuthorization Privileges { get; } = new(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: true,
            streamingPermitted: false,
            controlCancelPermitted: true);

        private NewsmasterNntpAuthenticationProvider(string username, string password)
        {
            _username = username;
            _passwordUtf8 = Encoding.UTF8.GetBytes(password);
        }

        /// <summary>
        /// Returns a newsmaster provider when both credentials are configured; otherwise deny-all.
        /// </summary>
        public static INntpAuthenticationProvider Create(NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.NewsmasterUser)
                || string.IsNullOrEmpty(options.NewsmasterPassword))
            {
                return DenyAllNntpAuthenticationProvider.Instance;
            }

            return new NewsmasterNntpAuthenticationProvider(
                options.NewsmasterUser.Trim(),
                options.NewsmasterPassword);
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

            var provided = Encoding.UTF8.GetBytes(password);
            var passwordMatch = provided.Length == _passwordUtf8.Length
                && CryptographicOperations.FixedTimeEquals(provided, _passwordUtf8);
            var userMatch = string.Equals(username, _username, StringComparison.Ordinal);
            if (userMatch && passwordMatch)
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(username, Privileges));
            }

            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }
}
