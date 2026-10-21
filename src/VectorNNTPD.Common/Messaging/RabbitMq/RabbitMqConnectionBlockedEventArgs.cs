namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Broker block details for a single AMQP connection.</summary>
    /// <param name="Reason">Block reason supplied by the broker.</param>
    internal sealed record RabbitMqConnectionBlockedEventArgs(string Reason);
}
