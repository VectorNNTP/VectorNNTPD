using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blake3;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

/// <summary>Test-only helpers for production <see cref="NntpMultilineDataReader"/> / restuff coverage.</summary>
internal static class FramingPipe
{
    public static async Task<NntpMultilineReadResult> ReadOneAsync(
        byte[] wire,
        int maxArticleBytes = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        return await NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, maxArticleBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<NntpMultilineReadResult> ReadSegmentedAsync(
        byte[] wire,
        int segmentSize,
        int maxArticleBytes = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: Math.Max(1, segmentSize),
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024,
            useSynchronizationContext: false));

        var read = NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, maxArticleBytes, cancellationToken)
            .AsTask();
        await WriteChunkedAsync(pipe.Writer, wire, segmentSize, cancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        return await read.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteChunkedAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> wire,
        int chunkSize,
        CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < wire.Length)
        {
            var n = Math.Min(chunkSize, wire.Length - offset);
            await writer.WriteAsync(wire.Slice(offset, n), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            offset += n;
        }
    }

    public static async Task<NntpMultilineReadResult> ReadSplitTerminatorAsync(
        byte[] wire,
        int holdBackBytes = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(holdBackBytes, 1);
        if (wire.Length <= holdBackBytes)
        {
            throw new ArgumentException("Wire is shorter than the split.", nameof(wire));
        }

        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: 8,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            useSynchronizationContext: false));
        var read = NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, 8 * 1024 * 1024, cancellationToken)
            .AsTask();
        await pipe.Writer.WriteAsync(wire.AsMemory()[..^holdBackBytes], cancellationToken).ConfigureAwait(false);
        await pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pipe.Writer.WriteAsync(wire.AsMemory()[^holdBackBytes..], cancellationToken).ConfigureAwait(false);
        await pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        return await read.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Synthetic complete-wire articles for production destuff tests.</summary>
internal static class FramingWireFactory
{
    public static byte[] EmptyArticle { get; } = ".\r\n"u8.ToArray();

    public static byte[] WithTerminator(string bodyWithoutTerminator) =>
        Encoding.ASCII.GetBytes(bodyWithoutTerminator + ".\r\n");

    public static byte[] DataThenDelimiter() => WithTerminator("data\r\n");

    public static byte[] SmallArticle()
    {
        var sb = new StringBuilder(1600);
        for (var i = 0; i < 40; i++)
        {
            sb.Append("Subject: line-").Append(i).Append("\r\n");
        }

        sb.Append("\r\n");
        for (var i = 0; i < 20; i++)
        {
            sb.Append("body text line ").Append(i).Append(" with some padding abcdefghijklmnop\r\n");
        }

        return WithTerminator(sb.ToString());
    }

    public static byte[] MediumArticle() => BuildFilledArticle(128 * 1024, 64);

    public static byte[] Large768KiBArticle() => BuildFilledArticle(768 * 1024, 128);

    public static byte[] ManyCrlfArticle()
    {
        var sb = new StringBuilder(70_000);
        for (var i = 0; i < 8000; i++)
        {
            sb.Append('x').Append(i % 10).Append("\r\n");
        }

        return WithTerminator(sb.ToString());
    }

    public static byte[] ManyDotStuffedLinesArticle()
    {
        var sb = new StringBuilder(40_000);
        for (var i = 0; i < 2000; i++)
        {
            sb.Append("..stuffed-line-").Append(i).Append("\r\n");
        }

        return WithTerminator(sb.ToString());
    }

    public static byte[] TrailingPeriodLinesArticle()
    {
        var sb = new StringBuilder(40_000);
        sb.Append("Hello.\r\n");
        sb.Append("Something else.\r\n");
        sb.Append("This line has a trailing period.\r\n");
        for (var i = 0; i < 1000; i++)
        {
            sb.Append("Line ").Append(i).Append(" ends with a period.\r\n");
        }

        return WithTerminator(sb.ToString());
    }

    public static byte[] ManyDotsHostileArticle()
    {
        var sb = new StringBuilder(280_000);
        for (var i = 0; i < 4000; i++)
        {
            sb.Append("Hello.\r\n");
            sb.Append("....not-a-terminator.\r\n");
            sb.Append("..\r\n");
            sb.Append("x.y.z.\r\n");
        }

        return WithTerminator(sb.ToString());
    }

