using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// One accepted VATP article handed to in-memory consumers before Path rewrite.
    /// </summary>
    /// <param name="ArtId">Article identity from the accepted inbound bytes.</param>
    /// <param name="ArtHash">Hash of <paramref name="ArtData"/>, matching the inbound META hash.</param>
    /// <param name="ArtSize">Length of <paramref name="ArtData"/> in bytes.</param>
    /// <param name="ArtData">
    /// Original accepted article bytes. This server's Path hop has not been applied.
    /// The queue holds this buffer until a consumer dequeues it. The producer must not
    /// mutate the buffer after a successful enqueue. Dequeue transfers that ownership
    /// to the consumer. The queue does not copy the bytes and does not write them to disk.
    /// </param>
    public readonly record struct AcceptedArticle(
        ArticleId ArtId,
        ulong ArtHash,
        int ArtSize,
        ReadOnlyMemory<byte> ArtData);
}
