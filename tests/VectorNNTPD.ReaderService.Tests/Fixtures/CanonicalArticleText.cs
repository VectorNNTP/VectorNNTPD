using System.Net;
using System.Text;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Destuffed article text that Common <c>ArticleRecordFactory</c> accepts.
/// </summary>
internal static class CanonicalArticleText
{
    /// <summary>Date header value accepted by <c>NewsDateParser</c>.</summary>
    public const string Date = "Fri, 23 Aug 2024 07:30:10 +0000";

    /// <summary>Builds destuffed headers + body without an NNTP terminator.</summary>
    /// <param name="messageId">Message-ID value including angle brackets.</param>
    /// <param name="body">Destuffed body, typically CRLF-terminated.</param>
    /// <param name="newsgroups">Newsgroups header value.</param>
    /// <returns>Destuffed article text.</returns>
    public static string Destuffed(
        string messageId,
        string body = "body\r\n",
        string newsgroups = "alt.test",
        string subject = "ingress-test",
        string from = "user@example.test",
        string? references = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsgroups);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        var referencesHeader = string.IsNullOrEmpty(references)
            ? string.Empty
            : "References: " + references + "\r\n";
        return
            "Path: peer.example\r\n" +
            "Date: " + Date + "\r\n" +
            "Message-ID: " + messageId + "\r\n" +
            "Newsgroups: " + newsgroups + "\r\n" +
            "From: " + from + "\r\n" +
            "Subject: " + subject + "\r\n" +
            referencesHeader +
            "\r\n" +
            body;
    }

    /// <summary>NNTP stuffed wire for <see cref="Destuffed"/> without the terminator.</summary>
    public static string Stuffed(
        string messageId,
        string destuffedBody = "body\r\n",
        string newsgroups = "alt.test") =>
        Destuffed(messageId, destuffedBody, newsgroups)
            .Replace("\r\n.", "\r\n..", StringComparison.Ordinal);

    /// <summary>Builds a CanonicalV1 queue item for tests.</summary>
    public static InboundArticle CreateQueued(
        string messageId,
        InboundArticleProducer producer,
        string body = "body\r\n",
        string newsgroups = "alt.test",
        ReadOnlyMemory<byte> feed = default,
        string subject = "ingress-test",
        string from = "user@example.test",
        string? references = null)
    {
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            new NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(Destuffed(messageId, body, newsgroups, subject, from, references)));
        if (!created.IsAccepted)
        {
            throw new InvalidOperationException("Test article must be CanonicalV1.");
        }

        return ArticleRecordIngress.CreateQueued(
            messageId,
            created.Record,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            producer,
            feed);
    }
}
