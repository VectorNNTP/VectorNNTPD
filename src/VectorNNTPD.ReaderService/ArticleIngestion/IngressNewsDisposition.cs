using System.Text;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Newsgroups;

namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// Post-queue ingress disposition: decides <c>+</c> vs <c>j</c> and whether to log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses in-memory <see cref="NewsgroupSnapshot.TryGet"/>. A successful lookup
    /// means the group exists in the published catalogue. <see cref="NewsgroupPostingStatus.PeerOnly"/>
    /// is the RFC 6048 <c>j</c> status (peer articles permitted, nothing locally filed).
    /// Unknown means <see cref="NewsgroupSnapshot.TryGet"/> returned false. A locally
    /// carried non-junk group is a catalogue hit whose status is not PeerOnly
    /// (<c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>).
    /// </para>
    /// <para>
    /// POST is never WantTrash junk. An unavailable catalogue is treated as
    /// accepted rather than invented junk. Newsgroups bytes come from the
    /// CanonicalV1 field table; the header is not rewritten.
    /// </para>
    /// </remarks>
    internal static class IngressNewsDisposition
    {
        /// <summary>Classifies <paramref name="article"/> without writing a news line.</summary>
        public static NewsLogDisposition Classify(
            InboundArticle article,
            TransitOptions transit,
            INewsgroupCatalogue? catalogue) =>
            Classify(article, transit, catalogue, out _);

        /// <summary>
        /// Classifies <paramref name="article"/> and, for junk, the source-backed reason
        /// including the Newsgroups tokens that caused that decision.
        /// </summary>
        public static NewsLogDisposition Classify(
            InboundArticle article,
            TransitOptions transit,
            INewsgroupCatalogue? catalogue,
            out string? reason)
        {
            ArgumentNullException.ThrowIfNull(article);
            transit ??= new TransitOptions();
            reason = null;
            if (article.Producer == InboundArticleProducer.Post || catalogue is null)
            {
                return NewsLogDisposition.Accepted;
            }

            var snapshot = catalogue.Current;
            var newsgroups = article.Record.Newsgroups;
            if (HasLocalNonJunkGroup(newsgroups, snapshot))
            {
                return NewsLogDisposition.Accepted;
            }

            if (HasPeerOnlyGroup(newsgroups, snapshot))
            {
                reason = FormatResponsibleGroups(
                    IngressNewsReasons.PeerOnly,
                    newsgroups,
                    snapshot,
                    peerOnly: true);
                return NewsLogDisposition.Junk;
            }

            if (transit.WantTrash)
            {
                reason = FormatResponsibleGroups(
                    IngressNewsReasons.NewsgroupNotCarried,
                    newsgroups,
                    snapshot,
                    peerOnly: false);
                return NewsLogDisposition.Junk;
            }

            return NewsLogDisposition.Accepted;
        }

        /// <summary>
        /// TAKETHIS/IHAVE WantTrash=false rejection: every listed group is unknown.
        /// PeerOnly catalogue hits remain accepted junk. POST and a missing catalogue
        /// do not reject.
        /// </summary>
        public static bool IsUncarriedWantTrashRejection(
            InboundArticle article,
            TransitOptions transit,
            INewsgroupCatalogue? catalogue) =>
            IsUncarriedWantTrashRejection(article, transit, catalogue, out _);

        /// <summary>
        /// TAKETHIS/IHAVE WantTrash=false rejection, with the uncarried group names
        /// that caused it. Classification is unchanged from the bool overload.
        /// </summary>
        /// <param name="article">Queued inbound article already constructed.</param>
        /// <param name="transit">Transit options; <c>WantTrash=true</c> never rejects here.</param>
        /// <param name="catalogue">Published catalogue; missing catalogue does not reject.</param>
        /// <param name="reason">
        /// When the method returns <see langword="true"/>, <c>newsgroup not carried</c>
        /// plus the unknown header tokens in article order.
        /// </param>
        public static bool IsUncarriedWantTrashRejection(
            InboundArticle article,
            TransitOptions transit,
            INewsgroupCatalogue? catalogue,
            out string reason)
        {
            ArgumentNullException.ThrowIfNull(article);
            transit ??= new TransitOptions();
            reason = string.Empty;
            if (transit.WantTrash
                || article.Producer == InboundArticleProducer.Post
                || catalogue is null)
            {
                return false;
            }

            var snapshot = catalogue.Current;
            var newsgroups = article.Record.Newsgroups;
            if (HasLocalNonJunkGroup(newsgroups, snapshot)
                || HasPeerOnlyGroup(newsgroups, snapshot))
            {
                return false;
            }

            reason = FormatResponsibleGroups(
                IngressNewsReasons.NewsgroupNotCarried,
                newsgroups,
                snapshot,
                peerOnly: false);
            return true;
        }

        /// <summary>
        /// Builds a news event when the disposition should be recorded.
        /// </summary>
        public static bool TryCreateEvent(
            InboundArticle article,
            TransitOptions transit,
            INewsgroupCatalogue? catalogue,
            DateTimeOffset timestamp,
            out NewsLogEvent evt)
        {
            ArgumentNullException.ThrowIfNull(article);
            transit ??= new TransitOptions();
            var disposition = Classify(article, transit, catalogue, out var reason);
            if (disposition == NewsLogDisposition.Junk && !transit.LogTrash)
            {
                evt = default;
                return false;
            }

            evt = new NewsLogEvent(
                disposition,
                MessageIdMemory(article),
                feed: article.Feed,
                sites: default,
                timestamp,
                reason: Ascii(reason),
                size: article.Payload.Length);
            return true;
        }

        private static ReadOnlyMemory<byte> Ascii(string? value) =>
            string.IsNullOrEmpty(value) ? default : Encoding.ASCII.GetBytes(value);

        private static ReadOnlyMemory<byte> MessageIdMemory(InboundArticle article)
        {
            var range = article.Record.Fields.MessageId;
            if (!range.IsPresent || range.Length == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            return article.Record.ArtData.Slice(range.Offset, range.Length);
        }

        private static bool HasLocalNonJunkGroup(ReadOnlySpan<byte> newsgroups, NewsgroupSnapshot snapshot)
        {
            foreach (var token in EnumerateGroups(newsgroups))
            {
                if (snapshot.TryGet(token, out var group)
                    && group.PostingStatus != NewsgroupPostingStatus.PeerOnly)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds <paramref name="prefix"/> plus the header tokens that match the
        /// already-decided junk/rejection kind, in article Newsgroups order.
        /// </summary>
        /// <param name="prefix">Already-decided reason prefix.</param>
        /// <param name="newsgroups">Article Newsgroups field bytes.</param>
        /// <param name="snapshot">Published catalogue snapshot used by the existing decision.</param>
        /// <param name="peerOnly">
        /// <see langword="true"/> collects PeerOnly catalogue hits;
        /// <see langword="false"/> collects unknown tokens.
        /// </param>
        /// <returns>The prefix, or prefix plus responsible header tokens.</returns>
        private static string FormatResponsibleGroups(
            string prefix,
            ReadOnlySpan<byte> newsgroups,
            NewsgroupSnapshot snapshot,
            bool peerOnly)
        {
            List<string>? names = null;
            foreach (var token in EnumerateGroups(newsgroups))
            {
                var hit = snapshot.TryGet(token, out var group);
                var include = peerOnly
                    ? hit && group.PostingStatus == NewsgroupPostingStatus.PeerOnly
                    : !hit;
                if (!include)
                {
                    continue;
                }

                names ??= new List<string>(2);
                names.Add(Encoding.ASCII.GetString(token));
            }

            return names is null
                ? prefix
                : IngressNewsReasons.WithGroups(prefix, names);
        }

        private static bool HasPeerOnlyGroup(ReadOnlySpan<byte> newsgroups, NewsgroupSnapshot snapshot)
        {
            foreach (var token in EnumerateGroups(newsgroups))
            {
                if (snapshot.TryGet(token, out var group)
                    && group.PostingStatus == NewsgroupPostingStatus.PeerOnly)
                {
                    return true;
                }
            }

            return false;
        }

        private static GroupTokenEnumerable EnumerateGroups(ReadOnlySpan<byte> newsgroups) => new(newsgroups);

        private readonly ref struct GroupTokenEnumerable(ReadOnlySpan<byte> newsgroups)
        {
            private readonly ReadOnlySpan<byte> _newsgroups = newsgroups;

            public Enumerator GetEnumerator() => new(_newsgroups);

            public ref struct Enumerator(ReadOnlySpan<byte> newsgroups)
            {
                private ReadOnlySpan<byte> _remaining = newsgroups;
                private ReadOnlySpan<byte> _current;

                public ReadOnlySpan<byte> Current => _current;

                public bool MoveNext()
                {
                    while (!_remaining.IsEmpty)
                    {
                        var comma = _remaining.IndexOf((byte)',');
                        var token = comma < 0 ? _remaining : _remaining[..comma];
                        _remaining = comma < 0 ? default : _remaining[(comma + 1)..];
                        token = Trim(token);
                        if (!token.IsEmpty)
                        {
                            _current = token;
                            return true;
                        }
                    }

                    return false;
                }

                private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
                {
                    while (!value.IsEmpty && (value[0] == (byte)' ' || value[0] == (byte)'\t'))
                    {
                        value = value[1..];
                    }

                    while (!value.IsEmpty && (value[^1] == (byte)' ' || value[^1] == (byte)'\t'))
                    {
                        value = value[..^1];
                    }

                    return value;
                }
            }
        }
    }
}