    public static byte[] PipelinedTwoArticles()
    {
        var a1 = WithTerminator("article-one\r\n");
        var a2 = WithTerminator("article-two\r\n");
        var combined = new byte[a1.Length + a2.Length];
        a1.CopyTo(combined, 0);
        a2.CopyTo(combined, a1.Length);
        return combined;
    }

    public static byte[] ExpectedDestuffed(ReadOnlySpan<byte> completeWire) =>
        DestuffCompleteWire(completeWire);

    public static int ExpectedDestuffedLength(ReadOnlySpan<byte> completeWire) =>
        DestuffCompleteWire(completeWire).Length;

    public static byte[] DestuffCompleteWire(ReadOnlySpan<byte> completeWire)
    {
        if (completeWire.Length == 3
            && completeWire[0] == (byte)'.'
            && completeWire[1] == (byte)'\r'
            && completeWire[2] == (byte)'\n')
        {
            return [];
        }

        if (completeWire.Length < 3
            || completeWire[^3] != (byte)'.'
            || completeWire[^2] != (byte)'\r'
            || completeWire[^1] != (byte)'\n')
        {
            throw new ArgumentException("Expected a complete wire article ending in .\\r\\n.", nameof(completeWire));
        }

        return DestuffPayload(completeWire[..^3]);
    }

    public static byte[] DestuffPayload(ReadOnlySpan<byte> wirePayloadWithoutTerminator)
    {
        if (wirePayloadWithoutTerminator.IsEmpty)
        {
            return [];
        }

        var output = new ArrayBufferWriter<byte>(wirePayloadWithoutTerminator.Length);
        var offset = 0;
        while (offset < wirePayloadWithoutTerminator.Length)
        {
            var remaining = wirePayloadWithoutTerminator[offset..];
            var crlf = remaining.IndexOf("\r\n"u8);
            if (crlf < 0)
            {
                throw new InvalidOperationException("Expected CRLF-framed wire payload.");
            }

            var line = remaining[..crlf];
            if (line.Length > 0 && line[0] == (byte)'.')
            {
                line = line[1..];
            }

            var span = output.GetSpan(line.Length + 2);
            line.CopyTo(span);
            span[line.Length] = (byte)'\r';
            span[line.Length + 1] = (byte)'\n';
            output.Advance(line.Length + 2);
            offset += crlf + 2;
        }

        return output.WrittenSpan.ToArray();
    }

    public static byte[] BuildTakeThisTransaction(string messageId, ReadOnlySpan<byte> storedDestuffed)
    {
        var header = Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n");
        var body = ArticleWireReconstructor.RestuffArticle(storedDestuffed);
        var wire = new byte[header.Length + body.Length];
        header.CopyTo(wire, 0);
        body.CopyTo(wire, header.Length);
        return wire;
    }

    public static byte[] BuildFixedSizeTakethisStream(int articleBytes, int articleCount, int seed = 42)
    {
        var rng = new Random(seed);
        using var ms = new MemoryStream();
        var body = new byte[articleBytes];
        for (var i = 0; i < articleCount; i++)
        {
            ms.Write(Encoding.ASCII.GetBytes($"TAKETHIS <bench-{i}@vectornntp.test>\r\n"));
            for (var b = 0; b < body.Length; b++)
            {
                if (b > 0 && b % 256 == 255)
                {
                    body[b] = (byte)'\n';
                    body[b - 1] = (byte)'\r';
                }
                else
                {
                    var v = (byte)rng.Next(1, 250);
                    body[b] = v == (byte)'.' ? (byte)'x' : v;
                }
            }

            if (body.Length >= 2)
            {
                body[^2] = (byte)'\r';
                body[^1] = (byte)'\n';
            }

            if (body.Length > 16)
            {
                body[8] = (byte)(i & 0xFF);
                body[9] = (byte)((i >> 8) & 0xFF);
            }

            ms.Write(body);
            ms.Write(".\r\n"u8);
        }

        return ms.ToArray();
    }

