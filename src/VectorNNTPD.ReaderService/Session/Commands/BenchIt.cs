using System.Text;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// Internal benchmark facility: unadvertised <c>BENCHIT</c> command that serves one static
    /// ~750 KiB ARTICLE-style multiline response over the production transport path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a production protocol feature and not advertised via <c>CAPABILITIES</c>, HELP, or the public
    /// command inventory. Retained intentionally for transport performance baselines and regressions.
    /// Registered so the real dispatcher/session stack can be exercised against actual TCP sockets.
    /// </para>
    /// <para>
    /// The complete NNTP multiline wire representation (status line, CRLF article lines with dot-stuffing,
    /// and terminating <c>.CRLF</c>) is generated exactly once at type initialization and reused immutably
    /// for every request. Per-request work is: dispatch → write precomputed bytes → pipe/transport flush.
    /// </para>
    /// <para>
    /// Logging: no per-request INFO TX completion (handler does not use <see cref="NntpCommandExecution"/>).
    /// Session RX logging for <c>BENCHIT</c> is suppressed separately.
    /// </para>
    /// </remarks>
    internal static class BenchIt
    {
        /// <summary>Target article content size before NNTP multiline framing (750 KiB).</summary>
        public const int TargetArticleContentBytes = 750 * 1024;

        /// <summary>Stable Message-ID used in the static article and 220 status line.</summary>
        private const string MessageId = "<benchit-static@vectornntp.local>";

        /// <summary>Immutable ASCII article bytes (headers + body), length <see cref="ArticleContentBytes"/>.</summary>
        public static readonly ReadOnlyMemory<byte> ArticleContent;

        /// <summary>Immutable complete multiline wire response including 220 status and terminating <c>.CRLF</c>.</summary>
        public static readonly ReadOnlyMemory<byte> WireResponse;

        /// <summary>Exact article content length in bytes (before framing).</summary>
        public static readonly int ArticleContentBytes;

        /// <summary>Exact wire response length in bytes (status + framed article + terminator).</summary>
        public static readonly int WireResponseBytes;

        static BenchIt()
        {
            var article = BuildArticleContent();
            ArticleContent = article;
            ArticleContentBytes = article.Length;

            var wire = BuildWireResponse(article);
            WireResponse = wire;
            WireResponseBytes = wire.Length;
        }

        /// <summary>Handles <c>BENCHIT</c> (internal; public access; non-pipelined via normal session loop).</summary>
        internal static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            // Intentionally skip NntpCommandExecution TX INFO logging — benchmark must not measure Serilog.
            return context.Response.WriteBytesAndFlushAsync(WireResponse, cancellationToken);
        }

        private static byte[] BuildArticleContent()
        {
            // Deterministic mandatory-style headers + blank line, then a fixed body pattern.
            // One body line begins with '.' so the static wire form exercises dot-stuffing.
            var headerText =
                "From: benchit@vectornntp.local\r\n" +
                "Newsgroups: vectornntp.bench\r\n" +
                "Subject: VectorNNTP BENCHIT transport payload\r\n" +
                "Date: Mon, 01 Jan 2024 00:00:00 +0000\r\n" +
                "Message-ID: " + MessageId + "\r\n" +
                "Path: vectornntp.bench\r\n" +
                "\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(headerText);
            if (headerBytes.Length >= TargetArticleContentBytes)
            {
                throw new InvalidOperationException("BENCHIT headers alone exceed the target article size.");
            }

            var article = new byte[TargetArticleContentBytes];
            headerBytes.CopyTo(article.AsSpan());

            // 76-octet body lines (common Usenet width) of a repeating digit pattern, CRLF-terminated.
            // First body line is ".DOTSTUFF-CHECK" so framing must emit "..DOTSTUFF-CHECK\r\n".
            var offset = headerBytes.Length;
            var firstLine = Encoding.ASCII.GetBytes(".DOTSTUFF-CHECK\r\n");
            if (offset + firstLine.Length > article.Length)
            {
                throw new InvalidOperationException("BENCHIT target size too small for required body line.");
            }

            firstLine.CopyTo(article.AsSpan(offset));
            offset += firstLine.Length;

            // Pattern line without leading '.': 74 payload chars + CRLF = 76 bytes.
            Span<byte> patternLine = stackalloc byte[76];
            for (var i = 0; i < 74; i++)
            {
                patternLine[i] = (byte)('0' + (i % 10));
            }

            patternLine[74] = (byte)'\r';
            patternLine[75] = (byte)'\n';

            while (offset + patternLine.Length <= article.Length)
            {
                patternLine.CopyTo(article.AsSpan(offset));
                offset += patternLine.Length;
            }

            // Fill any remainder with ASCII 'X' (no CR/LF) so content length is exact.
            article.AsSpan(offset).Fill((byte)'X');
            return article;
        }

        private static byte[] BuildWireResponse(ReadOnlySpan<byte> article)
        {
            var status = Encoding.ASCII.GetBytes($"220 0 {MessageId}\r\n");
            using var buffer = new MemoryStream(status.Length + article.Length + (article.Length / 64) + 16);
            buffer.Write(status);

            var lineStart = 0;
            for (var i = 0; i < article.Length; i++)
            {
                if (article[i] != (byte)'\n')
                {
                    continue;
                }

                // Line includes optional trailing CR before LF.
                var lineEnd = i + 1;
                var line = article[lineStart..lineEnd];
                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    buffer.WriteByte((byte)'.');
                }

                buffer.Write(line);
                lineStart = lineEnd;
            }

            if (lineStart < article.Length)
            {
                // Final unterminated fragment: emit as a line with CRLF (RFC multiline writer style).
                var fragment = article[lineStart..];
                if (fragment.Length > 0 && fragment[0] == (byte)'.')
                {
                    buffer.WriteByte((byte)'.');
                }

                buffer.Write(fragment);
                buffer.Write("\r\n"u8);
            }

            buffer.Write(".\r\n"u8);
            return buffer.ToArray();
        }
    }
}
