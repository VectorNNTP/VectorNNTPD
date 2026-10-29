using System.Net;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>NNTPD-only SPAMD CHECK seam. Does not mutate <see cref="ArticleRecord.ArtData"/>.</summary>
    internal interface IPostFilterSpamAssassin
    {
        /// <summary>Runs CHECK. Cancellation is rethrown and is not an <c>on_failure</c> event.</summary>
        ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Peer and server identity used only to build the disposable SPAMD scan message.</summary>
    internal readonly record struct SpamdScanContext(
        IPAddress ClientAddress,
        string ServerFqdn,
        DateTimeOffset Now);

    /// <summary>SPAMD endpoints, pool, and timeouts captured with one PostFilter snapshot.</summary>
    internal readonly record struct PostFilterSpamAssassinTarget(
        IReadOnlyList<string> Hosts,
        int Port,
        TimeSpan ConnectTimeout,
        TimeSpan OperationTimeout,
        string ProtocolVersion,
        int MaxConnections,
        PostFilterSpamAssassinHostSelection HostSelection);

    /// <summary>CHECK outcome classified for PostFilter policy.</summary>
    public enum PostFilterSpamAssassinStatus
    {
        /// <summary>SPAMD reported ham.</summary>
        Ham = 0,

        /// <summary>SPAMD reported spam.</summary>
        Spam = 1,

        /// <summary>Connect, protocol, timeout, or malformed response.</summary>
        Failed = 2,
    }

    /// <summary>CHECK result.</summary>
    internal readonly struct PostFilterSpamAssassinResult
    {
        /// <summary>Initializes a result.</summary>
        public PostFilterSpamAssassinResult(
            PostFilterSpamAssassinStatus status,
            string detail,
            decimal? score = null,
            decimal? threshold = null)
        {
            Status = status;
            Detail = detail;
            Score = score;
            Threshold = threshold;
        }

        /// <summary>Gets the classified status.</summary>
        public PostFilterSpamAssassinStatus Status { get; }

        /// <summary>Gets an internal detail.</summary>
        public string Detail { get; }

        /// <summary>Gets the SPAMD score when the Spam header included one.</summary>
        public decimal? Score { get; }

        /// <summary>Gets the SPAMD threshold when the Spam header included one.</summary>
        public decimal? Threshold { get; }

        /// <summary>Ham.</summary>
        public static PostFilterSpamAssassinResult Ham(decimal? score = null, decimal? threshold = null) =>
            new(PostFilterSpamAssassinStatus.Ham, "ham", score, threshold);

        /// <summary>Spam.</summary>
        public static PostFilterSpamAssassinResult Spam(
            string detail,
            decimal? score = null,
            decimal? threshold = null) =>
            new(PostFilterSpamAssassinStatus.Spam, detail, score, threshold);

        /// <summary>Scanner fault.</summary>
        public static PostFilterSpamAssassinResult Failed(string detail) =>
            new(PostFilterSpamAssassinStatus.Failed, detail);
    }
}
