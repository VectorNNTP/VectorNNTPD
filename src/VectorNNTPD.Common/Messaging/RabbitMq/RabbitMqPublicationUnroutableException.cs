namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// The broker returned a mandatory publication as unroutable (basic.return / confirm return).
    /// </summary>
    /// <remarks>
    /// Distinct from a publisher-confirm NACK or a connection/channel failure. Callers must not
    /// treat every publish failure as unroutable.
    /// </remarks>
    public sealed class RabbitMqPublicationUnroutableException : InvalidOperationException
    {
        /// <summary>Stable message used when the broker returns a mandatory publication.</summary>
        public const string DefaultMessage = "RabbitMQ returned the publication as unroutable.";

        /// <summary>
        /// Initializes a new instance with <see cref="DefaultMessage"/>.
        /// </summary>
        public RabbitMqPublicationUnroutableException()
            : base(DefaultMessage)
        {
        }

        /// <summary>
        /// Initializes a new instance with <see cref="DefaultMessage"/> and an inner exception.
        /// </summary>
        /// <param name="innerException">Broker client exception that reported the return.</param>
        public RabbitMqPublicationUnroutableException(Exception? innerException)
            : base(DefaultMessage, innerException)
        {
        }
    }
}
