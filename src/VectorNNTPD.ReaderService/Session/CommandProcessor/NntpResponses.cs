using System.Text;

using VectorNNTPD.ReaderService.Session.Commands;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Immortal pre-encoded NNTP wire responses. Each value is ASCII, CRLF-terminated, and
    /// encoded once at type initialization. The TX pump may read the same backing array
    /// repeatedly; callers must not mutate it.
    /// </summary>
    /// <remarks>
    /// Invariant replies are complete wire buffers. CHECK/TAKETHIS/DATE use the prefix/suffix
    /// fields with <see cref="NntpResponseCompose"/> (one owned copy of dynamic octets).
    /// </remarks>
    internal static class NntpResponses
    {
        internal static readonly ReadOnlyMemory<byte> GreetingPostingPermitted =
            Line("200 VectorNNTPD.ReaderService ready, posting permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> GreetingPostingProhibited =
            Line("201 VectorNNTPD.ReaderService ready, posting prohibited\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ReaderModePostingPermitted =
            Line("200 Reader mode, posting permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ReaderModePostingProhibited =
            Line("201 Reader mode, posting prohibited\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> StreamingPermitted =
            Line("203 Streaming permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ConnectionClosing =
            Line("205 Connection closing\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CompressionActive =
            Line("206 Compression active\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthenticationAccepted =
            Line("281 Authentication accepted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PasswordRequired =
            Line("381 Password required\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ContinueWithTls =
            Line("382 Continue with TLS negotiation\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ServiceTemporarilyUnavailable =
            Line("400 Service temporarily unavailable\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CommandFailed =
            Line("403 Command failed\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> UnableToActivateCompression =
            Line("403 Unable to activate compression\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthenticationRequired =
            Line("480 Authentication required\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthenticationFailed =
            Line("481 Authentication failed\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthenticationCancelled =
            Line("481 Authentication cancelled\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> TooManySessions =
            Line("481 Too many sessions\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> TooManySourceAddresses =
            Line("481 Too many source addresses\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthenticationOutOfSequence =
            Line("482 Authentication commands issued out of sequence\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PrivacyRequired =
            Line("483 Encryption or stronger authentication required\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> UnknownCommand =
            Line("500 Unknown command\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CommandNotImplemented =
            Line("500 Command not implemented\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SyntaxError =
            Line("501 Syntax error\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> UnknownCommandVariant =
            Line("501 Unknown command variant\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthinfoUserRequiresUsername =
            Line("501 AUTHINFO USER requires a username\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthinfoPassRequiresPassword =
            Line("501 AUTHINFO PASS requires a password\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CompressRequiresAlgorithm =
            Line("501 COMPRESS requires a single algorithm argument\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CompressAlgorithmSyntaxInvalid =
            Line("501 Syntactically incorrect compression algorithm\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AuthinfoSaslNotImplemented =
            Line("501 AUTHINFO SASL not implemented\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslMechanismRequired =
            Line("501 SASL mechanism required\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslInvalidBase64 =
            Line("504 Base64 encoding error\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslLoginUsernameChallenge =
            Line("383 VXNlcm5hbWU6\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslLoginPasswordChallenge =
            Line("383 UGFzc3dvcmQ6\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslEmptyChallenge =
            Line("383 =\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslProtocolError =
            Line("482 SASL protocol error\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslMechanismNotSupported =
            Line("503 Mechanism not supported\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslContinuationNotSupported =
            Line("503 SASL continuation not supported\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NoSaslExchangeInProgress =
            Line("503 No SASL exchange in progress\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> TemporaryAuthenticationFailure =
            Line("503 Temporary authentication failure\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslScramUnavailable =
            Line("503 SCRAM not available\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SaslCramUnavailable =
            Line("503 CRAM-MD5 not available\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> AlreadyAuthenticated =
            Line("502 Already authenticated\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CommandUnavailableAfterAuthentication =
            Line("502 Command unavailable after authentication\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CommandUnavailableAfterCompression =
            Line("502 Command unavailable after compression\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> DeflateAlreadyActive =
            Line("502 DEFLATE compression already active\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CompressionAlreadyActive =
            Line("502 Compression already active\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> TlsAlreadyActive =
            Line("502 TLS already active\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> TlsProviderUnavailable =
            Line("502 TLS provider unavailable\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PostingNotPermitted =
            Line("502 Posting not permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PermissionDenied =
            Line("502 Permission denied\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CommandNotAvailable =
            Line("502 Command not available on this server\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> StreamingNotPermitted =
            Line("502 Streaming not permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NotInReaderMode =
            Line("502 Not in reader mode\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NotInStreamMode =
            Line("502 Not in stream mode\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CompressionAlgorithmNotSupported =
            Line("503 Compression algorithm not supported\r\n"u8);

        /// <summary>RFC 3977 §7.6.1.2: recognized LIST keyword whose information is not maintained.</summary>
        internal static readonly ReadOnlyMemory<byte> ListDataItemNotStored =
            Line("503 Data item not stored\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> HelpTextFollows =
            Line("100 Help text follows\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityListFollows =
            Line("101 Capability list:\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityVersion2 =
            Multiline("VERSION 2\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityImplementation =
            Multiline("IMPLEMENTATION VectorNNTPD.ReaderService\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityReader =
            Multiline("READER\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityModeReader =
            Multiline("MODE-READER\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityPost =
            Multiline("POST\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityAuthinfo =
            Multiline("AUTHINFO\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityAuthinfoUser =
            Multiline("AUTHINFO USER\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityAuthinfoUserSasl =
            Multiline("AUTHINFO USER SASL\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilitySasl =
            Multiline("SASL PLAIN LOGIN SCRAM-SHA-256 CRAM-MD5\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityStartTls =
            Multiline("STARTTLS\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityCompressDeflate =
            Multiline("COMPRESS DEFLATE\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityStreaming =
            Multiline("STREAMING\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilitySpeedTest =
            Multiline("SPEEDTEST\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityList =
            Multiline("LIST ACTIVE COUNTS HEADERS NEWSGROUPS OVERVIEW.FMT\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ListOfNewsgroupsFollows =
            Line("215 list of newsgroups follows\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ListOverviewFmtFollows =
            Line("215 Order of fields in overview database.\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ListHeadersFollows =
            Line("215 headers and metadata items supported:\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NoSuchNewsgroup =
            Line("411 No such newsgroup\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NoNewsgroupSelected =
            Line("412 No newsgroup selected\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CurrentArticleNumberInvalid =
            Line("420 Current article number is invalid\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NoArticleWithNumber =
            Line("423 No article with that number\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> NoArticleWithMessageId =
            Line("430 No article with that message-id\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestUnknownPeer =
            Line("502 UNKNOWN SPEEDTEST PEER\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestPeerMismatch =
            Line("502 SPEEDTEST PEER MISMATCH\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestBusy =
            Line("400 SPEEDTEST BUSY\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestNotSupported =
            Line("503 SPEEDTEST NOT SUPPORTED\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestReadyPrefix = Prefix("290 SPEEDTEST "u8);

        internal static readonly ReadOnlyMemory<byte> SpeedTestReadySuffix = Suffix(" TX\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> MultilineTerminator =
            Line(".\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CheckPrefix = Prefix("238 "u8);

        internal static readonly ReadOnlyMemory<byte> CheckSuffix =
            Suffix(" send article to be transferred\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CheckNotWantedPrefix = Prefix("438 "u8);

        internal static readonly ReadOnlyMemory<byte> CheckNotWantedSuffix = Suffix("\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CheckTryLaterPrefix = Prefix("431 "u8);

        internal static readonly ReadOnlyMemory<byte> CheckTryLaterSuffix = Suffix("\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveSendArticle =
            Line("335 Send article to be transferred\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ArticleReceivedOk =
            Line("240 Article received OK\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PostSendArticle =
            Line("340 Input article; end with <CR-LF>.<CR-LF>\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PostingProhibited =
            Line("440 Posting not permitted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> PostingFailed =
            Line("441 Posting failed\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveTransferredOk =
            Line("235 Article transferred OK\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveNotWanted =
            Line("435 Article not wanted\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveTryLater =
            Line("436 Transfer not possible; try again later\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveTransferFailed =
            Line("436 Transfer failed; try again later\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> IhaveRejected =
            Line("437 Transfer rejected; do not retry\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> CapabilityIhave =
            Multiline("IHAVE\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> ArticleTransferredOkPrefix = Prefix("239 "u8);

        internal static readonly ReadOnlyMemory<byte> TransferRejectedPrefix = Prefix("439 "u8);

        internal static readonly ReadOnlyMemory<byte> Crlf = Suffix("\r\n"u8);

        internal static readonly ReadOnlyMemory<byte> DatePrefix = Prefix("111 "u8);

        internal static readonly ReadOnlyMemory<byte>[] HelpBodyLines = EncodeHelpBody();

        /// <summary>Complete HELP multiline wire (status + body + terminator). Immortal.</summary>
        internal static readonly ReadOnlyMemory<byte> HelpComplete = EncodeHelpComplete();

        /// <summary>Complete LIST OVERVIEW.FMT multiline wire. Immortal; RFC 3977 §8.4 compatibility form.</summary>
        internal static readonly ReadOnlyMemory<byte> ListOverviewFmtComplete = EncodeListOverviewFmtComplete();

        /// <summary>Complete LIST HEADERS multiline wire. Immortal; same for MSGID and RANGE.</summary>
        internal static readonly ReadOnlyMemory<byte> ListHeadersComplete = EncodeListHeadersComplete();

        /// <summary>
        /// Copies <paramref name="asciiIncludingCrlf"/> onto an immortal heap array once.
        /// The UTF-8 literal span must not be retained.
        /// </summary>
        private static ReadOnlyMemory<byte> Line(ReadOnlySpan<byte> asciiIncludingCrlf)
        {
            if (asciiIncludingCrlf.Length < 2
                || asciiIncludingCrlf[^2] != (byte)'\r'
                || asciiIncludingCrlf[^1] != (byte)'\n')
            {
                throw new ArgumentException("Static NNTP responses must end with a single CRLF.");
            }

            var copy = asciiIncludingCrlf.ToArray();
            if (CrlfCount(copy) != 1)
            {
                throw new ArgumentException("Static single-line NNTP responses must contain exactly one CRLF.");
            }

            return copy;
        }

        private static ReadOnlyMemory<byte> Multiline(ReadOnlySpan<byte> asciiIncludingCrlf) =>
            Line(asciiIncludingCrlf);

        private static ReadOnlyMemory<byte>[] EncodeHelpBody()
        {
            var source = Help.BodyLines;
            var encoded = new ReadOnlyMemory<byte>[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                encoded[i] = Line(Encoding.ASCII.GetBytes(source[i] + "\r\n"));
            }

            return encoded;
        }

        private static ReadOnlyMemory<byte> EncodeHelpComplete()
        {
            var parts = new ReadOnlyMemory<byte>[HelpBodyLines.Length + 2];
            parts[0] = HelpTextFollows;
            HelpBodyLines.CopyTo(parts, 1);
            parts[^1] = MultilineTerminator;
            return NntpResponseCompose.Concatenate(parts);
        }

        private static ReadOnlyMemory<byte> EncodeListOverviewFmtComplete() =>
            EncodeStaticMultiline(
                ListOverviewFmtFollows,
                Multiline("Subject:\r\n"u8),
                Multiline("From:\r\n"u8),
                Multiline("Date:\r\n"u8),
                Multiline("Message-ID:\r\n"u8),
                Multiline("References:\r\n"u8),
                Multiline("Bytes:\r\n"u8),
                Multiline("Lines:\r\n"u8));

        private static ReadOnlyMemory<byte> EncodeListHeadersComplete() =>
            EncodeStaticMultiline(
                ListHeadersFollows,
                Multiline("Subject\r\n"u8),
                Multiline("From\r\n"u8),
                Multiline("Date\r\n"u8),
                Multiline("Message-ID\r\n"u8),
                Multiline("References\r\n"u8),
                Multiline(":bytes\r\n"u8),
                Multiline(":lines\r\n"u8));

        private static ReadOnlyMemory<byte> EncodeStaticMultiline(
            ReadOnlyMemory<byte> status,
            params ReadOnlyMemory<byte>[] bodyLines)
        {
            var parts = new ReadOnlyMemory<byte>[bodyLines.Length + 2];
            parts[0] = status;
            bodyLines.CopyTo(parts, 1);
            parts[^1] = MultilineTerminator;
            return NntpResponseCompose.Concatenate(parts);
        }

        private static ReadOnlyMemory<byte> Prefix(ReadOnlySpan<byte> ascii) => ascii.ToArray();

        private static ReadOnlyMemory<byte> Suffix(ReadOnlySpan<byte> ascii) => ascii.ToArray();

        private static int CrlfCount(ReadOnlySpan<byte> bytes)
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
    }
}
