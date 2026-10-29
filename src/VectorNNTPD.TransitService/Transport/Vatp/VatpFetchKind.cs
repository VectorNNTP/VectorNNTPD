namespace VectorNNTPD.TransitService.Transport.Vatp
{
    /// <summary>Outcome classification for a VATP article fetch.</summary>
    public enum VatpFetchKind
    {
        /// <summary>Canonical article received and validated.</summary>
        Success,

        /// <summary>Remote peer rejected or failed the transfer (FAIL frame, open rejected).</summary>
        RemoteTransferFailure,

        /// <summary>Local protocol violation or unexpected framing.</summary>
        ProtocolFailure,

        /// <summary>Connect, TLS, or transport I/O failure.</summary>
        ConnectionFailure,

        /// <summary>Operation cancelled by caller.</summary>
        Cancelled,

        /// <summary>Article identity could not be used for OPEN.</summary>
        InvalidArticleId,

        /// <summary>Transfer ended without a consumable canonical record.</summary>
        IncompleteOrMalformedArticle,
    }
}
