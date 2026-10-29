using System.Text;
using VectorNNTPD.TransitService.Newsgroups;

namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>
    /// POST newsgroup policy backed by one captured <see cref="INewsgroupCatalogue"/> snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every syntax-validated <c>Newsgroups:</c> target must exist in the snapshot.
    /// Local POST is rejected for unknown groups and for <c>n</c>/<c>x</c>/<c>j</c>
    /// (RFC 3977 §7.6.3, RFC 6048 §3.1). Status <c>y</c> is ordinary local posting.
    /// Status <c>m</c> is classified as <see cref="NewsgroupCatalogStatus.RequiresModeration"/>;
    /// this policy does not authorize <c>Approved:</c> and does not forward to a moderator.
    /// </para>
    /// <para>
    /// The catalogue snapshot is captured once per evaluation. Lookups do not query MySQL.
    /// </para>
    /// </remarks>
    public sealed class CatalogueNewsgroupPostingPolicy : INewsgroupPostingPolicy
    {
        private readonly INewsgroupCatalogue _catalogue;

        /// <summary>Initializes a new instance of the <see cref="CatalogueNewsgroupPostingPolicy"/> class.</summary>
        /// <param name="catalogue">Process-wide catalogue whose <see cref="INewsgroupCatalogue.Current"/> is captured once per evaluation.</param>
        public CatalogueNewsgroupPostingPolicy(INewsgroupCatalogue catalogue)
        {
            ArgumentNullException.ThrowIfNull(catalogue);
            _catalogue = catalogue;
        }

        /// <inheritdoc />
        public NewsgroupPostingEvaluation Evaluate(ReadOnlySpan<string> groups)
        {
            if (groups.IsEmpty)
            {
                return Reject(PostingFailureCategory.InvalidNewsgroups, "empty Newsgroups");
            }

            var snapshot = _catalogue.Current;
            Span<byte> nameBytes = stackalloc byte[128];
            string[]? moderated = null;
            var moderatedCount = 0;
            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group.Length is < 1 or > 128)
                {
                    return Reject(PostingFailureCategory.InvalidNewsgroups, "malformed newsgroup name");
                }

                var written = Encoding.ASCII.GetBytes(group, nameBytes);
                if (!snapshot.TryGet(nameBytes[..written], out var record))
                {
                    return Reject(PostingFailureCategory.PolicyRejected, "unknown newsgroup");
                }

                switch (record.PostingStatus)
                {
                    case NewsgroupPostingStatus.Allowed:
                        continue;
                    case NewsgroupPostingStatus.Prohibited:
                        return Reject(PostingFailureCategory.PolicyRejected, "posting prohibited");
                    case NewsgroupPostingStatus.Moderated:
                        moderated ??= new string[groups.Length];
                        moderated[moderatedCount++] = group;
                        continue;
                    case NewsgroupPostingStatus.NoPostingOrPeerArticles:
                        return Reject(PostingFailureCategory.PolicyRejected, "newsgroup closed");
                    case NewsgroupPostingStatus.PeerOnly:
                        return Reject(PostingFailureCategory.PolicyRejected, "local posting not accepted");
                    default:
                        return Reject(PostingFailureCategory.PolicyRejected, "unsupported posting status");
                }
            }

            if (moderatedCount == 0)
            {
                return new NewsgroupPostingEvaluation(NewsgroupCatalogStatus.Accepted, Failure: null);
            }

            var names = new string[moderatedCount];
            Array.Copy(moderated!, names, moderatedCount);
            return new NewsgroupPostingEvaluation(
                NewsgroupCatalogStatus.RequiresModeration,
                Failure: null,
                names);
        }

        private static NewsgroupPostingEvaluation Reject(PostingFailureCategory category, string detail) =>
            new(NewsgroupCatalogStatus.Rejected, new PostingFailure(category, detail));
    }
}
