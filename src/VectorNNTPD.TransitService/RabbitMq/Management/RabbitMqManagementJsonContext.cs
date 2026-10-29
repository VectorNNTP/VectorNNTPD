using System.Text.Json.Serialization;

namespace VectorNNTPD.TransitService.RabbitMq.Management
{
    /// <summary>
    /// Source-generated JSON metadata for RabbitMQ Management API queue inventory responses.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(RabbitMqManagementQueueDto[]))]
    [JsonSerializable(typeof(List<RabbitMqManagementQueueDto>))]
    internal sealed partial class RabbitMqManagementJsonContext : JsonSerializerContext;
}
