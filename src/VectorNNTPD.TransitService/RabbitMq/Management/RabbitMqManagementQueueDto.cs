using System.Text.Json.Serialization;

namespace VectorNNTPD.TransitService.RabbitMq.Management
{
    /// <summary>
    /// Minimal RabbitMQ Management HTTP API queue inventory entry
    /// (<c>GET /api/queues/{vhost}</c>).
    /// </summary>
    internal sealed class RabbitMqManagementQueueDto
    {
        /// <summary>Queue name as reported by the Management API (<c>name</c>).</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>Broker-reported AMQP consumer count (<c>consumers</c>).</summary>
        [JsonPropertyName("consumers")]
        public int Consumers { get; set; }
    }

    /// <summary>Normalized queue inventory row used by availability discovery.</summary>
    /// <param name="Name">Queue name.</param>
    /// <param name="Consumers">AMQP consumer count.</param>
    internal readonly record struct RabbitMqManagementQueueInfo(string Name, int Consumers);
}
