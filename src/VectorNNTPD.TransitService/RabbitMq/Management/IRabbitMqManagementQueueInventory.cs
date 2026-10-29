using VectorNNTPD.Common.Messaging.RabbitMq;
namespace VectorNNTPD.TransitService.RabbitMq.Management
{
    /// <summary>
    /// Reads the RabbitMQ Management HTTP API queue inventory for one virtual host.
    /// </summary>
    /// <remarks>
    /// Discovery plane only. Does not publish or consume ArticleWork AMQP messages.
    /// </remarks>
    internal interface IRabbitMqManagementQueueInventory
    {
        /// <summary>
        /// Lists all queues in the configured virtual host via
        /// <c>GET /api/queues/{vhost}</c> (vhost URL-encoded).
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the HTTP request.</param>
        /// <returns>Queue name and consumer count rows. Never <see langword="null"/>.</returns>
        Task<IReadOnlyList<RabbitMqManagementQueueInfo>> ListQueuesAsync(CancellationToken cancellationToken);
    }
}
