using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Acme.Protocol;

namespace VectorNNTPD.TransitService.Tests.Acme;

public sealed class AcmeProblemDiagnosticsTests
{
    [Fact]
    public void FormatProblemFields_PreservesStructuredFields()
    {
        var formatted = AcmeProblemDiagnostics.FormatProblemFields(
            preface: null,
            type: "urn:ietf:params:acme:error:orderNotReady",
            status: 403,
            identifier: "nntpd01.usenet.ninja",
            detail: "Order's status (\"pending\") is not acceptable for finalization",
            subproblems: null);

        Assert.Contains("type=urn:ietf:params:acme:error:orderNotReady", formatted, StringComparison.Ordinal);
        Assert.Contains("status=403", formatted, StringComparison.Ordinal);
        Assert.Contains("identifier=nntpd01.usenet.ninja", formatted, StringComparison.Ordinal);
        Assert.Contains("detail=", formatted, StringComparison.Ordinal);
        Assert.Contains("pending", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatException_RedactsPemAndLongTokens()
    {
        var pem = "oops -----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY----- trailing";
        var ex = new AcmeCaException(
            "server error",
            "urn:ietf:params:acme:error:serverInternal",
            pem + " " + new string('A', 100),
            statusCode: 500);
        var formatted = AcmeProblemDiagnostics.FormatException(ex);
        Assert.Contains("[redacted-pem]", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted-token]", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureSanitizer_IncludesAcmeOrderDiagnosticMessage()
    {
        var ex = new AcmeOrderException(
            "challenge_failed",
            "type=urn:ietf:params:acme:error:dns status=400 identifier=nntpd01.usenet.ninja detail=NXDOMAIN");
        var sanitized = AcmeFailureSanitizer.Sanitize(ex);
        Assert.Contains("challenge_failed", sanitized, StringComparison.Ordinal);
        Assert.Contains("urn:ietf:params:acme:error:dns", sanitized, StringComparison.Ordinal);
        Assert.Contains("nntpd01.usenet.ninja", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ChallengeFailedWrapper_UsesFormattedProtocolDetails_NotTypeNameOnly()
    {
        var protocol = new AcmeCaException(
            "Error processing request",
            "urn:ietf:params:acme:error:dns",
            "DNS problem: NXDOMAIN looking up TXT",
            statusCode: 400);
        var wrapped = new AcmeOrderException("challenge_failed", AcmeProblemDiagnostics.FormatException(protocol));

        Assert.Equal("challenge_failed", wrapped.Category);
        Assert.Contains("type=urn:ietf:params:acme:error:dns", wrapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("challenge_failed: AcmeException", wrapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("challenge_failed: AcmeCaException", wrapped.Message, StringComparison.Ordinal);
        Assert.Equal(wrapped.Message, AcmeFailureSanitizer.Sanitize(wrapped));
    }
}
