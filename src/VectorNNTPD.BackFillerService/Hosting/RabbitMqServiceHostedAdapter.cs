
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Hosting
{
    /// <summary>
    /// Forwards Generic Host start/stop to <see cref="RabbitMqService"/> without registering it
    /// into BackFiller <c>ApplicationServiceManager</c>.
    /// </summary>
    /// <remarks>
    /// BackFiller keeps RabbitMQ as an early <see cref="IHostedService"/> (before accounts).
    /// Application services remain Cloudflare → ACME → Cache Listener.
    /// </remarks>
    internal sealed class RabbitMqServiceHostedAdapter : IHostedService
    {
        /// <summary>Shared RabbitMQ connection owner started and stopped with the host.</summary>
        private readonly RabbitMqService _service;

        /// <summary>Initializes a new adapter around the shared RabbitMQ connection service.</summary>
        /// <param name="service">Connection owner. Must already be constructed; this adapter does not create it.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="service"/> is <see langword="null"/>.</exception>
        internal RabbitMqServiceHostedAdapter(RabbitMqService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            _service = service;
        }

        /// <summary>Forwards host start to <see cref="RabbitMqService.StartAsync"/>.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="RabbitMqService.StartAsync"/>.</param>
        /// <returns>The task returned by <see cref="RabbitMqService.StartAsync"/>.</returns>
        /// <remarks>Exceptions from the RabbitMQ service propagate and fail host start.</remarks>
        public Task StartAsync(CancellationToken cancellationToken) =>
            _service.StartAsync(cancellationToken);

        /// <summary>Forwards host stop to <see cref="RabbitMqService.StopAsync"/>.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="RabbitMqService.StopAsync"/>.</param>
        /// <returns>The task returned by <see cref="RabbitMqService.StopAsync"/>.</returns>
        /// <remarks>Exceptions from the RabbitMQ service propagate.</remarks>
        public Task StopAsync(CancellationToken cancellationToken) =>
            _service.StopAsync(cancellationToken);
    }
}
