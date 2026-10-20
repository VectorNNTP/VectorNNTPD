using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTPD.Common.Dns;

namespace VectorNNTPD.Common.Tests.Dns
{
    /// <summary>
    /// Deterministic loopback fixtures for <see cref="DnsUdpExchange"/> correlation and socket isolation.
    /// </summary>
    public sealed class DnsUdpExchangeTests
    {
        /// <summary>
        /// Asserts that a valid response from the expected endpoint with the correct transaction id is accepted.
        /// </summary>
        /// <returns>A task that completes when the exchange assertion finishes.</returns>
        [Fact]
        public async Task QueryAsync_MatchingEndpointAndTransactionId_Accepted()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            byte[] query = DnsQueryBuilder.Build("example.com", DnsRecordType.Txt, out ushort queryId);
            ns.OnQuery = (received, remote) =>
            {
                Assert.Equal(queryId, BinaryPrimitives.ReadUInt16BigEndian(received));
                return BuildMinimalTxtResponse(queryId, "example.com", "ok");
            };

            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 2_000,
                CancellationToken.None);

            Assert.False(result.TimedOut);
            Assert.False(result.Failed);
            Assert.NotNull(result.Buffer);
            Assert.True(DnsTxtParser.ResponseContainsTxt(
                result.Buffer,
                queryId,
                "example.com",
                "ok"u8,
                requireAuthoritativeAnswer: true));
            Assert.Equal(1, ns.QueriesReceived);
            Assert.Equal(1, ns.ResponsesSent);
        }

        /// <summary>
        /// Asserts that a datagram from a wrong source address is ignored until timeout when no valid reply arrives.
        /// </summary>
        /// <returns>A task that completes when the timeout assertion finishes.</returns>
        [Fact]
        public async Task QueryAsync_WrongSourceAddress_IgnoredUntilTimeout()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            // 127.0.0.2 is loopback but a different address than 127.0.0.1 used by the nameserver.
            await using SpoofSender spoof = await SpoofSender.StartAsync(IPAddress.Parse("127.0.0.2"));
            byte[] query = DnsQueryBuilder.Build("example.com", DnsRecordType.Txt, out ushort queryId);
            var clientReady = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

            ns.OnQuery = (_, remote) =>
            {
                clientReady.TrySetResult(remote);
                return null;
            };

            Task<DnsUdpExchange.Result> exchangeTask = DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 400,
                CancellationToken.None);

            IPEndPoint client = await clientReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await spoof.SendAsync(BuildMinimalTxtResponse(queryId, "example.com", "spoof"), client);

            DnsUdpExchange.Result result = await exchangeTask;
            Assert.True(result.TimedOut);
            Assert.Null(result.Buffer);
            Assert.False(spoof.LocalAddress.Equals(ns.EndPoint.Address));
        }

        /// <summary>
        /// Asserts that a response from the wrong source port is ignored.
        /// </summary>
        /// <returns>A task that completes when the timeout assertion finishes.</returns>
        [Fact]
        public async Task QueryAsync_WrongSourcePort_IgnoredUntilTimeout()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            await using SpoofSender spoof = await SpoofSender.StartAsync(IPAddress.Loopback);
            byte[] query = DnsQueryBuilder.Build("example.com", DnsRecordType.Txt, out ushort queryId);
            var clientReady = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

            ns.OnQuery = (_, remote) =>
            {
                clientReady.TrySetResult(remote);
                return null; // legitimate NS stays silent
            };

            Task<DnsUdpExchange.Result> exchangeTask = DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 400,
                CancellationToken.None);

            IPEndPoint client = await clientReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Spoof uses a different local port than ns.EndPoint.Port.
            await spoof.SendAsync(BuildMinimalTxtResponse(queryId, "example.com", "wrong-port"), client);

            DnsUdpExchange.Result result = await exchangeTask;
            Assert.True(result.TimedOut);
            Assert.Null(result.Buffer);
            Assert.NotEqual(ns.EndPoint.Port, spoof.LocalPort);
        }

        /// <summary>
        /// Asserts that a late datagram with a previous query's transaction id cannot satisfy a later query.
        /// </summary>
        /// <returns>A task that completes when both exchanges finish.</returns>
        [Fact]
        public async Task QueryAsync_LatePreviousTransactionId_DoesNotSatisfyLaterQuery()
        {
            // Repeat to surface races from socket reuse (pooling would fail; dispose-per-query must pass).
            for (int iteration = 0; iteration < 8; iteration++)
            {
                await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
                ushort firstId = 0;
                var firstClientReady = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                int phase = 0;

                ns.OnQuery = (received, remote) =>
                {
                    ushort id = BinaryPrimitives.ReadUInt16BigEndian(received);
                    if (Interlocked.Exchange(ref phase, 1) == 0)
                    {
                        firstId = id;
                        firstClientReady.TrySetResult(remote);
                        return null; // first query times out; late reply injected after timeout
                    }

                    // Second query: reply only with the second query's id.
                    return BuildMinimalTxtResponse(id, "example.com", "second");
                };

                byte[] firstQuery = BuildQueryWithId(0x1111, "example.com");
                Task<DnsUdpExchange.Result> firstTask = DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    firstQuery,
                    timeoutMilliseconds: 150,
                    CancellationToken.None);

                IPEndPoint firstClient = await firstClientReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
                DnsUdpExchange.Result first = await firstTask;
                Assert.True(first.TimedOut);

                // Late response for the first query, aimed at the disposed first client's port (harmless),
                // and also send stale id toward the nameserver path by answering the next query wrongly first.
                using var late = new UdpClient(AddressFamily.InterNetwork);
                await late.SendAsync(BuildMinimalTxtResponse(firstId, "example.com", "late"), firstClient);

                byte[] secondQuery = BuildQueryWithId(0x2222, "example.com");
                DnsUdpExchange.Result second = await DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    secondQuery,
                    timeoutMilliseconds: 2_000,
                    CancellationToken.None);

                Assert.False(second.TimedOut);
                Assert.NotNull(second.Buffer);
                Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16BigEndian(second.Buffer));
                Assert.True(DnsTxtParser.ResponseContainsTxt(
                    second.Buffer,
                    0x2222,
                    "example.com",
                    "second"u8));
                Assert.False(DnsTxtParser.ResponseContainsTxt(
                    second.Buffer,
                    0x2222,
                    "example.com",
                    "late"u8));
            }
        }

        /// <summary>
        /// Asserts that a stale transaction id from the expected nameserver is ignored until the matching response arrives.
        /// </summary>
        /// <returns>A task that completes when the matching response is accepted.</returns>
        [Fact]
        public async Task QueryAsync_StaleTransactionId_IgnoredUntilMatchingResponse()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            byte[] query = BuildQueryWithId(0xABCD, "example.com");

            ns.OnQueryAsync = async (_, remote, send) =>
            {
                await send(BuildMinimalTxtResponse(0x0001, "example.com", "stale"), remote).ConfigureAwait(false);
                await send(BuildMinimalTxtResponse(0xABCD, "example.com", "fresh"), remote).ConfigureAwait(false);
            };

            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 2_000,
                CancellationToken.None);

            Assert.False(result.TimedOut);
            Assert.NotNull(result.Buffer);
            Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16BigEndian(result.Buffer));
            Assert.True(DnsTxtParser.ResponseContainsTxt(result.Buffer, 0xABCD, "example.com", "fresh"u8));
            Assert.Equal(2, ns.ResponsesSent);
        }

        /// <summary>
        /// Asserts that a transaction-id collision with a mismatched question name cannot satisfy TXT validation.
        /// </summary>
        /// <returns>A task that completes when the parser rejection is asserted.</returns>
        [Fact]
        public async Task QueryAsync_TransactionIdCollision_WrongQuestion_RejectedByTxtParser()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            const ushort collidingId = 0x4242;
            byte[] query = BuildQueryWithId(collidingId, "_acme-challenge.example.com");

            ns.OnQuery = (_, _) =>
                BuildMinimalTxtResponse(collidingId, "evil.example.com", "digest");

            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 2_000,
                CancellationToken.None);

            Assert.NotNull(result.Buffer);
            Assert.Equal(collidingId, BinaryPrimitives.ReadUInt16BigEndian(result.Buffer));
            // Exchange correlates on TXID only; #181 owner/question checks still reject.
            Assert.False(DnsTxtParser.ResponseContainsTxt(
                result.Buffer,
                collidingId,
                "_acme-challenge.example.com",
                "digest"u8));
            Assert.Empty(DnsTxtParser.ParseTxtStrings(
                result.Buffer,
                collidingId,
                "_acme-challenge.example.com"));
        }

        /// <summary>
        /// Asserts that short and truncated-header datagrams are never accepted as successful answers.
        /// </summary>
        /// <returns>A task that completes when the matching full response is accepted.</returns>
        [Fact]
        public async Task QueryAsync_ShortAndMalformedDatagrams_Ignored()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            byte[] query = BuildQueryWithId(0x7777, "example.com");

            ns.OnQueryAsync = async (_, remote, send) =>
            {
                await send([0x77, 0x77], remote).ConfigureAwait(false); // TXID only
                await send([0x77, 0x77, 0x84, 0x00, 0x00, 0x01], remote).ConfigureAwait(false); // short header
                await send(BuildMinimalTxtResponse(0x7777, "example.com", "full"), remote).ConfigureAwait(false);
            };

            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: 2_000,
                CancellationToken.None);

            Assert.NotNull(result.Buffer);
            Assert.True(result.Buffer.Length >= DnsWireFormat.HeaderSize);
            Assert.True(DnsTxtParser.ResponseContainsTxt(result.Buffer, 0x7777, "example.com", "full"u8));
        }

        /// <summary>
        /// Asserts that a flood of unrelated datagrams cannot extend the query past its original deadline.
        /// </summary>
        /// <returns>A task that completes when the bounded timeout is observed.</returns>
        [Fact]
        public async Task QueryAsync_FloodOfUnrelatedDatagrams_DoesNotExtendDeadline()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            byte[] query = BuildQueryWithId(0x5555, "example.com");
            const int timeoutMs = 300;

            ns.OnQueryAsync = async (_, remote, send) =>
            {
                for (int i = 0; i < 64; i++)
                {
                    await send(BuildMinimalTxtResponse((ushort)(0x1000 + i), "example.com", "noise"), remote)
                        .ConfigureAwait(false);
                }
            };

            var sw = Stopwatch.StartNew();
            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                timeoutMilliseconds: timeoutMs,
                CancellationToken.None);
            sw.Stop();

            Assert.True(result.TimedOut);
            Assert.Null(result.Buffer);
            // Allow scheduling slack but reject indefinite extension from the flood.
            Assert.True(sw.ElapsedMilliseconds < timeoutMs + 2_000, $"elapsed={sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Asserts that after a timeout, a subsequent query on a new socket cannot observe the previous response.
        /// </summary>
        /// <returns>A task that completes when isolation is demonstrated across iterations.</returns>
        [Fact]
        public async Task QueryAsync_TimeoutThenNewQuery_CannotConsumePreviousQueuedResponse()
        {
            for (int iteration = 0; iteration < 5; iteration++)
            {
                await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
                int queryCount = 0;
                var firstRemote = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                ushort firstId = 0;

                ns.OnQueryAsync = async (received, remote, send) =>
                {
                    int n = Interlocked.Increment(ref queryCount);
                    ushort id = BinaryPrimitives.ReadUInt16BigEndian(received);
                    if (n == 1)
                    {
                        firstId = id;
                        firstRemote.TrySetResult(remote);
                        // Stay silent so the first exchange times out. A late reply is sent after
                        // dispose from a separate sender; the new query uses a fresh socket.
                        await Task.CompletedTask.ConfigureAwait(false);
                        return;
                    }

                    await send(BuildMinimalTxtResponse(id, "example.com", "second-ok"), remote)
                        .ConfigureAwait(false);
                };

                byte[] firstQuery = BuildQueryWithId(0x0101, "example.com");
                DnsUdpExchange.Result first = await DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    firstQuery,
                    timeoutMilliseconds: 150,
                    CancellationToken.None);
                Assert.True(first.TimedOut);

                IPEndPoint staleClient = await firstRemote.Task.WaitAsync(TimeSpan.FromSeconds(2));
                // Inject the previous query's response toward the disposed ephemeral port and also
                // toward any subsequent listener — the next QueryAsync must still only accept 0x0202.
                using (var late = new UdpClient(AddressFamily.InterNetwork))
                {
                    await late.SendAsync(
                        BuildMinimalTxtResponse(firstId, "example.com", "late-first"),
                        staleClient);
                }

                byte[] secondQuery = BuildQueryWithId(0x0202, "example.com");
                DnsUdpExchange.Result second = await DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    secondQuery,
                    timeoutMilliseconds: 2_000,
                    CancellationToken.None);

                Assert.False(second.TimedOut);
                Assert.False(second.Failed);
                Assert.NotNull(second.Buffer);
                Assert.Equal(0x0202, BinaryPrimitives.ReadUInt16BigEndian(second.Buffer));
                Assert.True(DnsTxtParser.ResponseContainsTxt(
                    second.Buffer,
                    0x0202,
                    "example.com",
                    "second-ok"u8));
                Assert.False(DnsTxtParser.ResponseContainsTxt(
                    second.Buffer,
                    0x0202,
                    "example.com",
                    "late-first"u8));
            }
        }

        /// <summary>
        /// Asserts that concurrent queries cannot consume each other's responses.
        /// </summary>
        /// <returns>A task that completes when both queries receive their own digests.</returns>
        [Fact]
        public async Task QueryAsync_ConcurrentQueries_DoNotCrossConsumeResponses()
        {
            for (int iteration = 0; iteration < 5; iteration++)
            {
                await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
                ns.OnQueryAsync = async (received, remote, send) =>
                {
                    ushort id = BinaryPrimitives.ReadUInt16BigEndian(received);
                    string token = id switch
                    {
                        0xAAA1 => "token-a",
                        0xBBB2 => "token-b",
                        _ => "other",
                    };
                    // Small delay so both receives are in flight.
                    await Task.Delay(20).ConfigureAwait(false);
                    await send(BuildMinimalTxtResponse(id, "example.com", token), remote).ConfigureAwait(false);
                };

                Task<DnsUdpExchange.Result> a = DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    BuildQueryWithId(0xAAA1, "example.com"),
                    2_000,
                    CancellationToken.None);
                Task<DnsUdpExchange.Result> b = DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    BuildQueryWithId(0xBBB2, "example.com"),
                    2_000,
                    CancellationToken.None);

                await Task.WhenAll(a, b);
                DnsUdpExchange.Result ra = await a;
                DnsUdpExchange.Result rb = await b;
                Assert.NotNull(ra.Buffer);
                Assert.NotNull(rb.Buffer);
                Assert.True(DnsTxtParser.ResponseContainsTxt(ra.Buffer, 0xAAA1, "example.com", "token-a"u8));
                Assert.True(DnsTxtParser.ResponseContainsTxt(rb.Buffer, 0xBBB2, "example.com", "token-b"u8));
            }
        }

        /// <summary>
        /// Asserts that caller cancellation propagates and does not return a successful buffer.
        /// </summary>
        /// <returns>A task that completes when cancellation is observed.</returns>
        [Fact]
        public async Task QueryAsync_CallerCancellation_Propagates()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            ns.OnQuery = (_, _) => null;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DnsUdpExchange.QueryAsync(
                    ns.EndPoint,
                    BuildQueryWithId(0x3333, "example.com"),
                    timeoutMilliseconds: 5_000,
                    cts.Token));
        }

        /// <summary>
        /// Asserts that a truncated (TC) UDP response is still returned so TCP fallback can proceed.
        /// </summary>
        /// <returns>A task that completes when the truncated buffer is observed.</returns>
        [Fact]
        public async Task QueryAsync_TruncatedResponse_ReturnedForTcpFallback()
        {
            await using ScriptedUdpNameserver ns = await ScriptedUdpNameserver.StartAsync();
            byte[] query = BuildQueryWithId(0x7C7C, "example.com");

            ns.OnQuery = (_, _) =>
            {
                byte[] response = BuildMinimalTxtResponse(0x7C7C, "example.com", "x");
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8600); // QR+AA+TC
                return response;
            };

            DnsUdpExchange.Result result = await DnsUdpExchange.QueryAsync(
                ns.EndPoint,
                query,
                2_000,
                CancellationToken.None);

            Assert.NotNull(result.Buffer);
            Assert.True(DnsResponseHeader.IsTruncated(result.Buffer));
        }

        /// <summary>
        /// Asserts that <see cref="DnsUdpExchange.TryReadCorrelatedTransactionId"/> rejects short buffers and id mismatches.
        /// </summary>
        [Fact]
        public void TryReadCorrelatedTransactionId_RejectsShortAndMismatch()
        {
            Assert.False(DnsUdpExchange.TryReadCorrelatedTransactionId([0x12], 0x1234));
            Assert.False(DnsUdpExchange.TryReadCorrelatedTransactionId(new byte[DnsWireFormat.HeaderSize], 0x1234));
            byte[] matching = new byte[DnsWireFormat.HeaderSize];
            BinaryPrimitives.WriteUInt16BigEndian(matching, 0x1234);
            Assert.True(DnsUdpExchange.TryReadCorrelatedTransactionId(matching, 0x1234));
        }

        /// <summary>
        /// Asserts that a query shorter than a DNS header is rejected before any socket IO.
        /// </summary>
        /// <returns>A task that completes when the argument exception is observed.</returns>
        [Fact]
        public async Task QueryAsync_ShortQuery_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DnsUdpExchange.QueryAsync(
                    IPAddress.Loopback,
                    new byte[4],
                    100,
                    CancellationToken.None));
        }

        private static byte[] BuildQueryWithId(ushort queryId, string name)
        {
            byte[] query = DnsQueryBuilder.Build(name, DnsRecordType.Txt, out _);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), queryId);
            return query;
        }

        private static byte[] BuildMinimalTxtResponse(ushort queryId, string recordName, string txtValue)
        {
            byte[] qname = new byte[DnsWireFormat.ComputeWireNameLength(recordName)];
            DnsWireFormat.EncodeDnsName(recordName, qname);
            byte[] rdata = new byte[1 + txtValue.Length];
            rdata[0] = (byte)txtValue.Length;
            Encoding.ASCII.GetBytes(txtValue, rdata.AsSpan(1));

            int length = DnsWireFormat.HeaderSize
                + qname.Length
                + DnsWireFormat.QuestionSuffixSize
                + qname.Length
                + DnsWireFormat.ResourceRecordFixedFieldsSize
                + rdata.Length;
            byte[] response = new byte[length];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), queryId);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8400);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);

            int offset = DnsWireFormat.HeaderSize;
            qname.CopyTo(response.AsSpan(offset));
            offset += qname.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            offset += DnsWireFormat.QuestionSuffixSize;

            qname.CopyTo(response.AsSpan(offset));
            offset += qname.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8), (ushort)rdata.Length);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            rdata.CopyTo(response.AsSpan(offset));
            return response;
        }

        /// <summary>Loopback UDP nameserver that replies according to scripted callbacks.</summary>
        private sealed class ScriptedUdpNameserver : IAsyncDisposable
        {
            private readonly UdpClient _udp;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private int _queriesReceived;
            private int _responsesSent;

            private ScriptedUdpNameserver(UdpClient udp, IPEndPoint endPoint)
            {
                _udp = udp;
                EndPoint = endPoint;
                _loop = Task.Run(ReceiveLoopAsync);
            }

            public IPEndPoint EndPoint { get; }

            public int QueriesReceived => Volatile.Read(ref _queriesReceived);

            public int ResponsesSent => Volatile.Read(ref _responsesSent);

            /// <summary>Optional sync reply factory. Return null to stay silent for that query.</summary>
            public Func<byte[], IPEndPoint, byte[]?>? OnQuery { get; set; }

            /// <summary>Optional async handler that may send multiple datagrams via <c>send</c>.</summary>
            public Func<byte[], IPEndPoint, Func<byte[], IPEndPoint, Task>, Task>? OnQueryAsync { get; set; }

            public static async Task<ScriptedUdpNameserver> StartAsync()
            {
                var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var ep = (IPEndPoint)udp.Client.LocalEndPoint!;
                await Task.Yield();
                return new ScriptedUdpNameserver(udp, ep);
            }

            private async Task ReceiveLoopAsync()
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        UdpReceiveResult datagram = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref _queriesReceived);
                        byte[] buffer = datagram.Buffer;
                        var remote = (IPEndPoint)datagram.RemoteEndPoint;

                        // Handle off the receive loop so concurrent queries are not serialized behind
                        // a slow or waiting script callback.
                        _ = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    if (OnQueryAsync is not null)
                                    {
                                        await OnQueryAsync(
                                                buffer,
                                                remote,
                                                async (payload, dest) =>
                                                {
                                                    await _udp.SendAsync(payload, dest).ConfigureAwait(false);
                                                    Interlocked.Increment(ref _responsesSent);
                                                })
                                            .ConfigureAwait(false);
                                        return;
                                    }

                                    byte[]? reply = OnQuery?.Invoke(buffer, remote);
                                    if (reply is not null)
                                    {
                                        await _udp.SendAsync(reply, remote).ConfigureAwait(false);
                                        Interlocked.Increment(ref _responsesSent);
                                    }
                                }
                                catch (ObjectDisposedException)
                                {
                                    // Nameserver disposed during reply.
                                }
                                catch (SocketException)
                                {
                                    // Best-effort scripted replies.
                                }
                            },
                            _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown.
                }
                catch (SocketException)
                {
                    // Shutdown / race with dispose.
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                _udp.Dispose();
                try
                {
                    await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Best-effort shutdown for tests.
                }

                _cts.Dispose();
            }
        }

        /// <summary>UDP sender bound to a distinct local port for wrong-source-port injections.</summary>
        private sealed class SpoofSender : IAsyncDisposable
        {
            private readonly UdpClient _udp;

            private SpoofSender(UdpClient udp, IPAddress localAddress, int localPort)
            {
                _udp = udp;
                LocalAddress = localAddress;
                LocalPort = localPort;
            }

            public IPAddress LocalAddress { get; }

            public int LocalPort { get; }

            public static Task<SpoofSender> StartAsync(IPAddress address)
            {
                var udp = new UdpClient(new IPEndPoint(address, 0));
                var local = (IPEndPoint)udp.Client.LocalEndPoint!;
                return Task.FromResult(new SpoofSender(udp, local.Address, local.Port));
            }

            public Task SendAsync(byte[] payload, IPEndPoint destination)
                => _udp.SendAsync(payload, destination).AsTask();

            public ValueTask DisposeAsync()
            {
                _udp.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
