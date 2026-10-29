namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>Application JSON fields for a BackFiller v1 article-work request.</summary>
    /// <remarks>
    /// AMQP <c>CorrelationId</c> and <c>ReplyTo</c> are transport metadata and are never
    /// serialized into this payload.
    /// </remarks>
    /// <param name="Version">Application protocol version. Current value is <c>1</c>.</param>
    /// <param name="RequestId">Application work identity retained across storage and provider publications.</param>
    /// <param name="MessageId">Canonical NNTP Message-ID as supplied on the command line.</param>
    /// <param name="Backbone">Destination backbone identity for the publication target.</param>
    internal sealed record ArticleWorkRequest(
        int Version,
        Guid RequestId,
        string MessageId,
        string Backbone);
}
