using Microsoft.Extensions.Options;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Validates <see cref="SilentConsumerMonitorOptions"/>. Infrastructure (vhost, queues, Management)
    /// is not part of this options type.
    /// </summary>
    internal sealed class SilentConsumerMonitorOptionsValidator : IValidateOptions<SilentConsumerMonitorOptions>
    {
        /// <summary>
        /// Validates <paramref name="options"/>. Currently accepts any non-null instance
        /// (Enabled is unconstrained; infrastructure lives on RabbitMQ/catalog).
        /// </summary>
        /// <param name="name">Options name (unused).</param>
        /// <param name="options">Bound silent-consumer options.</param>
        /// <returns><see cref="ValidateOptionsResult.Success"/> when <paramref name="options"/> is non-null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        public ValidateOptionsResult Validate(string? name, SilentConsumerMonitorOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return ValidateOptionsResult.Success;
        }
    }
}
