using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// RabbitMqOptions validation and ToRuntimeOptions host normalization.
    /// </summary>

    public sealed class RabbitMqOptionsTests
    {
        /// <summary>
        /// Contract: ToRuntimeOptions trims and deduplicates hosts.
        /// </summary>

        [Fact]
        public void ToRuntimeOptions_trims_and_deduplicates_hosts()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Hosts = [" 127.0.0.1 ", "localhost", "LOCALHOST", "127.0.0.1"];

            var runtime = rabbit.ToRuntimeOptions();

            Assert.Equal(["127.0.0.1", "localhost"], runtime.Hosts);
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_rejects_null_rabbitmq_hosts()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Hosts = null;

            var result = new RabbitMqOptionsValidator().Validate(null, rabbit);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("RabbitMQ:Hosts", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_rejects_empty_rabbitmq_hosts()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Hosts = [];

            var result = new RabbitMqOptionsValidator().Validate(null, rabbit);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("RabbitMQ:Hosts", StringComparison.Ordinal));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Validate_rejects_blank_rabbitmq_hosts()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.Hosts = [" ", "\t"];

            var result = new RabbitMqOptionsValidator().Validate(null, rabbit);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("RabbitMQ:Hosts", StringComparison.Ordinal));
        }
    }
}