    private static byte[] BuildFilledArticle(int targetBodyBytes, int lineLength)
    {
        var sb = new StringBuilder(targetBodyBytes + 64);
        var n = 0;
        while (sb.Length < targetBodyBytes)
        {
            var pad = new string('a', Math.Max(1, lineLength - 8));
            sb.Append(n).Append(' ').Append(pad).Append("\r\n");
            n++;
        }

        return WithTerminator(sb.ToString());
    }
}

/// <summary>Production command-line + multiline reader over a TAKETHIS/CHECK/QUIT stream.</summary>
internal static class ProductionTakethisStream
{
    public static async Task<TakethisStreamResult> ParseAsync(
        byte[] wire,
        bool stopAfterQuit = false,
        int? chunkSize = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: Math.Max(1, chunkSize ?? 4096),
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024,
            useSynchronizationContext: false));
        var parse = ParseFromReaderAsync(pipe.Reader, stopAfterQuit, cancellationToken);
        if (chunkSize is int size)
        {
            await FramingPipe.WriteChunkedAsync(pipe.Writer, wire, size, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await pipe.Writer.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        }

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        return await parse.ConfigureAwait(false);
    }

    private static async Task<TakethisStreamResult> ParseFromReaderAsync(
        PipeReader reader,
        bool stopAfterQuit,
        CancellationToken cancellationToken)
    {
        var articles = 0;
        var articleBytes = 0L;
        var checks = 0;
        var quits = 0;
        var payloads = new List<ReadOnlyMemory<byte>>();
        var messageIds = new List<string>();

        while (true)
        {
            var line = await NntpCommandLineReader
                .ReadLineAsync(reader, cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("TAKETHIS ", StringComparison.OrdinalIgnoreCase))
            {
                var id = line["TAKETHIS ".Length..];
                var article = await NntpMultilineDataReader
                    .ReadArticleAsync(reader, 8 * 1024 * 1024, cancellationToken)
                    .ConfigureAwait(false);
                if (article.Status != NntpMultilineReadStatus.Completed)
                {
                    return new TakethisStreamResult(
                        IsComplete: false,
                        articles,
                        articleBytes,
                        checks,
                        quits,
                        payloads,
                        messageIds);
                }

                articles++;
                articleBytes += article.Payload.Length;
                payloads.Add(article.Payload);
                messageIds.Add(id);
                continue;
            }

            if (line.StartsWith("CHECK ", StringComparison.OrdinalIgnoreCase))
            {
                checks++;
                continue;
            }

            if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                quits++;
                if (stopAfterQuit)
                {
                    break;
                }
            }
        }

        return new TakethisStreamResult(true, articles, articleBytes, checks, quits, payloads, messageIds);
    }
}

internal readonly record struct TakethisStreamResult(
    bool IsComplete,
    int Articles,
    long ArticleBytes,
    int Checks,
    int Quits,
    IReadOnlyList<ReadOnlyMemory<byte>> Payloads,
    IReadOnlyList<string> MessageIds);

/// <summary>INN fixture loader for the corpus already stored under tests/Data/InnArticles.</summary>
internal static class InnArticleCorpus
{
    public const string UpstreamCommit = "6843efceb8a15774d23e4e2892d08daf54574314";

    private static readonly object Gate = new();
    private static InnArticleManifest? _manifest;
    private static string? _root;

