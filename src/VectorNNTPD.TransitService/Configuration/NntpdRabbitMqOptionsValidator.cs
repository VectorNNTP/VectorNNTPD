using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// NNTPD RabbitMQ options validator: Common connection rules plus required Management HTTP settings.
    /// </summary>
    /// <remarks>
    /// NNTPD ArticleWork availability discovery requires Management BaseUrl.
    /// Broker username and password are published from <c>nntpsharedconfig</c> after this validator runs.
    /// Connectivity-only hosts should register <see cref="RabbitMqOptionsValidator"/> alone.
    /// </remarks>
    public sealed class NntpdRabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            RabbitMqOptionsValidator.ValidateConnection(options, failures);
            ValidateRequiredManagement(options, failures);
            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static void ValidateRequiredManagement(RabbitMqOptions rabbitMq, List<string> failures)
        {
            rabbitMq.Management ??= new RabbitMqManagementOptions();
            var management = rabbitMq.Management;

            if (string.IsNullOrWhiteSpace(management.BaseUrl))
            {
                failures.Add("RabbitMQ:Management:BaseUrl is required.");
                return;
            }

            RabbitMqOptionsValidator.ValidateManagementShape(management, failures);
        }
    }
}
