using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Per-query UDP DNS exchange with remote-endpoint and transaction-id correlation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each call creates and disposes its own <see cref="UdpClient"/>. Sockets are not pooled:
    /// a timed-out or failed receive can leave datagrams queued on the socket, and recycling that
    /// socket would allow a later query to observe an earlier response. Dispose-per-query gives a
    /// clear isolation guarantee without an unprovable drain/reset of <see cref="UdpClient"/>.
    /// </para>
    /// <para>
    /// After sending to a specific nameserver endpoint, datagrams are accepted only when
    /// <see cref="UdpReceiveResult.RemoteEndPoint"/> matches that address and port, the buffer is
    /// long enough to contain a DNS header, and the response transaction id equals the query id.
    /// Unrelated, short, or stale-id datagrams are discarded until the single receive deadline elapses.
    /// Full header, question, and owner-name validation remain the caller's responsibility
    /// (see <see cref="DnsTxtParser"/> / <see cref="DnsResponseHeader"/>).
    /// </para>
    /// </remarks>
    internal static class DnsUdpExchange
    {
        /// <summary>
        /// Outcome of a single UDP DNS exchange attempt.
        /// </summary>
        internal readonly struct Result
        {
            /// <summary>Initializes a successful exchange.</summary>
            /// <param name="buffer">Response datagram accepted from the expected nameserver with a matching transaction id.</param>
            internal Result(byte[] buffer)
            {
                Buffer = buffer;
                TimedOut = false;
                Failed = false;
            }

            /// <summary>Initializes a timeout or socket-failure outcome with no response buffer.</summary>
            /// <param name="timedOut"><see langword="true"/> when the receive window elapsed.</param>
            /// <param name="failed"><see langword="true"/> when a socket or disposal error prevented a response.</param>
            private Result(bool timedOut, bool failed)
            {
                Buffer = null;
                TimedOut = timedOut;
                Failed = failed;
            }

            /// <summary>Response bytes when the exchange succeeded; otherwise <see langword="null"/>.</summary>
            internal byte[]? Buffer { get; }

            /// <summary><see langword="true"/> when the receive window elapsed without a matching datagram.</summary>
            internal bool TimedOut { get; }

            /// <summary><see langword="true"/> when a socket error prevented a usable response.</summary>
            internal bool Failed { get; }

            /// <summary>Creates a timeout outcome.</summary>
            internal static Result Timeout() => new(timedOut: true, failed: false);

            /// <summary>Creates a hard-failure outcome.</summary>
            internal static Result Failure() => new(timedOut: false, failed: true);
        }

        /// <summary>
        /// Sends <paramref name="query"/> to <paramref name="destination"/>:53 and returns the first
        /// correlated response from that endpoint.
        /// </summary>
        /// <param name="destination">Nameserver address. The destination port is always 53.</param>
        /// <param name="query">DNS query bytes. Must include a complete DNS header; the transaction id is taken from it.</param>
        /// <param name="timeoutMilliseconds">Single receive deadline. Non-matching datagrams are discarded until it elapses.</param>
        /// <param name="cancellationToken">Cancels send and receive. A timeout that is not caller cancellation returns <see cref="Result.Timeout"/>.</param>
        /// <returns>A success, timeout, or socket-failure outcome. The socket is always disposed.</returns>
        internal static Task<Result> QueryAsync(
            IPAddress destination,
            byte[] query,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            return QueryAsync(new IPEndPoint(destination, 53), query, timeoutMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Sends <paramref name="query"/> to <paramref name="destination"/> and returns the first
        /// correlated response from that endpoint (address and port).
        /// </summary>
        /// <param name="destination">Nameserver endpoint (tests may use a non-53 loopback port).</param>
        /// <param name="query">DNS query bytes. Must include a complete DNS header; the transaction id is taken from it.</param>
        /// <param name="timeoutMilliseconds">Single receive deadline. Non-matching datagrams are discarded until it elapses.</param>
        /// <param name="cancellationToken">Cancels send and receive. A timeout that is not caller cancellation returns <see cref="Result.Timeout"/>.</param>
        /// <returns>A success, timeout, or socket-failure outcome. The socket is always disposed.</returns>
        internal static async Task<Result> QueryAsync(
            IPEndPoint destination,
            byte[] query,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(query);
            if (query.Length < DnsWireFormat.HeaderSize)
            {
                throw new ArgumentException("DNS query must include a complete header.", nameof(query));
            }

            ushort expectedId = BinaryPrimitives.ReadUInt16BigEndian(query);

            // Fresh socket per query: never recycle after timeout/failure (stale datagram risk).
            using var udp = new UdpClient(destination.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            try
            {
                _ = await udp.SendAsync(query, destination, timeoutCts.Token).ConfigureAwait(false);

                while (!timeoutCts.IsCancellationRequested)
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (!RemoteEndpointMatches(result.RemoteEndPoint, destination))
                    {
                        continue;
                    }

                    if (!TryReadCorrelatedTransactionId(result.Buffer, expectedId))
                    {
                        // Short, malformed, or stale transaction id — keep the original deadline.
                        continue;
                    }

                    return new Result(result.Buffer);
                }

                return Result.Timeout();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result.Timeout();
            }
            catch (SocketException)
            {
                return Result.Failure();
            }
            catch (ObjectDisposedException)
            {
                return Result.Failure();
            }
        }

        /// <summary>
        /// Returns whether <paramref name="buffer"/> is long enough for a DNS header and carries
        /// transaction id <paramref name="expectedId"/>.
        /// </summary>
        /// <param name="buffer">Received datagram.</param>
        /// <param name="expectedId">Transaction id from the outbound query.</param>
        /// <returns>
        /// <see langword="true"/> when the datagram is a correlation candidate for the caller to parse;
        /// <see langword="false"/> when it must be ignored without ending the query.
        /// </returns>
        internal static bool TryReadCorrelatedTransactionId(byte[] buffer, ushort expectedId)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (buffer.Length < DnsWireFormat.HeaderSize)
            {
                return false;
            }

            ushort responseId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            return responseId == expectedId;
        }

        /// <summary>Returns whether <paramref name="remote"/> is an <see cref="IPEndPoint"/> with the expected address and port.</summary>
        /// <param name="remote">Datagram remote endpoint. Null and non-IP endpoints do not match.</param>
        /// <param name="expected">Nameserver endpoint.</param>
        /// <returns><see langword="true"/> when both address and port match.</returns>
        private static bool RemoteEndpointMatches(EndPoint? remote, IPEndPoint expected)
        {
            if (remote is not IPEndPoint ip)
            {
                return false;
            }

            return ip.Port == expected.Port && ip.Address.Equals(expected.Address);
        }
    }
}
