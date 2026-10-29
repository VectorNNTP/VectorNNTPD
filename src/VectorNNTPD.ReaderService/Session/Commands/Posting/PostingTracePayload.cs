using System.Net;

namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>
    /// Transport and authentication identity recovered from a server-owned POST <c>X-Trace</c> token.
    /// </summary>
    /// <remarks>
    /// Intended for trusted server-side diagnostics only. Do not log this value.
    /// <see cref="AuthenticatedUsername"/> is the AUTHINFO identity captured at POST admission;
    /// it is never taken from article headers. Null or empty means the session was unauthenticated.
    /// </remarks>
    public readonly struct PostingTracePayload
    {
        /// <summary>Initializes a payload without an authenticated username (unauthenticated POST).</summary>
        public PostingTracePayload(IPAddress address, int port, DateTimeOffset injectedAtUtc, Guid traceId)
            : this(address, port, injectedAtUtc, traceId, authenticatedUsername: null)
        {
        }

        /// <summary>Initializes a payload, optionally including the authenticated session username.</summary>
        public PostingTracePayload(
            IPAddress address,
            int port,
            DateTimeOffset injectedAtUtc,
            Guid traceId,
            string? authenticatedUsername)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            Address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            Port = port;
            InjectedAtUtc = injectedAtUtc.ToUniversalTime();
            TraceId = traceId;
            AuthenticatedUsername = NormalizeUsername(authenticatedUsername);
        }

        /// <summary>Gets the effective transport peer address.</summary>
        public IPAddress Address { get; }

        /// <summary>Gets the effective transport peer port.</summary>
        public int Port { get; }

        /// <summary>Gets the injection-boundary UTC timestamp.</summary>
        public DateTimeOffset InjectedAtUtc { get; }

        /// <summary>Gets the unique identifier for this POST attempt.</summary>
        public Guid TraceId { get; }

        /// <summary>
        /// Gets the AUTHINFO username captured at POST admission, or <see langword="null"/>
        /// when the session was unauthenticated or the token is a pre-username v1 payload.
        /// </summary>
        public string? AuthenticatedUsername { get; }

        internal static string? NormalizeUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            return username;
        }
    }
}
