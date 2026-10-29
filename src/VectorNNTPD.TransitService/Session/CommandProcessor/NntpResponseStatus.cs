namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Immortal semantic first/status lines matching <see cref="NntpResponses"/> wire text
    /// (without CRLF). Authored independently of the byte buffers; not decoded from them.
    /// </summary>
    internal static class NntpResponseStatus
    {
        internal const string HelpTextFollows = "100 Help text follows";
        internal const string CapabilityListFollows = "101 Capability list:";
        internal const string ListOfNewsgroupsFollows = "215 list of newsgroups follows";
        internal const string ListOverviewFmtFollows = "215 Order of fields in overview database.";
        internal const string ListHeadersFollows = "215 headers and metadata items supported:";
        internal const string NoSuchNewsgroup = "411 No such newsgroup";
        internal const string NoNewsgroupSelected = "412 No newsgroup selected";
        internal const string CurrentArticleNumberInvalid = "420 Current article number is invalid";
        internal const string NoArticleWithNumber = "423 No article with that number";
        internal const string NoArticleWithMessageId = "430 No article with that message-id";
        internal const string GroupSelected = "211";
        internal const string ReaderModePostingPermitted = "200 Reader mode, posting permitted";
        internal const string ReaderModePostingProhibited = "201 Reader mode, posting prohibited";
        internal const string StreamingPermitted = "203 Streaming permitted";
        internal const string ConnectionClosing = "205 Connection closing";
        internal const string CompressionActive = "206 Compression active";
        internal const string IhaveTransferredOk = "235 Article transferred OK";
        internal const string ArticleReceivedOk = "240 Article received OK";
        internal const string IhaveSendArticle = "335 Send article to be transferred";
        internal const string PostSendArticle = "340 Input article; end with <CR-LF>.<CR-LF>";
        internal const string PostingProhibited = "440 Posting not permitted";
        internal const string PostingFailed = "441 Posting failed";
        internal const string AuthenticationAccepted = "281 Authentication accepted";
        internal const string PasswordRequired = "381 Password required";
        internal const string ContinueWithTls = "382 Continue with TLS negotiation";
        internal const string ServiceTemporarilyUnavailable = "400 Service temporarily unavailable";
        internal const string SpeedTestBusy = "400 SPEEDTEST BUSY";
        internal const string CommandFailed = "403 Command failed";
        internal const string UnableToActivateCompression = "403 Unable to activate compression";
        internal const string IhaveNotWanted = "435 Article not wanted";
        internal const string IhaveTryLater = "436 Transfer not possible; try again later";
        internal const string IhaveTransferFailed = "436 Transfer failed; try again later";
        internal const string IhaveRejected = "437 Transfer rejected; do not retry";
        internal const string AuthenticationRequired = "480 Authentication required";
        internal const string AuthenticationFailed = "481 Authentication failed";
        internal const string AuthenticationCancelled = "481 Authentication cancelled";
        internal const string TooManySessions = "481 Too many sessions";
        internal const string TooManySourceAddresses = "481 Too many source addresses";
        internal const string AuthenticationOutOfSequence = "482 Authentication commands issued out of sequence";
        internal const string PrivacyRequired = "483 Encryption or stronger authentication required";
        internal const string UnknownCommand = "500 Unknown command";
        internal const string CommandNotImplemented = "500 Command not implemented";
        internal const string SyntaxError = "501 Syntax error";
        internal const string UnknownCommandVariant = "501 Unknown command variant";
        internal const string AuthinfoUserRequiresUsername = "501 AUTHINFO USER requires a username";
        internal const string AuthinfoPassRequiresPassword = "501 AUTHINFO PASS requires a password";
        internal const string CompressRequiresAlgorithm = "501 COMPRESS requires a single algorithm argument";
        internal const string CompressAlgorithmSyntaxInvalid = "501 Syntactically incorrect compression algorithm";
        internal const string AuthinfoSaslNotImplemented = "501 AUTHINFO SASL not implemented";
        internal const string SaslMechanismRequired = "501 SASL mechanism required";
        internal const string SaslInvalidBase64 = "504 Base64 encoding error";
        internal const string SaslLoginUsernameChallenge = "383 VXNlcm5hbWU6";
        internal const string SaslLoginPasswordChallenge = "383 UGFzc3dvcmQ6";
        internal const string SaslEmptyChallenge = "383 =";
        internal const string SaslChallenge = "383 SASL challenge";
        internal const string SaslSuccessWithData = "283 Authentication accepted";
        internal const string SaslProtocolError = "482 SASL protocol error";
        internal const string SaslMechanismNotSupported = "503 Mechanism not supported";
        internal const string SaslContinuationNotSupported = "503 SASL continuation not supported";
        internal const string NoSaslExchangeInProgress = "503 No SASL exchange in progress";
        internal const string TemporaryAuthenticationFailure = "503 Temporary authentication failure";
        internal const string SaslScramUnavailable = "503 SCRAM not available";
        internal const string SaslCramUnavailable = "503 CRAM-MD5 not available";
        internal const string AlreadyAuthenticated = "502 Already authenticated";
        internal const string CommandUnavailableAfterAuthentication = "502 Command unavailable after authentication";
        internal const string CommandUnavailableAfterCompression = "502 Command unavailable after compression";
        internal const string DeflateAlreadyActive = "502 DEFLATE compression already active";
        internal const string CompressionAlreadyActive = "502 Compression already active";
        internal const string TlsAlreadyActive = "502 TLS already active";
        internal const string TlsProviderUnavailable = "502 TLS provider unavailable";
        internal const string PostingNotPermitted = "502 Posting not permitted";
        internal const string PermissionDenied = "502 Permission denied";
        internal const string CommandNotAvailable = "502 Command not available on this server";
        internal const string StreamingNotPermitted = "502 Streaming not permitted";
        internal const string NotInReaderMode = "502 Not in reader mode";
        internal const string NotInStreamMode = "502 Not in stream mode";
        internal const string SpeedTestUnknownPeer = "502 UNKNOWN SPEEDTEST PEER";
        internal const string SpeedTestPeerMismatch = "502 SPEEDTEST PEER MISMATCH";
        internal const string CompressionAlgorithmNotSupported = "503 Compression algorithm not supported";
        internal const string SpeedTestNotSupported = "503 SPEEDTEST NOT SUPPORTED";
        internal const string ListDataItemNotStored = "503 Data item not stored";
    }
}
