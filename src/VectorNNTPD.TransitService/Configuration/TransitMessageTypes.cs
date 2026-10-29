namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Diablo-style article message-type flags (configuration/policy only).
    /// </summary>
    /// <remarks>
    /// Names follow Diablo <c>ArtTypeConv()</c>. <see cref="Binary"/> is the value for both
    /// <c>binary</c> and <c>binaries</c>. <see cref="All"/> is the union of every concrete flag.
    /// Bit values match Common <c>ArticleType</c>. Receive matching uses
    /// <c>ArticleTypeCapabilities</c> against <c>ArticleRecord.ArtType</c>. Send matching is not wired.
    /// </remarks>
    [Flags]
    public enum TransitMessageTypes
    {
        /// <summary>Diablo <c>none</c> — no selected types.</summary>
        None = 0,

        /// <summary>Diablo <c>default</c>. Distinct from <see cref="None"/>.</summary>
        Default = 1 << 0,

        /// <summary>Diablo <c>control</c>.</summary>
        Control = 1 << 1,

        /// <summary>Diablo <c>cancel</c>.</summary>
        Cancel = 1 << 2,

        /// <summary>Diablo <c>mime</c>.</summary>
        Mime = 1 << 3,

        /// <summary>Diablo <c>binary</c> / <c>binaries</c>.</summary>
        Binary = 1 << 4,

        /// <summary>Diablo <c>uuencode</c>.</summary>
        UuEncode = 1 << 5,

        /// <summary>Diablo <c>base64</c>.</summary>
        Base64 = 1 << 6,

        /// <summary>Diablo <c>yenc</c>.</summary>
        Yenc = 1 << 7,

        /// <summary>Diablo <c>bommanews</c>.</summary>
        BommaNews = 1 << 8,

        /// <summary>Diablo <c>unidata</c>.</summary>
        UniData = 1 << 9,

        /// <summary>Diablo <c>multipart</c>.</summary>
        Multipart = 1 << 10,

        /// <summary>Diablo <c>html</c>.</summary>
        Html = 1 << 11,

        /// <summary>Diablo <c>ps</c>.</summary>
        PostScript = 1 << 12,

        /// <summary>Diablo <c>binhex</c>.</summary>
        BinHex = 1 << 13,

        /// <summary>Diablo <c>partial</c>.</summary>
        Partial = 1 << 14,

        /// <summary>Diablo <c>pgp</c>.</summary>
        PgpMessage = 1 << 15,

        /// <summary>Union of every concrete message-type flag (Diablo <c>all</c>).</summary>
        All = Default | Control | Cancel | Mime | Binary | UuEncode | Base64 | Yenc | BommaNews
              | UniData | Multipart | Html | PostScript | BinHex | Partial | PgpMessage,
    }
}
