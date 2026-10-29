using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>In-memory NntpDB stand-in for PostFilter policy load/refresh tests.</summary>
internal sealed class InMemoryPostFilterPolicyRepository : IPostFilterPolicyRepository
{
    public PostFilterPolicyRecord? Record { get; set; }

    public Exception? Exception { get; set; }

    public int LoadCount { get; private set; }

    public ValueTask<PostFilterPolicyRecord> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadCount++;
        if (Exception is not null)
        {
            throw Exception;
        }

        if (Record is null)
        {
            throw new InvalidOperationException(
                "nntppostfiltercurrent is missing policy_id = 1. NNTPD will not invent a local PostFilter policy.");
        }

        return ValueTask.FromResult(Record);
    }

    public static PostFilterPolicyRecord Create(
        PostFilterOptions options,
        long revision = 1,
        DateTimeOffset? updatedUtc = null) =>
        new(revision, updatedUtc ?? DateTimeOffset.UtcNow, options);
}
