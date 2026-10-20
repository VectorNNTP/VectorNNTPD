using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTPD.Common.Dns
{
    /// <summary>
    /// Sends TXT queries directly to one authoritative nameserver (UDP, then TCP when needed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queries use RD=0. UDP is attempted first; TCP (RFC 7766 length-prefixed framing) is used when the
    /// UDP response is truncated, missing, or yields no TXT answers.
    /// </para>
    /// <para>
    /// UDP responses are accepted only when the datagram remote endpoint matches the queried nameserver
    /// address and port 53 (see <see cref="DnsUdpExchange"/>). TCP connects explicitly to that address.
    /// </para>
    /// <para>
    /// This type does not perform recursive resolution and does not discover nameservers. Pair with
    /// <see cref="ZoneApexNameserverDiscovery"/> for ACME DNS-01 visibility checks.
    /// </para>
    /// </remarks>
    internal static class AuthoritativeTxtClient
    {
        /// <summary>UDP and TCP receive window, in milliseconds, for one authoritative TXT query.</summary>
        private const int ReceiveTimeoutMs = 5_000;

        /// <summary>
        /// Queries TXT for <paramref name="recordName"/> at <paramref name="nameserver"/> using UDP, then TCP
        /// if truncated or empty answers.
        /// </summary>
        /// <param name="nameserver">Authoritative nameserver address. Queries use port 53 and RD=0.</param>
        /// <param name="recordName">TXT owner name.</param>
        /// <param name="cancellationToken">Cancels both transports. A transport timeout is not caller cancellation.</param>
        /// <param name="logger">Optional logger for UDP and TCP timeouts. Null suppresses those events.</param>
        /// <returns>Decoded TXT strings; empty when the nameserver returns no usable TXT.</returns>
        internal static async Task<List<string>> QueryTxtAsync(
            IPAddress nameserver,
            string recordName,
            CancellationToken cancellationToken,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(nameserver);
            ArgumentException.ThrowIfNullOrWhiteSpace(recordName);

            string ownerName = recordName.TrimEnd('.');
            byte[] queryPacket = DnsQueryBuilder.Build(ownerName, DnsRecordType.Txt, out ushort queryId);
            byte[]? udpResponse = await TryUdpQueryAsync(nameserver, ownerName, queryPacket, logger, cancellationToken)
                .ConfigureAwait(false);
            // Require AA=1: UDP/TCP queries are sent RD=0 directly to an authoritative nameserver address.
            // A recursive or otherwise non-authoritative reply must not count as DNS-01 propagation proof.
            List<string> results = udpResponse is null
                ? []
                : DnsTxtParser.ParseTxtStrings(udpResponse, queryId, ownerName, requireAuthoritativeAnswer: true);
            if (udpResponse is null || ShouldRetryOverTcp(udpResponse, results))
            {
                byte[]? tcpResponse = await TryTcpQueryAsync(nameserver, ownerName, queryPacket, logger, cancellationToken)
                    .ConfigureAwait(false);
                if (tcpResponse is not null)
                {
                    List<string> tcpParsed = DnsTxtParser.ParseTxtStrings(
                        tcpResponse,
                        queryId,
                        ownerName,
                        requireAuthoritativeAnswer: true);
                    if (tcpParsed.Count > 0)
                    {
                        return tcpParsed;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Returns whether TCP should be tried: the UDP header has TC set, or the UDP parse produced no TXT strings.
        /// </summary>
        /// <param name="udpResponse">UDP response bytes.</param>
        /// <param name="parsedTxt">TXT strings parsed from <paramref name="udpResponse"/>.</param>
        /// <returns><see langword="true"/> when TCP should be attempted.</returns>
        private static bool ShouldRetryOverTcp(byte[] udpResponse, List<string> parsedTxt)
            => DnsResponseHeader.IsTruncated(udpResponse) || parsedTxt.Count == 0;

        /// <summary>Sends one RD=0 UDP TXT query and returns the matching datagram, or null on timeout or socket failure.</summary>
        /// <param name="nameserver">Authoritative nameserver address.</param>
        /// <param name="recordName">TXT owner name, used only in the timeout log.</param>
        /// <param name="queryPacket">Query built by <see cref="DnsQueryBuilder"/>.</param>
        /// <param name="logger">Optional timeout logger.</param>
        /// <param name="cancellationToken">Cancels the exchange. Caller cancellation propagates from <see cref="DnsUdpExchange.QueryAsync(IPAddress, byte[], int, CancellationToken)"/>.</param>
        /// <returns>Response bytes, or null when the exchange timed out or failed.</returns>
        private static async Task<byte[]?> TryUdpQueryAsync(
            IPAddress nameserver,
            string recordName,
            byte[] queryPacket,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            DnsUdpExchange.Result exchange = await DnsUdpExchange
                .QueryAsync(nameserver, queryPacket, ReceiveTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (exchange.TimedOut && logger is not null && logger.IsEnabled(LogLevel.Debug))
            {
                string nameserverText = nameserver.ToString();
                DnsLogMessages.AuthoritativeUdpTimeout(logger, nameserverText, recordName);
            }

            return exchange.Buffer;
        }

        /// <summary>
        /// Sends one RD=0 TXT query over TCP using a 2-byte length prefix, then reads one length-prefixed response.
        /// </summary>
        /// <param name="nameserver">Authoritative nameserver address. The connection target port is 53.</param>
        /// <param name="recordName">TXT owner name, used only in the timeout log.</param>
        /// <param name="queryPacket">Query built by <see cref="DnsQueryBuilder"/>.</param>
        /// <param name="logger">Optional timeout logger. Socket, disposal, and IO failures are not logged.</param>
        /// <param name="cancellationToken">
        /// Cancels connect and IO. A timeout that is not caller cancellation is logged and returns null.
        /// Caller cancellation propagates.
        /// </param>
        /// <returns>Response message bytes without the length prefix, or null on timeout, invalid length, or transport failure.</returns>
        private static async Task<byte[]?> TryTcpQueryAsync(
            IPAddress nameserver,
            string recordName,
            byte[] queryPacket,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReceiveTimeoutMs);

            byte[]? rentedResponse = null;
            try
            {
                await tcp.ConnectAsync(nameserver, 53, timeoutCts.Token).ConfigureAwait(false);
                NetworkStream stream = tcp.GetStream();
                int qLen = queryPacket.Length;
                byte[] lengthPrefix = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)qLen);
                await stream.WriteAsync(lengthPrefix.AsMemory(0, 2), timeoutCts.Token).ConfigureAwait(false);
                await stream.WriteAsync(queryPacket, timeoutCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                byte[] lenBuf = new byte[2];
                await stream.ReadExactlyAsync(lenBuf.AsMemory(0, 2), timeoutCts.Token).ConfigureAwait(false);
                int msgLen = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                if (msgLen is <= 0 or > 65535)
                {
                    return null;
                }

                rentedResponse = ArrayPool<byte>.Shared.Rent(msgLen);
                await stream.ReadExactlyAsync(rentedResponse.AsMemory(0, msgLen), timeoutCts.Token).ConfigureAwait(false);
                byte[] response = new byte[msgLen];
                rentedResponse.AsSpan(0, msgLen).CopyTo(response);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                {
                    string nameserverText = nameserver.ToString();
                    DnsLogMessages.AuthoritativeTcpTimeout(logger, nameserverText, recordName);
                }

                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                if (rentedResponse is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedResponse);
                }
            }
        }
    }
}
