using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Validates that <see cref="SilentConsumerMonitorOptionsValidator"/> accepts both enabled and disabled monitor flags
    /// without inspecting RabbitMQ management or other infrastructure on this options type.
    /// </summary>
    public sealed class SilentConsumerMonitorOptionsValidatorTests
    {
        /// <summary>
        /// <see cref="SilentConsumerMonitorOptionsValidator.Validate"/> succeeds for <c>Enabled</c> true and false.
        /// </summary>
        [Fact]
        public void Enabled_and_disabled_both_succeed()
        {
            var validator = new SilentConsumerMonitorOptionsValidator();
            Assert.True(validator.Validate(null, new SilentConsumerMonitorOptions { Enabled = true }).Succeeded);
            Assert.True(validator.Validate(null, new SilentConsumerMonitorOptions { Enabled = false }).Succeeded);
        }
    }
}
