using System.Buffers;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Session.Framing;

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Destuffs IHAVE/TAKETHIS wire into <see cref="Article"/> for benches and
    /// classifier tests.
    /// </summary>
    /// <remarks>
    /// Production IHAVE no longer uses this type. After receive, IHAVE destuffs
    /// once in <c>ArticleRecordIngress.TryCreateFromStuffedWire</c> and
    /// materializes CanonicalV1 through <see cref="ArticleRecordFactory"/>.
    /// Remaining IHAVE-specific behaviour lives in the IHAVE command (History,
    /// 335/235/435/436/437, non-blocking probe/admit). Common owns parse,
    /// Date/Path materialize, ArtId, ArtHash, and ArtType.
    /// </remarks>
    public static class IhaveArticleInterpreter
    {
        /// <summary>
        /// Destuffs stuffed IHAVE wire (no terminator) into <see cref="Article"/>.
        /// </summary>
        public static Article DestuffToArticle(ReadOnlySpan<byte> stuffedWire, int maxArticleBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            var headers = new ArrayBufferWriter<byte>(1024);
            var body = new ArrayBufferWriter<byte>(1024);
            var type = ArticleType.None;
            var exceeded = false;
            var inHeaders = true;
            var offset = 0;
            while (offset < stuffedWire.Length && !exceeded)
            {
                var remaining = stuffedWire[offset..];
                var crlf = remaining.IndexOf(NntpDelimiterSearch.Crlf);
                if (crlf < 0)
                {
                    break;
                }

                var line = remaining[..crlf];
                var destuffed = DestuffLine(line);
                if (inHeaders)
                {
                    if (destuffed.IsEmpty)
                    {
                        inHeaders = false;
                    }
                    else
                    {
                        NntpArticleDestuffer.AppendUnstuffedLine(headers, line, maxArticleBytes, ref exceeded);
                        ArticleTypeClassifier.ObserveLine(destuffed, inHeader: true, ref type);
                    }
                }
                else
                {
                    NntpArticleDestuffer.AppendUnstuffedLine(body, line, maxArticleBytes, ref exceeded);
                    ArticleTypeClassifier.ObserveLine(destuffed, inHeader: false, ref type);
                }

                offset += crlf + 2;
            }

            var headerBytes = headers.WrittenCount == 0
                ? ReadOnlyMemory<byte>.Empty
                : headers.WrittenMemory.ToArray();
            var bodyBytes = body.WrittenCount == 0
                ? ReadOnlyMemory<byte>.Empty
                : body.WrittenMemory.ToArray();
            return new Article(headerBytes, bodyBytes, type);
        }

        private static ReadOnlySpan<byte> DestuffLine(ReadOnlySpan<byte> stuffedLine) =>
            stuffedLine.Length > 0 && stuffedLine[0] == (byte)'.' ? stuffedLine[1..] : stuffedLine;
    }
}
