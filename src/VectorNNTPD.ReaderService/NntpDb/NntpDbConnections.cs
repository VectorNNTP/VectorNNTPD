using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>Opens an NNTPD query connection from the shared database service.</summary>
    internal static class NntpDbConnections
    {
        /// <summary>
        /// Opens a session and returns the NNTPD query surface.
        /// A session that already implements <see cref="INntpDbConnection"/> is returned unchanged.
        /// </summary>
        /// <param name="service">Started database service.</param>
        /// <param name="cancellationToken">Token used to cancel the open.</param>
        /// <returns>An open query connection. The caller disposes it.</returns>
        public static async ValueTask<INntpDbConnection> OpenAsync(
            NntpDbService service,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(service);
            var session = await service.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (session is INntpDbConnection connection)
            {
                return connection;
            }

            return new MySqlNntpDbConnection(session);
        }
    }
}
