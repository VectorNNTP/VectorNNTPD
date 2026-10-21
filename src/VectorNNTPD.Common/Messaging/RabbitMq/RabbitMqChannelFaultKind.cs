namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Why a caller-owned AMQP channel became unusable while the process connection may still be open.</summary>
    internal enum RabbitMqChannelFaultKind
    {
        /// <summary>The AMQP channel shut down (broker or peer close independent of connection loss).</summary>
        ChannelShutdown = 0,

        /// <summary>The broker cancelled the consumer tag without an application <c>basic.cancel</c>.</summary>
        ConsumerCancelled = 1,
    }
}
