using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.Networking.Proxy;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>NNTPD POST-only accept filter. Does not own CreateQueued / TryAdmit / History.</summary>
    public interface IPostFilter
    {
        /// <summary>
        /// Evaluates one CanonicalV1 article. On Accept, <see cref="PostFilterResult.Lease"/>
        /// is set when a reservation was taken.
        /// </summary>
        ValueTask<PostFilterResult> EvaluateAsync(
            PostFilterRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Inputs already available at the POST insertion point.</summary>
    public readonly struct PostFilterRequest
    {
        /// <summary>Initializes a request. Does not copy <paramref name="article"/>.</summary>
        public PostFilterRequest(
            ArticleRecord article,
            string? accountName,
            ConnectionClientIdentity clientIdentity,
            NntpAccountPolicy? accountPolicy,
            DateTimeOffset injectionUtc,
            string[] newsgroups,
            DateTimeOffset now,
            string? serverFqdn = null)
        {
            Article = article;
            AccountName = accountName;
            ClientIdentity = clientIdentity;
            AccountPolicy = accountPolicy;
            InjectionUtc = injectionUtc;
            Newsgroups = newsgroups;
            Now = now;
            ServerFqdn = string.IsNullOrWhiteSpace(serverFqdn) ? "localhost" : serverFqdn.Trim();
        }

        /// <summary>Gets the CanonicalV1 record (read-only).</summary>
        public ArticleRecord Article { get; }

        /// <summary>Gets the authenticated account, when present.</summary>
        public string? AccountName { get; }

        /// <summary>Gets the PROXY-aware client identity.</summary>
        public ConnectionClientIdentity ClientIdentity { get; }

        /// <summary>Gets the AUTH-time account policy, when present.</summary>
        public NntpAccountPolicy? AccountPolicy { get; }

        /// <summary>Gets injection time from StreamingPost.</summary>
        public DateTimeOffset InjectionUtc { get; }

        /// <summary>Gets newsgroup names already parsed by StreamingPost.</summary>
        public string[] Newsgroups { get; }

        /// <summary>Gets the evaluation clock.</summary>
        public DateTimeOffset Now { get; }

        /// <summary>Gets the server FQDN used for synthetic SPAMD scan headers.</summary>
        public string ServerFqdn { get; }
    }
}
