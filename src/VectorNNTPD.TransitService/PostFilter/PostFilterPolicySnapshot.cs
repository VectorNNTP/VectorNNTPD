using System.Net;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.PostFilter.Quota;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Immutable PostFilter policy observed for one POST evaluation.</summary>
    internal sealed class PostFilterPolicySnapshot
    {
        /// <summary>Initializes a compiled snapshot.</summary>
        public PostFilterPolicySnapshot(
            PostFilterGateState gate,
            IReadOnlySet<string> deniedAccounts,
            IReadOnlyList<IPNetwork> deniedCidrs,
            IReadOnlySet<string> allowlistedAccounts,
            IReadOnlyList<IPNetwork> allowlistedCidrs,
            ArticleType rejectArtTypes,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            bool spamAssassinEnabled,
            PostFilterSpamOnFailure? spamAssassinOnFailure,
            int spamAssassinMaxArticleSize,
            ArticleType spamAssassinExcludeArtTypes,
            IReadOnlyList<string> spamAssassinHosts,
            int spamAssassinPort,
            string spamAssassinProtocolVersion,
            int spamAssassinMaxConnections,
            PostFilterSpamAssassinHostSelection spamAssassinHostSelection,
            TimeSpan spamAssassinConnectTimeout,
            TimeSpan spamAssassinOperationTimeout,
            long reservationTtlMs,
            long revision = 0)
        {
            Gate = gate;
            DeniedAccounts = deniedAccounts;
            DeniedCidrs = deniedCidrs;
            AllowlistedAccounts = allowlistedAccounts;
            AllowlistedCidrs = allowlistedCidrs;
            RejectArtTypes = rejectArtTypes;
            Windows = windows;
            Ceilings = ceilings;
            SpamAssassinEnabled = spamAssassinEnabled;
            SpamAssassinOnFailure = spamAssassinOnFailure;
            SpamAssassinMaxArticleSize = spamAssassinMaxArticleSize;
            SpamAssassinExcludeArtTypes = spamAssassinExcludeArtTypes;
            SpamAssassinHosts = spamAssassinHosts;
            SpamAssassinPort = spamAssassinPort;
            SpamAssassinProtocolVersion = spamAssassinProtocolVersion;
            SpamAssassinMaxConnections = spamAssassinMaxConnections;
            SpamAssassinHostSelection = spamAssassinHostSelection;
            SpamAssassinConnectTimeout = spamAssassinConnectTimeout;
            SpamAssassinOperationTimeout = spamAssassinOperationTimeout;
            ReservationTtlMs = reservationTtlMs;
            Revision = revision;
        }

        /// <summary>Gets the operational gate.</summary>
        public PostFilterGateState Gate { get; }

        /// <summary>Gets denied account MD5 identifiers (ordinal lowercase hex).</summary>
        public IReadOnlySet<string> DeniedAccounts { get; }

        /// <summary>Gets denied client networks.</summary>
        public IReadOnlyList<IPNetwork> DeniedCidrs { get; }

        /// <summary>Gets allowlisted account MD5 identifiers (ordinal lowercase hex).</summary>
        public IReadOnlySet<string> AllowlistedAccounts { get; }

        /// <summary>Gets allowlisted client networks.</summary>
        public IReadOnlyList<IPNetwork> AllowlistedCidrs { get; }

        /// <summary>Gets the ArtType reject mask. <see cref="ArticleType.None"/> disables the stage.</summary>
        public ArticleType RejectArtTypes { get; }

        /// <summary>Gets quota windows.</summary>
        public PostFilterQuotaWindows Windows { get; }

        /// <summary>Gets quota ceilings.</summary>
        public PostFilterQuotaCeilings Ceilings { get; }

        /// <summary>Gets whether SpamAssassin CHECK is enabled.</summary>
        public bool SpamAssassinEnabled { get; }

        /// <summary>Gets scanner-fault behaviour when SpamAssassin is enabled.</summary>
        public PostFilterSpamOnFailure? SpamAssassinOnFailure { get; }

        /// <summary>
        /// Gets the exclusive SA size gate. Articles with
        /// <c>ArtSize &gt;= SpamAssassinMaxArticleSize</c> skip CHECK.
        /// <c>0</c> disables the size gate.
        /// </summary>
        public int SpamAssassinMaxArticleSize { get; }

        /// <summary>Gets ArtType bits excluded from CHECK.</summary>
        public ArticleType SpamAssassinExcludeArtTypes { get; }

        /// <summary>Gets compiled SPAMD hosts for this evaluation.</summary>
        public IReadOnlyList<string> SpamAssassinHosts { get; }

        /// <summary>Gets the compiled SPAMD port.</summary>
        public int SpamAssassinPort { get; }

        /// <summary>Gets the compiled SPAMC protocol version.</summary>
        public string SpamAssassinProtocolVersion { get; }

        /// <summary>Gets the compiled persistent-connection pool size.</summary>
        public int SpamAssassinMaxConnections { get; }

        /// <summary>Gets the compiled host-selection strategy.</summary>
        public PostFilterSpamAssassinHostSelection SpamAssassinHostSelection { get; }

        /// <summary>Gets the compiled SPAMD connect timeout.</summary>
        public TimeSpan SpamAssassinConnectTimeout { get; }

        /// <summary>Gets the compiled SPAMD CHECK wall-clock budget.</summary>
        public TimeSpan SpamAssassinOperationTimeout { get; }

        /// <summary>
        /// Gets the reservation hold compiled for this snapshot.
        /// Floor is 10s; when SpamAssassin is enabled this covers
        /// <see cref="SpamAssassinOperationTimeout"/> plus hold skew.
        /// </summary>
        public long ReservationTtlMs { get; }

        /// <summary>Gets the NntpDB policy revision compiled into this snapshot.</summary>
        public long Revision { get; }

        /// <summary>Gets the CHECK target captured with this snapshot.</summary>
        public PostFilterSpamAssassinTarget SpamAssassinTarget =>
            new(
                SpamAssassinHosts,
                SpamAssassinPort,
                SpamAssassinConnectTimeout,
                SpamAssassinOperationTimeout,
                SpamAssassinProtocolVersion,
                SpamAssassinMaxConnections,
                SpamAssassinHostSelection);

        /// <summary>Disabled gate, no lists, no type policy, no SA, all quota dimensions off.</summary>
        public static PostFilterPolicySnapshot Disabled { get; } = new(
            PostFilterGateState.Disabled,
            new HashSet<string>(StringComparer.Ordinal),
            [],
            new HashSet<string>(StringComparer.Ordinal),
            [],
            ArticleType.None,
            new PostFilterQuotaWindows(86_400_000, 600_000),
            new PostFilterQuotaCeilings(0, 0, 0, 0, 0, 0),
            spamAssassinEnabled: false,
            spamAssassinOnFailure: null,
            spamAssassinMaxArticleSize: 0,
            spamAssassinExcludeArtTypes: ArticleType.None,
            spamAssassinHosts: [],
            spamAssassinPort: 783,
            spamAssassinProtocolVersion: PostFilterSpamAssassinOptions.DefaultProtocolVersion,
            spamAssassinMaxConnections: PostFilterSpamAssassinOptions.DefaultMaxConnections,
            spamAssassinHostSelection: PostFilterSpamAssassinHostSelection.RoundRobin,
            spamAssassinConnectTimeout: TimeSpan.FromSeconds(5),
            spamAssassinOperationTimeout: TimeSpan.FromSeconds(30),
            reservationTtlMs: (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds);
    }
}
