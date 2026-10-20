using System.Text.Json.Serialization;

namespace VectorNNTPD.Common.Acme.Protocol
{
    /// <summary>ACME directory document (RFC 8555 §7.1.1).</summary>
    internal sealed class AcmeDirectoryResource
    {
        /// <summary>
        /// Directory <c>newNonce</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// <see cref="AcmeHttpTransport"/> requires it before a JWS request can obtain a replay nonce.
        /// </summary>
        [JsonPropertyName("newNonce")]
        public Uri? NewNonce { get; set; }

        /// <summary>
        /// Directory <c>newAccount</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// Account registration fails when this is missing.
        /// </summary>
        [JsonPropertyName("newAccount")]
        public Uri? NewAccount { get; set; }

        /// <summary>
        /// Directory <c>newOrder</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// Order creation fails when this is missing.
        /// </summary>
        [JsonPropertyName("newOrder")]
        public Uri? NewOrder { get; set; }

        /// <summary>
        /// Directory <c>newAuthz</c> URL (pre-authorization). <see langword="null"/> when the field is absent or JSON null.
        /// This client does not call it.
        /// </summary>
        [JsonPropertyName("newAuthz")]
        public Uri? NewAuthorization { get; set; }

        /// <summary>
        /// Directory <c>revokeCert</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// This client does not call it.
        /// </summary>
        [JsonPropertyName("revokeCert")]
        public Uri? RevokeCertificate { get; set; }

        /// <summary>
        /// Directory <c>keyChange</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// This client does not call it.
        /// </summary>
        [JsonPropertyName("keyChange")]
        public Uri? KeyChange { get; set; }

        /// <summary>
        /// Directory <c>renewalInfo</c> URL when the CA advertises it. <see langword="null"/> when the field is absent or JSON null.
        /// This client does not call it.
        /// </summary>
        [JsonPropertyName("renewalInfo")]
        public Uri? RenewalInfo { get; set; }

        /// <summary>
        /// Directory <c>meta</c> object. <see langword="null"/> when the field is absent or JSON null.
        /// </summary>
        [JsonPropertyName("meta")]
        public AcmeDirectoryMeta? Meta { get; set; }
    }

    /// <summary>Directory <c>meta</c> object.</summary>
    internal sealed class AcmeDirectoryMeta
    {
        /// <summary>
        /// <c>termsOfService</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// Registration does not read this URL; it sends <c>termsOfServiceAgreed</c> from the caller.
        /// </summary>
        [JsonPropertyName("termsOfService")]
        public Uri? TermsOfService { get; set; }

        /// <summary>
        /// <c>website</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// Not read by issuance.
        /// </summary>
        [JsonPropertyName("website")]
        public Uri? Website { get; set; }

        /// <summary>
        /// <c>caaIdentities</c> hostnames. <see langword="null"/> when the field is absent or JSON null.
        /// Not read by issuance.
        /// </summary>
        [JsonPropertyName("caaIdentities")]
        public IReadOnlyList<string>? CaaIdentities { get; set; }

        /// <summary>
        /// <c>externalAccountRequired</c>. Omitted JSON deserializes as <see langword="false"/>.
        /// Registration still sends no external account binding.
        /// </summary>
        [JsonPropertyName("externalAccountRequired")]
        public bool ExternalAccountRequired { get; set; }

        /// <summary>
        /// <c>profiles</c> map of profile name to description. <see langword="null"/> when the field is absent or JSON null.
        /// Order creation does not select a profile.
        /// </summary>
        [JsonPropertyName("profiles")]
        public IReadOnlyDictionary<string, string>? Profiles { get; set; }
    }

    /// <summary>ACME account resource.</summary>
    internal sealed class AcmeAccountResource
    {
        /// <summary>
        /// Account <c>status</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Registration stores the Location URL and does not branch on this value.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Account <c>contact</c> URIs. <see langword="null"/> when the field is absent or JSON null.
        /// </summary>
        [JsonPropertyName("contact")]
        public IReadOnlyList<string>? Contact { get; set; }

        /// <summary>
        /// Account <c>orders</c> list URL. <see langword="null"/> when the field is absent or JSON null.
        /// This client does not fetch it.
        /// </summary>
        [JsonPropertyName("orders")]
        public Uri? Orders { get; set; }
    }

    /// <summary>ACME order resource.</summary>
    internal sealed class AcmeOrderResource
    {
        /// <summary>
        /// Order <c>status</c> wire value (<see cref="AcmeStatus"/>). <see langword="null"/> when the field is absent or JSON null.
        /// Readiness treats null as not ready and not invalid. <see cref="AcmeClient.WaitForOrderAsync"/> returns on
        /// <see cref="AcmeStatus.Valid"/> and fails on <see cref="AcmeStatus.Invalid"/>.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Order <c>expires</c> timestamp. <see langword="null"/> when the field is absent or JSON null.
        /// Local poll deadlines do not use this value.
        /// </summary>
        [JsonPropertyName("expires")]
        public DateTimeOffset? Expires { get; set; }

