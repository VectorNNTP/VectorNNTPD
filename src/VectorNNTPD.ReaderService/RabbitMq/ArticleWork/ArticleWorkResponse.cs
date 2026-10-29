namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>Parsed BackFiller v1 article-work response JSON.</summary>
    /// <param name="Version">Application protocol version.</param>
    /// <param name="RequestId">Application request identity when the responder could establish it.</param>
    /// <param name="MessageId">Request Message-ID when the responder could establish it.</param>
    /// <param name="Backbone">Request backbone when the responder could establish it.</param>
    /// <param name="Outcome">Terminal response outcome.</param>
    /// <param name="Fqdn">Success-only BackFiller FQDN. Absent for non-success outcomes.</param>
    /// <param name="VatpPort">Success-only TLS VATP listen port. Absent for non-success outcomes.</param>
    /// <param name="ArticleId">Success-only CanonicalV1 ArtId. Absent for non-success outcomes.</param>
    /// <param name="Error">Terminal-failure detail. Absent for success.</param>
    internal sealed record ArticleWorkResponse(
        int Version,
        Guid? RequestId,
        string? MessageId,
        string? Backbone,
        ArticleWorkOutcome Outcome,
        string? Fqdn,
        int? VatpPort,
        VectorNNTPD.Common.Articles.ArticleId? ArticleId,
        string? Error);
}
