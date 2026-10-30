using System.Buffers;
using System.Net;
using System.Text;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Session.Commands.Posting
{
    /// <summary>Replaces server-owned POST headers and emits one destuffed article buffer.</summary>
    internal static class PostHeaderNormalizer
    {
        /// <summary>Server-controlled Path for locally POSTed articles.</summary>
        public const string PostedPath = ".POSTED";

        /// <summary>
        /// Builds the accepted article: client Path / injection metadata / Xref discarded,
        /// server-owned values written once, a valid client Date preserved, and a Date
        /// written from the injection time only when the client omitted Date.
        /// </summary>
        public static ReadOnlyMemory<byte> Normalize(
            ParsedPostArticle article,
            DateTimeOffset injectionUtc,
            string injectionIdentity,
            ConnectionClientIdentity clientIdentity,
            string mailComplaintsTo,
            IPostingTraceProtector traceProtector,
            string? authenticatedUsername = null)
        {
            ArgumentNullException.ThrowIfNull(article);
            ArgumentException.ThrowIfNullOrWhiteSpace(article.MessageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(injectionIdentity);
            ArgumentNullException.ThrowIfNull(clientIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(mailComplaintsTo);
            ArgumentNullException.ThrowIfNull(traceProtector);

            var writer = new ArrayBufferWriter<byte>(article.Payload.Length + 256);
            foreach (var header in article.Headers)
            {
                if (IsServerOwned(header.Name.Span))
                {
                    continue;
                }

                writer.Write(header.RawField.Span);
            }

            WriteServerOwnedHeaders(
                writer,
                article.MessageIdSynthesized,
                article.MessageId,
                article.DateSynthesized,
                injectionUtc,
                injectionIdentity,
                clientIdentity,
                mailComplaintsTo,
                traceProtector,
                authenticatedUsername);
            if (!article.Body.IsEmpty)
            {
                writer.Write(article.Body.Span);
            }

            return writer.WrittenMemory.ToArray();
        }

        /// <summary>Formats the transport peer for trusted diagnostics (never written as a header).</summary>
        public static string FormatPostingHost(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        }

        internal static bool IsServerOwned(ReadOnlySpan<byte> name) =>
            PostFieldSyntax.EqualsFolded(name, "PATH"u8)
            || PostFieldSyntax.EqualsFolded(name, "INJECTION-DATE"u8)
            || PostFieldSyntax.EqualsFolded(name, "INJECTION-INFO"u8)
            || PostFieldSyntax.EqualsFolded(name, "NNTP-POSTING-DATE"u8)
            || PostFieldSyntax.EqualsFolded(name, "NNTP-POSTING-HOST"u8)
            || PostFieldSyntax.EqualsFolded(name, "X-TRACE"u8)
            || PostFieldSyntax.EqualsFolded(name, "XREF"u8);

        /// <summary>
        /// Writes synthesized Message-ID and Date (when needed), Path, Injection-Date, Injection-Info,
        /// X-Trace, and the header/body separator into <paramref name="writer"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="writeDate"/> writes <see cref="PostRfcDate.Format"/> of
        /// <paramref name="injectionUtc"/>. A client Date already copied into the article is not rewritten.
        /// </remarks>
        internal static void WriteServerOwnedHeaders(
            IBufferWriter<byte> writer,
            bool writeMessageId,
            string messageId,
            bool writeDate,
            DateTimeOffset injectionUtc,
            string injectionIdentity,
            ConnectionClientIdentity clientIdentity,
            string mailComplaintsTo,
            IPostingTraceProtector traceProtector,
            string? authenticatedUsername = null)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(injectionIdentity);
            ArgumentNullException.ThrowIfNull(clientIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(mailComplaintsTo);
            ArgumentNullException.ThrowIfNull(traceProtector);

            if (writeMessageId)
            {
                WriteHeader(writer, "Message-ID: "u8, messageId);
            }

            if (writeDate)
            {
                WriteHeader(writer, "Date: "u8, PostRfcDate.Format(injectionUtc));
            }

            WriteHeader(writer, "Path: "u8, PostedPath);
            WriteHeader(writer, "Injection-Date: "u8, PostRfcDate.Format(injectionUtc));
            WriteHeader(writer, "Injection-Info: "u8, BuildInjectionInfo(injectionIdentity, messageId, mailComplaintsTo));
            WriteHeader(
                writer,
                "X-Trace: "u8,
                traceProtector.Protect(
                    new PostingTracePayload(
                        clientIdentity.ClientAddress,
                        clientIdentity.ClientPort,
                        injectionUtc,
                        Guid.NewGuid(),
                        authenticatedUsername)));
            writer.Write("\r\n"u8);
        }

        /// <summary>
        /// Writes a synthesized Message-ID and Date when required, then the header/body separator.
        /// Does not write Path, Injection-Date, Injection-Info, or X-Trace (RFC 5537 §3.5 step 7).
        /// </summary>
        internal static void WriteProtoArticleBoundary(
            IBufferWriter<byte> writer,
            bool writeMessageId,
            string messageId,
            bool writeDate,
            DateTimeOffset injectionUtc)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            if (writeMessageId)
            {
                WriteHeader(writer, "Message-ID: "u8, messageId);
            }

            if (writeDate)
            {
                WriteHeader(writer, "Date: "u8, PostRfcDate.Format(injectionUtc));
            }

            writer.Write("\r\n"u8);
        }

        private static void WriteHeader(IBufferWriter<byte> writer, ReadOnlySpan<byte> prefix, string value)
        {
            writer.Write(prefix);
            Encoding.ASCII.GetBytes(value, writer);
            writer.Write("\r\n"u8);
        }

        private static string BuildInjectionInfo(
            string identity,
            string messageId,
            string mailComplaintsTo) =>
            $"{identity}; logging-data=\"{messageId}\"; mail-complaints-to=\"{mailComplaintsTo}\"";
    }
}