        /// <summary>
        /// Order <c>identifiers</c>. <see langword="null"/> when the field is absent or JSON null.
        /// </summary>
        [JsonPropertyName("identifiers")]
        public IReadOnlyList<AcmeIdentifier>? Identifiers { get; set; }

        /// <summary>
        /// Order <c>authorizations</c> URLs. <see langword="null"/> when the field is absent or JSON null.
        /// Issuance fails with <c>no_dns01_challenge</c> when this is null or empty.
        /// </summary>
        [JsonPropertyName("authorizations")]
        public IReadOnlyList<Uri>? Authorizations { get; set; }

        /// <summary>
        /// Order <c>finalize</c> URL. <see langword="null"/> when the field is absent or JSON null.
        /// Finalize fails when both the create-order value and a later refresh are null.
        /// </summary>
        [JsonPropertyName("finalize")]
        public Uri? Finalize { get; set; }

        /// <summary>
        /// Order <c>certificate</c> URL. <see langword="null"/> until the CA publishes it, or when the field is JSON null.
        /// Download fails when this is still null after the order becomes <see cref="AcmeStatus.Valid"/>.
        /// </summary>
        [JsonPropertyName("certificate")]
        public Uri? Certificate { get; set; }

        /// <summary>
        /// Order <c>error</c> problem document. <see langword="null"/> when the field is absent or JSON null.
        /// Included in the exception when <see cref="Status"/> is <see cref="AcmeStatus.Invalid"/>.
        /// </summary>
        [JsonPropertyName("error")]
        public AcmeProblem? Error { get; set; }
    }

    /// <summary>ACME identifier (dns / ip).</summary>
    internal sealed class AcmeIdentifier
    {
        /// <summary>
        /// Identifier <c>type</c> wire value. Omitted JSON keeps <see cref="AcmeIdentifierTypes.Dns"/>.
        /// Issuance writes <see cref="AcmeIdentifierTypes.Dns"/> only.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = AcmeIdentifierTypes.Dns;

        /// <summary>
        /// Identifier <c>value</c> (DNS name or IP text). Omitted JSON keeps an empty string.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>ACME authorization resource.</summary>
    internal sealed class AcmeAuthorizationResource
    {
        /// <summary>
        /// Authorization <c>identifier</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Issuance fails with <c>no_dns01_challenge</c> when <see cref="AcmeIdentifier.Value"/> is missing.
        /// </summary>
        [JsonPropertyName("identifier")]
        public AcmeIdentifier? Identifier { get; set; }

        /// <summary>
        /// Authorization <c>status</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Readiness treats a null or unrecognized status as still pending unless the order itself is ready or invalid.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Authorization <c>expires</c> timestamp. <see langword="null"/> when the field is absent or JSON null.
        /// The readiness poll does not use this value.
        /// </summary>
        [JsonPropertyName("expires")]
        public DateTimeOffset? Expires { get; set; }

        /// <summary>
        /// Authorization <c>challenges</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Issuance requires a challenge whose <c>type</c> is <c>dns-01</c>.
        /// </summary>
        [JsonPropertyName("challenges")]
        public IReadOnlyList<AcmeChallengeResource>? Challenges { get; set; }

        /// <summary>
        /// Authorization <c>wildcard</c> flag. Omitted JSON deserializes as <see langword="false"/>.
        /// Issuance does not branch on this flag; the identifier value is used as the DNS-01 name.
        /// </summary>
        [JsonPropertyName("wildcard")]
        public bool Wildcard { get; set; }
    }

    /// <summary>ACME challenge resource.</summary>
    internal sealed class AcmeChallengeResource
    {
        /// <summary>
        /// Challenge <c>type</c> (for example <c>dns-01</c>). <see langword="null"/> when the field is absent or JSON null.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Challenge <c>url</c> posted to start validation. <see langword="null"/> when the field is absent or JSON null.
        /// Issuance rejects a dns-01 challenge that has no URL.
        /// </summary>
        [JsonPropertyName("url")]
        public Uri? Url { get; set; }

        /// <summary>
        /// Challenge <c>status</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Readiness uses the authorization status, not this field.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Challenge <c>token</c> used to build the DNS-01 key authorization. <see langword="null"/> when the field is absent or JSON null.
        /// Issuance rejects a dns-01 challenge whose token is missing or whitespace.
        /// </summary>
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        /// <summary>
        /// Challenge <c>validated</c> timestamp. <see langword="null"/> when the field is absent, JSON null, or validation has not completed.
        /// Not read by issuance.
        /// </summary>
        [JsonPropertyName("validated")]
        public DateTimeOffset? Validated { get; set; }

        /// <summary>
        /// Challenge <c>error</c> problem document. <see langword="null"/> when the field is absent or JSON null.
        /// Readiness prefers the dns-01 challenge error, then any other challenge error on the same authorization.
        /// </summary>
        [JsonPropertyName("error")]
        public AcmeProblem? Error { get; set; }
    }

