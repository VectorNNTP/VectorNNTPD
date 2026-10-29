using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.Session;

namespace VectorNNTPD.ReaderService.Tests.Session.Commands;

/// <summary>
/// Phase C: byte-native dynamic replies and one-write static multiline bodies.
/// </summary>
public sealed class NntpPhaseCResponseTests
{
    private static readonly NntpAuthorization TransitAuth = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Theory]
    [InlineData("<a@b>")]
    [InlineData("<dyn@example.com>")]
    [InlineData("<i.am.an.article.you.will.want@example.com>")]
    public void Check_Compose_ExactWire_FromMessageIdBytes(string messageId)
    {
        var mid = Encoding.ASCII.GetBytes(messageId);
        var wire = NntpResponseCompose.Concat(
            NntpResponses.CheckPrefix.Span,
            mid,
            NntpResponses.CheckSuffix.Span);
        Assert.Equal(
            "238 " + messageId + " send article to be transferred\r\n",
            Encoding.ASCII.GetString(wire));
    }

    [Fact]
    public void Check_Compose_NearMaximumMessageId()
    {
        var mid = NearMaxMessageId();
        Assert.Equal(VectorNNTPD.Common.Articles.Validation.NntpMessageIdValidation.MaxMessageIdLength, mid.Length);
        Assert.True(VectorNNTPD.Common.Articles.Validation.NntpMessageIdValidation.IsValidMessageId(mid));

        var wire = NntpResponseCompose.Concat(
            NntpResponses.CheckPrefix.Span,
            mid,
            NntpResponses.CheckSuffix.Span);
        var expected = "238 " + Encoding.ASCII.GetString(mid) + " send article to be transferred\r\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(wire));
        Assert.Equal(4 + mid.Length + NntpResponses.CheckSuffix.Length, wire.Length);
    }

    [Theory]
    [InlineData("<a@ex.com>")]
    [InlineData("<article-one@example.com>")]
    public void TakeThis_Compose_Exact239And439_FromMessageIdBytes(string messageId)
    {
        var mid = Encoding.ASCII.GetBytes(messageId);
        var ok = NntpResponseCompose.Concat(
            NntpResponses.ArticleTransferredOkPrefix.Span,
            mid,
            NntpResponses.Crlf.Span);
        var rejected = NntpResponseCompose.Concat(
            NntpResponses.TransferRejectedPrefix.Span,
            mid,
            NntpResponses.Crlf.Span);
        Assert.Equal("239 " + messageId + "\r\n", Encoding.ASCII.GetString(ok));
        Assert.Equal("439 " + messageId + "\r\n", Encoding.ASCII.GetString(rejected));
    }

    [Fact]
    public void Date_FormatUtcDateLine_ExactStatusAndStamp()
    {
        var utc = new DateTime(2026, 9, 24, 1, 2, 3, DateTimeKind.Utc);
        var wire = Date.FormatUtcDateLine(utc);
        Assert.Equal("111 20260924010203\r\n", Encoding.ASCII.GetString(wire));
        Assert.Equal(20, wire.Length);
        Assert.Equal(
            utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            Encoding.ASCII.GetString(wire, 4, 14));
    }

    [Fact]
    public void StaticSingleLine_ConnectionClosing_RemainsByteIdentical()
    {
        Assert.True(NntpResponses.ConnectionClosing.Span.SequenceEqual("205 Connection closing\r\n"u8));
    }

    [Fact]
    public async Task Help_Dispatch_OneEnqueue_ExactCompleteWire()
    {
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        var expected = ExpectedHelpWire();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "HELP");
        Assert.Equal(Encoding.ASCII.GetString(expected), await read);
        Assert.Equal(1, response.ChannelEnqueueCount);
        Assert.Equal(Help.BodyLines.Count + 2, CountCrlf(expected));
    }

    [Fact]
    public async Task Capabilities_DefaultSession_OneEnqueue_ExactWire()
    {
        var expected = Concat(
            NntpResponses.CapabilityListFollows,
            NntpResponses.CapabilityVersion2,
            NntpResponses.CapabilityImplementation,
            NntpResponses.CapabilityReader,
            NntpResponses.CapabilityList,
            NntpResponses.CapabilityModeReader,
            NntpResponses.CapabilityAuthinfoUser,
            NntpResponses.CapabilityStartTls,
            NntpResponses.CapabilityCompressDeflate,
            NntpResponses.MultilineTerminator);

        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CAPABILITIES");
        Assert.Equal(Encoding.ASCII.GetString(expected), await read);
        Assert.Equal(1, response.ChannelEnqueueCount);
        Assert.Contains("VERSION 2", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("IHAVE", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("STREAMING", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("SPEEDTEST", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("NEWNEWS", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("NEWGROUPS", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.True(expected.AsSpan().EndsWith(".\r\n"u8));
    }

    [Fact]
    public async Task Capabilities_Authenticated_OmitsAuthinfoAndModeReader_OneEnqueue()
    {
        var expected = Concat(
            NntpResponses.CapabilityListFollows,
            NntpResponses.CapabilityVersion2,
            NntpResponses.CapabilityImplementation,
            NntpResponses.CapabilityReader,
            NntpResponses.CapabilityList,
            NntpResponses.CapabilityStartTls,
            NntpResponses.CapabilityCompressDeflate,
            NntpResponses.MultilineTerminator);

        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.ApplySuccessfulAuthentication(
            "user",
            new NntpAuthorization(
                isAuthenticated: true,
                authorizedReader: true,
                authorizedTransit: true,
                postingPermitted: false,
                streamingPermitted: true));
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CAPABILITIES");
        Assert.Equal(Encoding.ASCII.GetString(expected), await read);
        Assert.Equal(1, response.ChannelEnqueueCount);
        Assert.DoesNotContain("AUTHINFO", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("MODE-READER", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("IHAVE", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("STREAMING", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
        Assert.DoesNotContain("SPEEDTEST", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Dispatch_OnReader_Exact502Bytes_OneEnqueue()
    {
        const string id = "<want@example.com>";
        var expected = "502 Command not available on this server\r\n";
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(TransitAuth);
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CHECK " + id);
        Assert.Equal(expected, await read);
        Assert.Equal(1, response.ChannelEnqueueCount);
    }

    [Fact]
    public async Task Check_Dispatch_OnReader_NearMaximumMessageId_Exact502Bytes()
    {
        var mid = Encoding.ASCII.GetString(NearMaxMessageId());
        var expected = "502 Command not available on this server\r\n";
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(TransitAuth);
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "CHECK " + mid);
        Assert.Equal(expected, await read);
    }

    [Fact]
    public async Task TakeThis_Dispatch_OnReader_Exact502_AndNothingQueued()
    {
        const string id = "<article-one@example.com>";
        var expected = "502 Command not available on this server\r\n";
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        // The Reader role gate answers before any article is read, so no body is written.
        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "TAKETHIS " + id);
        Assert.Equal(1, response.ChannelEnqueueCount);
        await response.FlushCoalescedAsync();
        Assert.Equal(expected, await read);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TakeThis_Dispatch_OnReader_OversizedLimit_Exact502_NotRejection()
    {
        const string id = "<big@ex.com>";
        var expected = "502 Command not available on this server\r\n";
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(expected.Length);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "TAKETHIS " + id);
        Assert.Equal(1, response.ChannelEnqueueCount);
        await response.FlushCoalescedAsync();
        Assert.Equal(expected, await read);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Date_Dispatch_ExactStatusAndDigitStamp_OneEnqueue()
    {
        await using var duplex = await PhaseCDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();

        var read = duplex.ReadExactAsciiAsync(20);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "DATE");
        var wire = await read;
        Assert.Equal(20, wire.Length);
        Assert.StartsWith("111 ", wire, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", wire, StringComparison.Ordinal);
        Assert.All(wire[4..18], static c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Equal(1, response.ChannelEnqueueCount);
    }

    private static byte[] ExpectedHelpWire()
    {
        var builder = new StringBuilder();
        builder.Append("100 Help text follows\r\n");
        foreach (var line in Help.BodyLines)
        {
            builder.Append(line).Append("\r\n");
        }

        builder.Append(".\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] Concat(params ReadOnlyMemory<byte>[] parts) =>
        NntpResponseCompose.Concatenate(parts);

    private static byte[] NearMaxMessageId()
    {
        var mid = new byte[VectorNNTPD.Common.Articles.Validation.NntpMessageIdValidation.MaxMessageIdLength];
        mid[0] = (byte)'<';
        for (var i = 1; i < mid.Length - 3; i++)
        {
            mid[i] = (byte)'a';
        }

        mid[^3] = (byte)'@';
        mid[^2] = (byte)'x';
        mid[^1] = (byte)'>';
        return mid;
    }

    private static int CountCrlf(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n')
            {
                count++;
            }
        }

        return count;
    }

    private sealed class PhaseCDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<PhaseCDuplex> CreateAsync() => Task.FromResult(new PhaseCDuplex());

        public NntpSession CreateSession(
            IArticleIngestionQueue? queue = null,
            bool allowCleartextAuth = true)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                allowCleartextAuth: allowCleartextAuth);
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadExactAsciiAsync(int byteCount)
        {
            var buffer = new byte[byteCount];
            var copied = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (copied < byteCount)
            {
                var result = await _serverToClient.Reader.ReadAsync(timeout.Token);
                var unread = result.Buffer;
                var take = (int)Math.Min(unread.Length, byteCount - copied);
                unread.Slice(0, take).CopyTo(buffer.AsSpan(copied, take));
                copied += take;
                _serverToClient.Reader.AdvanceTo(unread.GetPosition(take));
                if (result.IsCompleted && copied < byteCount)
                {
                    throw new InvalidOperationException($"Pipe completed after {copied} of {byteCount} bytes.");
                }
            }

            return Encoding.ASCII.GetString(buffer);
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls => false;
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