    public static string ResolveRoot()
    {
        if (_root is not null)
        {
            return _root;
        }

        lock (Gate)
        {
            if (_root is not null)
            {
                return _root;
            }

            var candidates = new[]
            {
                Path.Join(AppContext.BaseDirectory, "Data", "InnArticles"),
                Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "Data", "InnArticles")),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Join(candidate, "inn-articles.manifest.json")))
                {
                    _root = candidate;
                    return _root;
                }
            }

            throw new DirectoryNotFoundException(
                "INN article corpus not found. Expected Data/InnArticles/inn-articles.manifest.json.");
        }
    }

    public static InnArticleManifest LoadManifest()
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        lock (Gate)
        {
            if (_manifest is not null)
            {
                return _manifest;
            }

            var json = File.ReadAllText(Path.Join(ResolveRoot(), "inn-articles.manifest.json"));
            var manifest = JsonSerializer.Deserialize(json, InnArticleManifestJsonContext.Default.InnArticleManifest)
                           ?? throw new InvalidOperationException("Failed to deserialize inn-articles.manifest.json.");
            _manifest = manifest;
            return _manifest;
        }
    }

    public static byte[] ReadAllBytes(string relativePath)
    {
        var full = Path.Join(ResolveRoot(), relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"INN fixture missing: {relativePath}", full);
        }

        return File.ReadAllBytes(full);
    }

    public static void VerifySha256(InnArticleManifestEntry entry)
    {
        var bytes = ReadAllBytes(entry.RelativePath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // Hash the raw file bytes. InnArticles is marked binary in .gitattributes so
        // core.autocrlf cannot rewrite LF fixtures before this comparison.
        if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch for {entry.RelativePath}: disk={hash}, manifest={entry.Sha256}.");
        }

        if (bytes.Length != entry.Length)
        {
            throw new InvalidOperationException(
                $"Length mismatch for {entry.RelativePath}: disk={bytes.Length}, manifest={entry.Length}.");
        }
    }

    public static IReadOnlyList<InnArticleManifestEntry> CompleteWireArticles() =>
        LoadManifest().Files
            .Where(static f => f.FramingClassification == "complete_multiline_wire")
            .ToArray();

    public static bool ContainsFiveByteDelimiter(ReadOnlySpan<byte> data) =>
        data.IndexOf("\r\n.\r\n"u8) >= 0;
}

internal sealed class InnArticleManifest
{
    [JsonPropertyName("repository")]
    public string Repository { get; set; } = "";

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = "";

    [JsonPropertyName("upstreamCommit")]
    public string UpstreamCommit { get; set; } = "";

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("files")]
    public List<InnArticleManifestEntry> Files { get; set; } = [];
}

internal sealed class InnArticleManifestEntry
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("framingClassification")]
    public string FramingClassification { get; set; } = "";

    [JsonPropertyName("containsLeadingDotLines")]
    public bool ContainsLeadingDotLines { get; set; }
}

[JsonSerializable(typeof(InnArticleManifest))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class InnArticleManifestJsonContext : JsonSerializerContext;

internal static class MessageIdDigest
{
    public static string ComputeHexLower(string messageIdWithBrackets)
    {
        var utf8 = Encoding.UTF8.GetBytes(messageIdWithBrackets);
        var hash = Hasher.Hash(utf8);
        return Convert.ToHexString(hash.AsSpan()).ToLowerInvariant();
    }

    public static string BuildRelativePath(string digestHexLower) =>
        Path.Join(digestHexLower[..2], digestHexLower.Substring(2, 2), digestHexLower);
}

internal static class StoredArticleReader
{
    public static bool TryExtractMessageId(ReadOnlySpan<byte> storedHead, out string messageIdWithBrackets)
    {
        messageIdWithBrackets = string.Empty;
        var offset = 0;
        while (offset < storedHead.Length)
        {
            var remaining = storedHead[offset..];
            var eol = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> physical;
            if (eol < 0)
            {
                physical = remaining;
                offset = storedHead.Length;
            }
            else
            {
                physical = remaining[..eol];
                offset += eol + 1;
            }

            if (!physical.IsEmpty && physical[^1] == (byte)'\r')
            {
                physical = physical[..^1];
            }

            if (physical.Length == 0)
            {
                break;
            }

            if (!StartsWithIgnoreCase(physical, "Message-ID:"u8))
            {
                continue;
            }

            var colon = physical.IndexOf((byte)':');
            var value = physical[(colon + 1)..];
            while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t')
            {
                value = value[1..];
            }

            var lt = value.IndexOf((byte)'<');
            var gt = value.LastIndexOf((byte)'>');
            if (lt >= 0 && gt > lt)
            {
                messageIdWithBrackets = Encoding.ASCII.GetString(value[lt..(gt + 1)]);
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> asciiPrefix)
    {
        if (line.Length < asciiPrefix.Length)
        {
            return false;
        }

        for (var i = 0; i < asciiPrefix.Length; i++)
        {
            var a = line[i];
            var b = asciiPrefix[i];
            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }
}
