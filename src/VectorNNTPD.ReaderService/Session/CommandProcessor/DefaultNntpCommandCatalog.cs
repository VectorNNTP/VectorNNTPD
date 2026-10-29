namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>Built-in NNTP command inventory, access flags, and display names.</summary>
    /// <remarks>
    /// See <c>docs/commands.md</c> for the implementation checklist. Dispatch is enum/switch, not a dictionary.
    /// </remarks>
    public static class DefaultNntpCommandCatalog
    {
        /// <summary>
        /// Expected registry keys for the complete command inventory (stable order for tests).
        /// </summary>
        public static IReadOnlyList<string> InventoryKeys { get; } =
        [
            "ARTICLE",
            "AUTHINFO PASS",
            "AUTHINFO SASL",
            "AUTHINFO USER",
            "BODY",
            "CAPABILITIES",
            "CHECK",
            "COMPRESS",
            "DATE",
            "GROUP",
            "HDR",
            "HEAD",
            "HELP",
            "IHAVE",
            "LAST",
            "LIST",
            "LIST ACTIVE",
            "LIST COUNTS",
            "LIST HEADERS",
            "LIST MOTD",
            "LIST NEWSGROUPS",
            "LIST OVERVIEW.FMT",
            "LISTGROUP",
            "MODE READER",
            "MODE STREAM",
            "NEXT",
            "OVER",
            "POST",
            "QUIT",
            "SPEEDTEST",
            "STARTTLS",
            "STAT",
            "TAKETHIS",
        ];

        /// <summary>Registered keys including the internal BENCHIT facility.</summary>
        public static IReadOnlyList<string> GetRegisteredKeys()
        {
            var keys = new List<string>(InventoryKeys.Count + 1);
            keys.AddRange(InventoryKeys);
            keys.Add("BENCHIT");
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>Returns access flags for a syntactically valid built-in command.</summary>
        public static NntpCommandAccess GetAccess(NntpVerb verb, NntpVerb qualifier)
        {
            var readerAccess = NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader;
            return verb switch
            {
                NntpVerb.Capabilities or NntpVerb.Help or NntpVerb.Date or NntpVerb.Quit
                    or NntpVerb.StartTls or NntpVerb.Compress or NntpVerb.BenchIt
                    or NntpVerb.AuthInfo => NntpCommandAccess.Public,
                NntpVerb.Mode when qualifier == NntpVerb.Reader => NntpCommandAccess.Public,
                NntpVerb.Mode when qualifier == NntpVerb.Stream => NntpCommandAccess.RequiresStreaming,
                NntpVerb.Check or NntpVerb.TakeThis or NntpVerb.Ihave or NntpVerb.SpeedTest
                    => NntpCommandAccess.RequiresTransit,
                NntpVerb.Post => NntpCommandAccess.RequiresAuthentication
                    | NntpCommandAccess.RequiresReader,
                NntpVerb.List or NntpVerb.ListGroup or NntpVerb.Group
                    or NntpVerb.Article or NntpVerb.Head or NntpVerb.Body
                    or NntpVerb.Stat or NntpVerb.Last or NntpVerb.Next or NntpVerb.Over
                    or NntpVerb.Hdr => readerAccess,
                _ => NntpCommandAccess.Public,
            };
        }

        /// <summary>Returns the static uppercase display name used by TX completion logs.</summary>
        public static string DisplayName(NntpVerb verb, NntpVerb qualifier)
        {
            return (verb, qualifier) switch
            {
                (NntpVerb.Mode, NntpVerb.Reader) => "MODE READER",
                (NntpVerb.Mode, NntpVerb.Stream) => "MODE STREAM",
                (NntpVerb.AuthInfo, NntpVerb.User) => "AUTHINFO USER",
                (NntpVerb.AuthInfo, NntpVerb.Pass) => "AUTHINFO PASS",
                (NntpVerb.AuthInfo, NntpVerb.Sasl) => "AUTHINFO SASL",
                (NntpVerb.List, NntpVerb.Active) => "LIST ACTIVE",
                (NntpVerb.List, NntpVerb.Counts) => "LIST COUNTS",
                (NntpVerb.List, NntpVerb.Headers) => "LIST HEADERS",
                (NntpVerb.List, NntpVerb.Motd) => "LIST MOTD",
                (NntpVerb.List, NntpVerb.Newsgroups) => "LIST NEWSGROUPS",
                (NntpVerb.List, NntpVerb.OverviewFmt) => "LIST OVERVIEW.FMT",
                (NntpVerb.Article, _) => "ARTICLE",
                (NntpVerb.AuthInfo, _) => "AUTHINFO",
                (NntpVerb.Body, _) => "BODY",
                (NntpVerb.Capabilities, _) => "CAPABILITIES",
                (NntpVerb.Check, _) => "CHECK",
                (NntpVerb.Compress, _) => "COMPRESS",
                (NntpVerb.Date, _) => "DATE",
                (NntpVerb.Group, _) => "GROUP",
                (NntpVerb.Hdr, _) => "HDR",
                (NntpVerb.Head, _) => "HEAD",
                (NntpVerb.Help, _) => "HELP",
                (NntpVerb.Ihave, _) => "IHAVE",
                (NntpVerb.Last, _) => "LAST",
                (NntpVerb.List, _) => "LIST",
                (NntpVerb.ListGroup, _) => "LISTGROUP",
                (NntpVerb.Mode, _) => "MODE",
                (NntpVerb.Next, _) => "NEXT",
                (NntpVerb.Over, _) => "OVER",
                (NntpVerb.Post, _) => "POST",
                (NntpVerb.Quit, _) => "QUIT",
                (NntpVerb.SpeedTest, _) => "SPEEDTEST",
                (NntpVerb.StartTls, _) => "STARTTLS",
                (NntpVerb.Stat, _) => "STAT",
                (NntpVerb.TakeThis, _) => "TAKETHIS",
                (NntpVerb.BenchIt, _) => "BENCHIT",
                _ => "INVALID",
            };
        }
    }
}
