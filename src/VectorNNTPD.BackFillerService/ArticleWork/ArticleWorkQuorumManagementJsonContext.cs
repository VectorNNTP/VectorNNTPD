using System.Text.Json.Serialization;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Source-generated JSON metadata for RabbitMQ Management queue documents used by quorum health.
    /// </summary>
    /// <remarks>
    /// Mirrors the historical case-insensitive property names used by
    /// <see cref="ArticleWorkQuorumManagementHealthProbe"/>. Required for Native AOT publish
    /// (<c>IL2026</c>/<c>IL3050</c>).
    /// </remarks>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ArticleWorkQuorumManagementQueueDocument))]
    [JsonSerializable(typeof(List<string>))]
    internal sealed partial class ArticleWorkQuorumManagementJsonContext : JsonSerializerContext;

    /// <summary>
    /// Subset of the Management <c>GET /api/queues/{vhost}/{name}</c> JSON used for quorum health.
    /// </summary>
    internal sealed class ArticleWorkQuorumManagementQueueDocument
    {
        /// <summary>Configured quorum member node names.</summary>
        public List<string>? Members { get; set; }

        /// <summary>Online quorum member node names.</summary>
        public List<string>? Online { get; set; }

        /// <summary>Current Raft leader node name.</summary>
        public string? Leader { get; set; }
    }
}
