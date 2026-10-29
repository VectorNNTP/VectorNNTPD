namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Canonical NNTP verb or qualifier identity. Built-in dispatch uses this enum, not strings.
    /// </summary>
    public enum NntpVerb : byte
    {
        /// <summary>No verb (empty line).</summary>
        None = 0,

        /// <summary>Unrecognized verb or qualifier.</summary>
        Unknown,

        /// <summary>QUIT.</summary>
        Quit,

        /// <summary>HELP.</summary>
        Help,

        /// <summary>DATE.</summary>
        Date,

        /// <summary>CAPABILITIES.</summary>
        Capabilities,

        /// <summary>MODE.</summary>
        Mode,

        /// <summary>STARTTLS.</summary>
        StartTls,

        /// <summary>COMPRESS.</summary>
        Compress,

        /// <summary>LIST.</summary>
        List,

        /// <summary>LISTGROUP.</summary>
        ListGroup,

        /// <summary>HDR.</summary>
        Hdr,

        /// <summary>OVER.</summary>
        Over,

        /// <summary>GROUP.</summary>
        Group,

        /// <summary>ARTICLE.</summary>
        Article,

        /// <summary>HEAD.</summary>
        Head,

        /// <summary>BODY.</summary>
        Body,

        /// <summary>STAT.</summary>
        Stat,

        /// <summary>NEXT.</summary>
        Next,

        /// <summary>LAST.</summary>
        Last,

        /// <summary>POST.</summary>
        Post,

        /// <summary>CHECK.</summary>
        Check,

        /// <summary>IHAVE.</summary>
        Ihave,

        /// <summary>TAKETHIS.</summary>
        TakeThis,

        /// <summary>AUTHINFO.</summary>
        AuthInfo,

        /// <summary>Internal BENCHIT facility.</summary>
        BenchIt,

        /// <summary>VectorNNTP SPEEDTEST diagnostic extension.</summary>
        SpeedTest,

        /// <summary>MODE READER qualifier.</summary>
        Reader,

        /// <summary>MODE STREAM qualifier.</summary>
        Stream,

        /// <summary>AUTHINFO USER qualifier.</summary>
        User,

        /// <summary>AUTHINFO PASS qualifier.</summary>
        Pass,

        /// <summary>AUTHINFO SASL qualifier.</summary>
        Sasl,

        /// <summary>LIST ACTIVE qualifier.</summary>
        Active,

        /// <summary>LIST COUNTS qualifier.</summary>
        Counts,

        /// <summary>LIST HEADERS qualifier.</summary>
        Headers,

        /// <summary>LIST MOTD qualifier.</summary>
        Motd,

        /// <summary>LIST NEWSGROUPS qualifier.</summary>
        Newsgroups,

        /// <summary>LIST OVERVIEW.FMT qualifier.</summary>
        OverviewFmt,
    }
}
