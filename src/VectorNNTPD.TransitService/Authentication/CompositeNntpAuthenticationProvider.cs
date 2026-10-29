using System.Net;
using VectorNNTPD.TransitService.Session.Authentication;

namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>
    /// AUTHINFO USER/PASS provider: newsmaster first (when configured), then MySQL reader accounts.
    /// </summary>
    /// <remarks>
    /// A newsmaster username match never falls through to MySQL. Transit peer AUTHINFO is
    /// selected only when the session AUTHINFO authority is Transit (MODE STREAM) and is
    /// handled by <see cref="VectorNNTPD.TransitService.Session.Authentication.ITransitPeerAuthenticator"/>,
    /// not this type. This provider is Reader-authority only.
    /// </remarks>
    public sealed class CompositeNntpAuthenticationProvider : INntpAuthenticationProvider
    {
        private readonly INntpAuthenticationProvider _newsmaster;
        private readonly string? _newsmasterUsername;
        private readonly MySqlNntpCredentialValidator _mysql;

        /// <summary>Initializes a composite provider.</summary>
        public CompositeNntpAuthenticationProvider(
            INntpAuthenticationProvider newsmaster,
            string? newsmasterUsername,
            MySqlNntpCredentialValidator mysql)
        {
            ArgumentNullException.ThrowIfNull(newsmaster);
            ArgumentNullException.ThrowIfNull(mysql);
            _newsmaster = newsmaster;
            _newsmasterUsername = string.IsNullOrWhiteSpace(newsmasterUsername) ? null : newsmasterUsername.Trim();
            _mysql = mysql;
        }

        /// <inheritdoc />
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            AuthenticateAsync(username, password, IPAddress.None, cancellationToken);

        /// <inheritdoc />
        public async ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            IPAddress clientIp,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(clientIp);
            if (string.IsNullOrWhiteSpace(username))
            {
                return NntpAuthenticationResult.Failed;
            }

            if (_newsmasterUsername is not null
                && string.Equals(username, _newsmasterUsername, StringComparison.Ordinal))
            {
                return await _newsmaster.AuthenticateAsync(username, password, cancellationToken).ConfigureAwait(false);
            }

            return await _mysql
                .ValidatePasswordAsync(
                    NntpAuthMechanisms.AuthInfoUserPass,
                    username,
                    password,
                    clientIp,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
