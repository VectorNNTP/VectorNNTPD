namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Details for a single unexpected channel or consumer fault.</summary>
    /// <param name="Kind">Fault classification.</param>
    /// <param name="Reason">Broker or client reason text; never credentials or payload bytes.</param>
    internal sealed record RabbitMqChannelFaultedEventArgs(RabbitMqChannelFaultKind Kind, string Reason);
}