    /// <summary>ACME / HTTP problem details document.</summary>
    internal sealed class AcmeProblem
    {
        /// <summary>
        /// Problem <c>type</c> URI. <see langword="null"/> when the field is absent or JSON null.
        /// <see cref="AcmeErrorTypes.BadNonce"/> and <see cref="AcmeErrorTypes.RateLimited"/> change transport retry behavior.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Problem <c>title</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Not copied into <see cref="AcmeCaException"/>.
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>
        /// Problem <c>detail</c>. <see langword="null"/> when the field is absent or JSON null.
        /// Used as the CA exception detail when present.
        /// </summary>
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        /// <summary>
        /// Problem <c>status</c> HTTP code. <see langword="null"/> when the field is absent or JSON null.
        /// Stored on <see cref="AcmeCaException.StatusCode"/> when the transport builds an exception from this document.
        /// </summary>
        [JsonPropertyName("status")]
        public int? Status { get; set; }

        /// <summary>
        /// Problem <c>subproblems</c>. <see langword="null"/> when the field is absent or JSON null.
        /// The transport stores the parent problem only; diagnostics format subproblems when a caller passes them separately.
        /// </summary>
        [JsonPropertyName("subproblems")]
        public IReadOnlyList<AcmeProblem>? Subproblems { get; set; }

        /// <summary>
        /// Problem <c>identifier</c> for a failed authorization. <see langword="null"/> when the field is absent or JSON null.
        /// </summary>
        [JsonPropertyName("identifier")]
        public AcmeIdentifier? Identifier { get; set; }
    }

    /// <summary>ACME account orders list (RFC 8555 §7.1.2.1).</summary>
    internal sealed class AcmeOrdersListResource
    {
        /// <summary>
        /// Account <c>orders</c> URL array. <see langword="null"/> when the field is absent or JSON null.
        /// Order recovery after an ambiguous <c>newOrder</c> scans this list; an incomplete page is a recovery limit.
        /// </summary>
        [JsonPropertyName("orders")]
        public IReadOnlyList<Uri>? Orders { get; set; }
    }

    /// <summary>Empty JSON body (successful POST with no resource).</summary>
    internal sealed class AcmeEmptyResource;

    /// <summary>ACME identifier type wire values.</summary>
    internal static class AcmeIdentifierTypes
    {
        /// <summary>Identifier <c>type</c> value <c>dns</c>. The only type written by issuance.</summary>
        public const string Dns = "dns";

        /// <summary>Identifier <c>type</c> value <c>ip</c>. Deserialized when a CA sends it; issuance does not request it.</summary>
        public const string Ip = "ip";
    }

    /// <summary>ACME status wire values used by order / authorization polling.</summary>
    internal static class AcmeStatus
    {
        /// <summary>Status <c>pending</c>.</summary>
        public const string Pending = "pending";

        /// <summary>Status <c>ready</c>. Readiness treats this order status as ready to finalize.</summary>
        public const string Ready = "ready";

        /// <summary>Status <c>processing</c>.</summary>
        public const string Processing = "processing";

        /// <summary>Status <c>valid</c>. Order polling returns when the order status equals this value.</summary>
        public const string Valid = "valid";

        /// <summary>Status <c>invalid</c>. Order polling and readiness fail when an order or authorization status equals this value.</summary>
        public const string Invalid = "invalid";

        /// <summary>Status <c>deactivated</c>.</summary>
        public const string Deactivated = "deactivated";

        /// <summary>Status <c>expired</c>.</summary>
        public const string Expired = "expired";

        /// <summary>Status <c>revoked</c>.</summary>
        public const string Revoked = "revoked";
    }

    /// <summary>Common ACME problem type URNs.</summary>
    internal static class AcmeErrorTypes
    {
        /// <summary>
        /// <c>urn:ietf:params:acme:error:badNonce</c>.
        /// The transport discards that JWS and signs again with a new nonce; it is not a transient HTTP retry.
        /// </summary>
        public const string BadNonce = "urn:ietf:params:acme:error:badNonce";

        /// <summary>
        /// <c>urn:ietf:params:acme:error:rateLimited</c>.
        /// The transport throws <see cref="AcmeCaRateLimitException"/> for this type and for HTTP 429.
        /// </summary>
        public const string RateLimited = "urn:ietf:params:acme:error:rateLimited";

        /// <summary><c>urn:ietf:params:acme:error:accountDoesNotExist</c>. Stored for comparison; the client does not branch on it.</summary>
        public const string AccountDoesNotExist = "urn:ietf:params:acme:error:accountDoesNotExist";

        /// <summary><c>urn:ietf:params:acme:error:orderNotReady</c>. Stored for comparison; the client does not branch on it.</summary>
        public const string OrderNotReady = "urn:ietf:params:acme:error:orderNotReady";

        /// <summary><c>urn:ietf:params:acme:error:dns</c>. Stored for comparison; the client does not branch on it.</summary>
        public const string Dns = "urn:ietf:params:acme:error:dns";

        /// <summary><c>urn:ietf:params:acme:error:unauthorized</c>. Stored for comparison; the client does not branch on it.</summary>
        public const string Unauthorized = "urn:ietf:params:acme:error:unauthorized";

        /// <summary><c>urn:ietf:params:acme:error:incorrectResponse</c>. Stored for comparison; the client does not branch on it.</summary>
        public const string IncorrectResponse = "urn:ietf:params:acme:error:incorrectResponse";
    }
}
