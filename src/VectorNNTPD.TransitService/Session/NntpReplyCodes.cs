namespace VectorNNTPD.TransitService.Session
{
    /// <summary>Common NNTP reply codes used by the session and command layer (RFC 3977 / 4643).</summary>
    public static class NntpReplyCodes
    {
        /// <summary>Help text follows (multiline).</summary>
        public const int HelpTextFollows = 100;

        /// <summary>Capability list follows (multiline).</summary>
        public const int CapabilityListFollows = 101;

        /// <summary>Server date stamp.</summary>
        public const int ServerDate = 111;

        /// <summary>Posting allowed (greeting / MODE READER).</summary>
        public const int PostingAllowed = 200;

        /// <summary>Posting prohibited (greeting / MODE READER).</summary>
        public const int PostingProhibited = 201;

        /// <summary>Connection closing (QUIT).</summary>
        public const int ConnectionClosing = 205;

        /// <summary>Compression layer activated (COMPRESS; RFC 8054).</summary>
        public const int CompressionActive = 206;

        /// <summary>Streaming permitted (MODE STREAM; RFC 4644 §2.3).</summary>
        public const int StreamingPermitted = 203;

        /// <summary>Article follows (ARTICLE; RFC 3977 §6.2.1).</summary>
        public const int ArticleFollows = 220;

        /// <summary>Headers follow (HEAD; RFC 3977 §6.2.2).</summary>
        public const int HeadFollows = 221;

        /// <summary>Body follows (BODY; RFC 3977 §6.2.3).</summary>
        public const int BodyFollows = 222;

        /// <summary>Article exists / selected (STAT; RFC 3977 §6.2.4).</summary>
        public const int ArticleExists = 223;

        /// <summary>Article received OK (POST second stage; RFC 3977 §6.3.1).</summary>
        public const int ArticleReceivedOk = 240;

        /// <summary>Send article to be posted (POST first stage; RFC 3977 §6.3.1).</summary>
        public const int PostSendArticle = 340;

        /// <summary>Posting not permitted (POST first stage; RFC 3977 §6.3.1).</summary>
        public const int PostingNotPermitted = 440;

        /// <summary>Posting failed (POST second stage; RFC 3977 §6.3.1).</summary>
        public const int PostingFailed = 441;

        /// <summary>Article transferred OK (IHAVE second stage; RFC 3977 §6.3.2).</summary>
        public const int IhaveTransferredOk = 235;

        /// <summary>Send article to be transferred (IHAVE first stage; RFC 3977 §6.3.2).</summary>
        public const int IhaveSendArticle = 335;

        /// <summary>Article not wanted (IHAVE first stage; RFC 3977 §6.3.2).</summary>
        public const int IhaveNotWanted = 435;

        /// <summary>Transfer not possible or failed; try again later (IHAVE; RFC 3977 §6.3.2).</summary>
        public const int IhaveTryLater = 436;

        /// <summary>Transfer rejected; do not retry (IHAVE second stage; RFC 3977 §6.3.2).</summary>
        public const int IhaveRejected = 437;

        /// <summary>Send article to be transferred (CHECK; RFC 4644 §2.4).</summary>
        public const int SendArticleToBeTransferred = 238;

        /// <summary>Article transferred OK (TAKETHIS; RFC 4644 §2.5).</summary>
        public const int ArticleTransferredOk = 239;

        /// <summary>Service temporarily unavailable (close connection; RFC 4644 §2.5).</summary>
        public const int ServiceTemporarilyUnavailable = 400;

        /// <summary>Transfer rejected; do not retry (TAKETHIS; RFC 4644 §2.5).</summary>
        public const int TransferRejected = 439;

        /// <summary>Authentication accepted (AUTHINFO).</summary>
        public const int AuthenticationAccepted = 281;

        /// <summary>Continue with TLS negotiation (STARTTLS).</summary>
        public const int ContinueWithTls = 382;

        /// <summary>Password required to complete AUTHINFO USER/PASS.</summary>
        public const int PasswordRequired = 381;

        /// <summary>Command failed due to temporary/internal fault.</summary>
        public const int CommandFailed = 403;

        /// <summary>Feature exists but is unavailable or not supported in this form (e.g. unsupported COMPRESS algorithm).</summary>
        public const int FeatureUnavailable = 503;

        /// <summary>Authentication required before the facility can be used.</summary>
        public const int AuthenticationRequired = 480;

        /// <summary>Authentication failed or rejected.</summary>
        public const int AuthenticationRejected = 481;

        /// <summary>Authentication commands issued out of sequence.</summary>
        public const int AuthenticationOutOfSequence = 482;

        /// <summary>Privacy/encryption required for the command.</summary>
        public const int PrivacyRequired = 483;

        /// <summary>Unknown or unimplemented command.</summary>
        public const int UnknownCommand = 500;

        /// <summary>Syntax error or unknown command variant.</summary>
        public const int SyntaxError = 501;

        /// <summary>Command not permitted in the current state / permanently unavailable.</summary>
        public const int CommandUnavailable = 502;
    }
}
