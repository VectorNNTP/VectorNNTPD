using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Session.Commands.Posting;
using VectorNNTPD.TransitService.Session.Framing;

namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class StreamingPostArticleReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionClientIdentity Client =
        ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));

    [Fact]
    public async Task Streaming_MatchesLegacyMaterialize_ForNormalAndDotBodies()
    {
        foreach (var stuffed in new[] { "hello\r\n", "..dot\r\n", "...triple\r\n", "....quad\r\n", "a\r\n..\r\n...\r\n" }
                     .Select(static body => ClientArticle(body: body)))
        {
            var streaming = await ReadStreamingAsync(stuffed);
            var legacy = await LegacyPostMaterialize.ProcessAsync(stuffed, CreateOptions());
            Assert.Equal(StreamingPostReadStatus.Completed, streaming.Status);
            Assert.True(streaming.Wire.Span.SequenceEqual(legacy.Span));
        }
    }

    [Fact]
    public async Task FoldedHeader_IsPreservedOnWire()
    {
        var stuffed = ClientArticle(extraHeaders: "User-Agent: foo\r\n\tbar\r\n");
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Contains("User-Agent: foo\r\n\tbar\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSingleton_IsRejected()
    {
        var stuffed = ClientArticle(extraHeaders: "Subject: again\r\n");
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.DuplicateHeader, result.Failure.Category);
        Assert.True(result.Wire.IsEmpty);
    }

    [Fact]
    public async Task MissingRequiredHeaders_AreRejected()
    {
        var stuffed = "From: poster@example.com\r\nSubject: test\r\n\r\nbody\r\n.\r\n";
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.MissingRequiredHeader, result.Failure.Category);
        Assert.Equal("Newsgroups", result.Failure.Detail);
    }

    [Theory]
    [InlineData("From")]
    [InlineData("Newsgroups")]
    [InlineData("Subject")]
    public async Task MissingMandatoryHeader_IsRejected(string header)
    {
        var stuffed = header switch
        {
            "From" => "Newsgroups: misc.test\r\nSubject: test\r\n\r\nbody\r\n.\r\n",
            "Newsgroups" => "From: poster@example.com\r\nSubject: test\r\n\r\nbody\r\n.\r\n",
            _ => "From: poster@example.com\r\nNewsgroups: misc.test\r\n\r\nbody\r\n.\r\n",
        };
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.MissingRequiredHeader, result.Failure.Category);
        Assert.Equal(header, result.Failure.Detail);
    }

    [Fact]
    public async Task MissingDate_IsGeneratedOnce()
    {
        var result = await ReadStreamingAsync(ClientArticle(date: null));
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountHeader(text, "Date: "));
        Assert.Contains("Date: " + PostRfcDate.Format(Now) + "\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppliedDate_IsPreservedOnce()
    {
        const string date = "25 Sep 2026 11:00:00 +0000";
        var result = await ReadStreamingAsync(ClientArticle(date: date));
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountHeader(text, "Date: "));
        Assert.Contains("Date: " + date + "\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedDate_IsRejected()
    {
        var result = await ReadStreamingAsync(ClientArticle(date: "yesterday"));
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.InvalidDate, result.Failure.Category);
    }

    [Theory]
    [InlineData(25, 0)]
    [InlineData(0, -15)]
    public async Task DateOutsidePolicy_IsRejected(int hours, int days)
    {
        var date = PostRfcDate.Format(Now.AddHours(hours).AddDays(days));
        var result = await ReadStreamingAsync(ClientArticle(date: date));
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.InvalidDate, result.Failure.Category);
    }

    [Fact]
    public async Task MalformedMessageId_IsRejected()
    {
        var result = await ReadStreamingAsync(ClientArticle(messageId: "not-an-id"));
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.InvalidMessageId, result.Failure.Category);
    }

    [Fact]
    public async Task AbsentPath_IsServerGeneratedOnce()
    {
        var result = await ReadStreamingAsync(ClientArticle());
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountHeader(text, "Path: "));
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppliedPath_IsDiscardedAndReplacedOnce()
    {
        var result = await ReadStreamingAsync(ClientArticle(extraHeaders: "Path: evil.path\r\n"));
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountHeader(text, "Path: "));
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.path", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingMessageId_IsPreservedOnce()
    {
        var stuffed = ClientArticle(messageId: "<keep@example.com>");
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal("<keep@example.com>", result.MessageId);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountOccurrences(text, "Message-ID:"));
        Assert.Contains("Message-ID: <keep@example.com>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMessageId_IsGeneratedOnce()
    {
        var stuffed = ClientArticle(messageId: null);
        var result = await ReadStreamingAsync(stuffed);
        Assert.Matches(@"^<[0-9a-f]{32}@usenet\.ninja>$", result.MessageId);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Equal(1, CountOccurrences(text, "Message-ID:"));
        Assert.Contains("Message-ID: " + result.MessageId, text, StringComparison.Ordinal);
        Assert.Contains("logging-data=\"" + result.MessageId + "\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerOwnedHeaders_AreReplaced_AndClientValuesDiscarded()
    {
        var stuffed = ClientArticle(
            extraHeaders:
                "Path: evil.path\r\n" +
                "Injection-Date: forged\r\n" +
                "Injection-Info: forged\r\n" +
                "NNTP-Posting-Date: forged\r\n" +
                "NNTP-Posting-Host: 203.0.113.9\r\n" +
                "X-Trace: forged\r\n" +
                "Xref: other 1\r\n");
        var result = await ReadStreamingAsync(stuffed);
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Date: " + PostRfcDate.Format(Now), text, StringComparison.Ordinal);
        Assert.Contains(
            "Injection-Info: nntpd01.usenet.ninja; logging-data=\"<ok@example.com>\"; mail-complaints-to=\"abuse@usenet.ninja\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains("X-Trace: v1.test-token\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Date", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Xref:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailComplaintsTo_IsWrittenOnInjectionInfo()
    {
        var options = CreateOptions();
        var custom = new StreamingPostReadOptions
        {
            MaxArticleSize = options.MaxArticleSize,
            Time = options.Time,
            NewsgroupPolicy = options.NewsgroupPolicy,
            InjectionIdentity = options.InjectionIdentity,
            ClientIdentity = options.ClientIdentity,
            MailComplaintsTo = "ops@usenet.ninja",
            TraceProtector = options.TraceProtector,
        };
        var result = await ReadStreamingAsync(ClientArticle(), custom);
        Assert.Contains(
            "mail-complaints-to=\"ops@usenet.ninja\"",
            Encoding.ASCII.GetString(result.Wire.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyDotVariants_AreStoredDestuffed()
    {
        var stuffed = ClientArticle(body: "plain\r\n..\r\n...\r\n....\r\n");
        var result = await ReadStreamingAsync(stuffed);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        var body = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("plain\r\n.\r\n..\r\n...\r\n", body);
    }

    [Theory]
    [InlineData("hello\r\n", "hello\r\n")]
    [InlineData("..hidden\r\n", ".hidden\r\n")]
    [InlineData("...hidden\r\n", "..hidden\r\n")]
    [InlineData("....hidden\r\n", "...hidden\r\n")]
    public async Task Body_RemovesOneStuffingLayer(string stuffedBody, string destuffedBody)
    {
        var result = await ReadStreamingAsync(ClientArticle(body: stuffedBody));
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        var text = Encoding.ASCII.GetString(result.Wire.Span);
        var body = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal(destuffedBody, body);
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.False(result.Wire.Span.EndsWith(".\r\n"u8));
    }

    [Fact]
    public async Task CrlfAndDotSplitAcrossSegments_ProduceIdenticalWire()
    {
        var stuffed = ClientArticle(body: "..hello\r\n...more\r\n");
        var expected = await ReadStreamingAsync(stuffed);
        foreach (var chunkSize in new[] { 1, 2, 3, 7 })
        {
            var segmented = await ReadStreamingAsync(stuffed, CreateOptions(), chunkSize);
            Assert.Equal(StreamingPostReadStatus.Completed, segmented.Status);
            Assert.True(expected.Wire.Span.SequenceEqual(segmented.Wire.Span), "chunk " + chunkSize);
        }
    }

    [Fact]
    public async Task ExactMaxArticleSize_IsAccepted()
    {
        var destuffed = PaddedDestuffed(512);
        Assert.Equal(512, destuffed.Length);
        var stuffed = destuffed + ".\r\n";
        var result = await ReadStreamingAsync(stuffed, CreateOptions(maxArticleSize: 512));
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        Assert.Equal(512, result.DestuffedSize);
    }

    [Fact]
    public async Task MaxArticleSizePlusOne_IsRejected()
    {
        var destuffed = PaddedDestuffed(513);
        var result = await ReadStreamingAsync(destuffed + ".\r\n", CreateOptions(maxArticleSize: 512));
        Assert.Equal(StreamingPostReadStatus.TooLarge, result.Status);
        Assert.True(result.Wire.IsEmpty);
    }

    [Fact]
    public async Task DotStuffing_DoesNotCountTowardMaxArticleSize()
    {
        var prefix = ClientArticle(body: string.Empty, includeTerminator: false);
        var destuffedPrefix = Encoding.ASCII.GetByteCount(prefix);
        var destuffedBody = Encoding.ASCII.GetByteCount(".hello\r\n");
        var limit = destuffedPrefix + destuffedBody;
        var stuffed = prefix + "..hello\r\n.\r\n";
        Assert.True(Encoding.ASCII.GetByteCount(stuffed) - 3 > limit);

        var accepted = await ReadStreamingAsync(stuffed, CreateOptions(maxArticleSize: limit));
        Assert.Equal(StreamingPostReadStatus.Completed, accepted.Status);
        Assert.Equal(limit, accepted.DestuffedSize);
        Assert.Contains(".hello\r\n", Encoding.ASCII.GetString(accepted.Wire.Span), StringComparison.Ordinal);
        Assert.DoesNotContain("..hello\r\n", Encoding.ASCII.GetString(accepted.Wire.Span), StringComparison.Ordinal);

        var rejected = await ReadStreamingAsync(stuffed, CreateOptions(maxArticleSize: limit - 1));
        Assert.Equal(StreamingPostReadStatus.TooLarge, rejected.Status);
    }

    [Fact]
    public async Task Cancellation_Throws_AndDoesNotCompleteWire()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource();
        var read = StreamingPostArticleReader.ReadAsync(pipe.Reader, CreateOptions(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
    }

    [Fact]
    public async Task EofBeforeTerminator_IsIncomplete()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(ClientArticle(includeTerminator: false)));
        await pipe.Writer.CompleteAsync();
        var result = await StreamingPostArticleReader.ReadAsync(pipe.Reader, CreateOptions(), CancellationToken.None);
        Assert.Equal(StreamingPostReadStatus.Incomplete, result.Status);
        Assert.True(result.Wire.IsEmpty);
    }

    [Fact]
    public async Task Completed_ExposesParsedMessageIdAndNewsgroups()
    {
        // Finish()'s Completed path requires a materialized ParsedPostArticle; regressions that
        // leave _parsed null must not surface as empty identity fields.
        var result = await ReadStreamingAsync(ClientArticle());
        Assert.Equal(StreamingPostReadStatus.Completed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.MessageId));
        Assert.Contains("misc.test", result.Newsgroups);
        Assert.False(result.Wire.IsEmpty);
    }

    [Fact]
    public async Task MalformedArticle_IsRejected()
    {
        var result = await ReadStreamingAsync("not a header\r\n.\r\n");
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.MalformedHeader, result.Failure.Category);
    }

    [Fact]
    public async Task MissingTraceProtector_IsRejectedAfterHeaders()
    {
        var options = new StreamingPostReadOptions
        {
            MaxArticleSize = 64 * 1024,
            Time = new FixedTimeProvider(Now),
            NewsgroupPolicy = SyntaxOnlyNewsgroupPostingPolicy.Instance,
            InjectionIdentity = "nntpd01.usenet.ninja",
            ClientIdentity = Client,
            MailComplaintsTo = NntpdOptions.DefaultMailComplaintsTo,
            TraceProtector = null,
        };
        var result = await ReadStreamingAsync(ClientArticle(), options);
        Assert.Equal(StreamingPostReadStatus.Rejected, result.Status);
        Assert.Equal(PostingFailureCategory.PersistenceFailure, result.Failure.Category);
    }

    private static async Task<StreamingPostReadResult> ReadStreamingAsync(
        string stuffedWithTerminator,
        StreamingPostReadOptions? options = null,
        int? chunkSize = null)
    {
        options ??= CreateOptions();
        var bytes = Encoding.ASCII.GetBytes(stuffedWithTerminator);
        if (chunkSize is int size && size > 0)
        {
            var sequence = SegmentedSequence.FromChunks(bytes, size);
            var reader = PipeReader.Create(sequence);
            return await StreamingPostArticleReader.ReadAsync(reader, options, CancellationToken.None);
        }

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes);
        await pipe.Writer.CompleteAsync();
        return await StreamingPostArticleReader.ReadAsync(pipe.Reader, options, CancellationToken.None);
    }

    private static StreamingPostReadOptions CreateOptions(int maxArticleSize = 64 * 1024) =>
        new()
        {
            MaxArticleSize = maxArticleSize,
            Time = new FixedTimeProvider(Now),
            NewsgroupPolicy = SyntaxOnlyNewsgroupPostingPolicy.Instance,
            InjectionIdentity = "nntpd01.usenet.ninja",
            ClientIdentity = Client,
            MailComplaintsTo = NntpdOptions.DefaultMailComplaintsTo,
            TraceProtector = DeterministicTraceProtector.Instance,
        };

    private static string ClientArticle(
        string? messageId = "<ok@example.com>",
        string extraHeaders = "",
        string body = "body\r\n",
        bool includeTerminator = true,
        string? date = "")
    {
        var sb = new StringBuilder();
        if (date is not null)
        {
            sb.Append("Date: ").Append(date.Length == 0 ? PostRfcDate.Format(Now) : date).Append("\r\n");
        }

        sb.Append("From: poster@example.com\r\n");
        sb.Append("Newsgroups: misc.test\r\n");
        sb.Append("Subject: test\r\n");
        if (messageId is not null)
        {
            sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        }

        sb.Append(extraHeaders);
        sb.Append("\r\n");
        sb.Append(body);
        if (includeTerminator)
        {
            sb.Append(".\r\n");
        }

        return sb.ToString();
    }

    private static string PaddedDestuffed(int destuffedBytes)
    {
        var prefix = ClientArticle(body: string.Empty, includeTerminator: false);
        var needed = destuffedBytes - Encoding.ASCII.GetByteCount(prefix);
        Assert.True(needed >= 2);
        return prefix + new string('Z', needed - 2) + "\r\n";
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static int CountHeader(string text, string header)
    {
        var count = text.StartsWith(header, StringComparison.Ordinal) ? 1 : 0;
        return count + CountOccurrences(text, "\r\n" + header);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    internal sealed class DeterministicTraceProtector : IPostingTraceProtector
    {
        public static DeterministicTraceProtector Instance { get; } = new();

        public string Protect(PostingTracePayload payload) => "v1.test-token";

        public bool TryUnprotect(ReadOnlySpan<char> token, out PostingTracePayload payload)
        {
            payload = default;
            return false;
        }
    }
}

internal static class LegacyPostMaterialize
{
    public static async Task<ReadOnlyMemory<byte>> ProcessAsync(
        string stuffedWithTerminator,
        StreamingPostReadOptions options)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(stuffedWithTerminator));
        await pipe.Writer.CompleteAsync();
        var destuffed = await NntpMultilineDataReader.ReadArticleAsync(
            pipe.Reader,
            options.MaxArticleSize,
            CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, destuffed.Status);
        Assert.True(PostHeaderParser.TryParse(destuffed.Payload, out var parsed, out var failure), failure.Detail);
        Assert.True(
            PostArticleValidator.TryValidate(parsed!, options.Time.GetUtcNow(), options.NewsgroupPolicy, out failure),
            failure.Detail);
        var normalized = PostHeaderNormalizer.Normalize(
            parsed!,
            options.Time.GetUtcNow(),
            options.InjectionIdentity,
            options.ClientIdentity,
            options.MailComplaintsTo,
            options.TraceProtector!);
        return normalized;
    }
}

internal static class SegmentedSequence
{
    public static ReadOnlySequence<byte> FromChunks(ReadOnlySpan<byte> source, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        if (source.IsEmpty)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        BufferSegment? first = null;
        BufferSegment? last = null;
        var offset = 0;
        while (offset < source.Length)
        {
            var take = Math.Min(chunkSize, source.Length - offset);
            var memory = source.Slice(offset, take).ToArray();
            var segment = new BufferSegment(memory);
            if (first is null)
            {
                first = segment;
            }
            else
            {
                last!.Append(segment);
            }

            last = segment;
            offset += take;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void Append(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
