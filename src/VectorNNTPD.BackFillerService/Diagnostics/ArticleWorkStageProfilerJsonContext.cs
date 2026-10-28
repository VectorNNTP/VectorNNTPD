using System.Text.Json.Serialization;

namespace VectorNNTPD.BackFillerService.Diagnostics
{
    /// <summary>
    /// Source-generated JSON metadata for lab stage-profiler snapshots.
    /// </summary>
    /// <remarks>
    /// Required for Native AOT publish (<c>IL2026</c>/<c>IL3050</c>). Lab-only at runtime
    /// (profiler disabled by default), but the serializer still ships in the production binary.
    /// </remarks>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ArticleWorkStageProfilerSnapshot))]
    [JsonSerializable(typeof(ArticleWorkStageSnapshot))]
    [JsonSerializable(typeof(List<ArticleWorkStageSnapshot>))]
    internal sealed partial class ArticleWorkStageProfilerJsonContext : JsonSerializerContext;
}
