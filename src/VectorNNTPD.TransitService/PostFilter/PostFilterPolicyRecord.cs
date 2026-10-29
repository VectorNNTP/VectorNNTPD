using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>
    /// Authoritative cluster PostFilter policy loaded from NntpDB, before compile.
    /// </summary>
    public sealed class PostFilterPolicyRecord
    {
        /// <summary>Initializes a loaded policy document.</summary>
        public PostFilterPolicyRecord(long revision, DateTimeOffset updatedUtc, PostFilterOptions options)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
            ArgumentNullException.ThrowIfNull(options);
            Revision = revision;
            UpdatedUtc = updatedUtc;
            Options = options;
        }

        /// <summary>Gets the published NntpDB revision (<c>nntppostfiltercurrent.revision</c>).</summary>
        public long Revision { get; }

        /// <summary>Gets <c>nntppostfilterpolicy.updated_utc</c>.</summary>
        public DateTimeOffset UpdatedUtc { get; }

        /// <summary>Gets the compile input. Not bound from <c>Nntpd:PostFilter</c>.</summary>
        public PostFilterOptions Options { get; }
    }
}
