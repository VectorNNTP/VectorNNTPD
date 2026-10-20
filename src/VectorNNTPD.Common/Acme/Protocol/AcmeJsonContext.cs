using System.Text.Json;
using System.Text.Json.Serialization;

namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>STJ source-generation context for ACME wire DTOs.</summary>
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(AcmeDirectoryResource))]
    [JsonSerializable(typeof(AcmeAccountResource))]
    [JsonSerializable(typeof(AcmeOrdersListResource))]
    [JsonSerializable(typeof(AcmeOrderResource))]
    [JsonSerializable(typeof(AcmeAuthorizationResource))]
    [JsonSerializable(typeof(AcmeChallengeResource))]
    [JsonSerializable(typeof(AcmeProblem))]
    [JsonSerializable(typeof(AcmeEmptyResource))]
    internal sealed partial class AcmeJsonContext : JsonSerializerContext;
}
