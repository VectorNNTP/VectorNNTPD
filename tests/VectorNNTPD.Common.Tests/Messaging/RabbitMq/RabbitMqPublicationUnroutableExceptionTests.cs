using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Ensures unroutable classification stays distinct from generic publish failures.
    /// </summary>
    public sealed class RabbitMqPublicationUnroutableExceptionTests
    {
        /// <summary>
        /// Default message matches the broker return classification string used in production.
        /// </summary>
        [Fact]
        public void Default_message_matches_broker_return_classification()
        {
            var ex = new RabbitMqPublicationUnroutableException();
            Assert.Equal(RabbitMqPublicationUnroutableException.DefaultMessage, ex.Message);
            Assert.Contains("unroutable", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Generic invalid-operation publish failures are not typed as unroutable.
        /// </summary>
        [Fact]
        public void Is_not_the_same_type_as_generic_invalid_operation()
        {
            Exception generic = new InvalidOperationException("RabbitMQ negatively acknowledged the publication.");
            Exception unroutable = new RabbitMqPublicationUnroutableException();
            Assert.IsNotType<RabbitMqPublicationUnroutableException>(generic);
            Assert.IsType<RabbitMqPublicationUnroutableException>(unroutable);
        }
    }
}
