using System.Net;
using System.Text;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Session.Commands.Posting;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class PostArticleValidationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingDate_IsSynthesized()
    {
        var parsed = Parse(Build(date: null));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.True(parsed.DateSynthesized);
        Assert.Equal(Now, parsed.AuthorDate);
    }

    [Fact]
    public void OmittedDate_NormalizeWritesGeneratedDateOnce()
    {
        var parsed = Parse(Build(date: null, extraHeaders: "Path: client.path\r\n"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        var protector = AesGcmPostingTraceProtector.Create(
            new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey });
        var normalized = PostHeaderNormalizer.Normalize(
            parsed,
            Now,
            "nntpd01.usenet.ninja",
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            NntpdOptions.DefaultMailComplaintsTo,
            protector);
        var text = Encoding.ASCII.GetString(normalized.Span);
        var generated = PostRfcDate.Format(Now);
        Assert.Equal(1, CountHeader(text, "Date: "));
        Assert.Contains("Date: " + generated + "\r\n", text, StringComparison.Ordinal);
        Assert.Equal(1, CountHeader(text, "Path: "));
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("client.path", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFrom_IsRejected()
    {
        AssertRejected(Build(from: null), PostingFailureCategory.MissingRequiredHeader);
    }

    [Fact]
    public void MissingNewsgroups_IsRejected()
    {
        AssertRejected(Build(newsgroups: null), PostingFailureCategory.MissingRequiredHeader);
    }

    [Fact]
    public void MissingSubject_IsRejected()
    {
        AssertRejected(Build(subject: null), PostingFailureCategory.MissingRequiredHeader);
    }

    [Fact]
    public void MissingMessageId_IsSynthesized()
    {
        var parsed = Parse(Build(messageId: null));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.True(parsed.MessageIdSynthesized);
        Assert.StartsWith("<", parsed.MessageId, StringComparison.Ordinal);
        Assert.EndsWith("@usenet.ninja>", parsed.MessageId, StringComparison.Ordinal);
        Assert.Equal(47, parsed.MessageId!.Length);
    }

    [Fact]
    public void SynthesizedMessageId_UsesConfiguredDomainAndMd5Length()
    {
        var parsed = Parse(Build(messageId: null));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.Matches(@"^<[0-9a-f]{32}@usenet\.ninja>$", parsed.MessageId);
    }

    [Fact]
    public void MalformedMessageId_IsRejected()
    {
        AssertRejected(Build(messageId: "not-an-id"), PostingFailureCategory.InvalidMessageId);
        AssertRejected(Build(messageId: "<a b>"), PostingFailureCategory.InvalidMessageId);
        AssertRejected(Build(messageId: "<a>b>"), PostingFailureCategory.InvalidMessageId);
    }

    [Fact]
    public void Rfc5536MessageId_IsAccepted()
    {
        AssertRejected(Build(messageId: "<x>"), PostingFailureCategory.InvalidMessageId);
        AssertRejected(Build(messageId: "<\"quoted\"@example.com>"), PostingFailureCategory.InvalidMessageId);
        AssertRejected(Build(messageId: "<double..dot@example.com>"), PostingFailureCategory.InvalidMessageId);
        var parsed = Parse(Build(messageId: "<foo.bar@example.com>"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.Equal("<foo.bar@example.com>", parsed.MessageId);

        parsed = Parse(Build(messageId: "<foo@[1.2.3.4]>"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.Equal("<foo@[1.2.3.4]>", parsed.MessageId);
    }

    [Fact]
    public void MalformedDate_IsRejected()
    {
        AssertRejected(Build(date: "yesterday"), PostingFailureCategory.InvalidDate);
    }

    [Fact]
    public void FutureDateBeyondPolicy_IsRejected()
    {
        AssertRejected(Build(date: PostRfcDate.Format(Now.AddHours(25))), PostingFailureCategory.InvalidDate);
    }

    [Fact]
    public void ExcessivelyOldDate_IsRejected()
    {
        AssertRejected(Build(date: PostRfcDate.Format(Now.AddDays(-15))), PostingFailureCategory.InvalidDate);
    }

    [Fact]
    public void GmtDate_IsAccepted()
    {
        var parsed = Parse(Build(date: "25 Sep 2026 12:00:00 GMT"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.Equal(Now, parsed.AuthorDate);
        Assert.False(parsed.DateSynthesized);
    }

    [Fact]
    public void DuplicateSingleton_IsRejected()
    {
        var extra = "Subject: first\r\nSubject: second\r\n";
        AssertRejected(Build(extraHeaders: extra, subject: null), PostingFailureCategory.DuplicateHeader);
    }

    [Fact]
    public void MalformedNewsgroup_IsRejected()
    {
        AssertRejected(Build(newsgroups: "misc..test"), PostingFailureCategory.InvalidNewsgroups);
    }

    [Fact]
    public void DuplicateNewsgroup_IsRejected()
    {
        AssertRejected(Build(newsgroups: "misc.test,misc.test"), PostingFailureCategory.InvalidNewsgroups);
    }

    [Fact]
    public void MultipleValidNewsgroups_AreAccepted()
    {
        var parsed = Parse(Build(newsgroups: "misc.test, alt.test"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        Assert.Equal(["misc.test", "alt.test"], parsed.Newsgroups);
    }

    [Fact]
    public void FollowupToPoster_IsAccepted()
    {
        var parsed = Parse(Build(extraHeaders: "Followup-To: poster\r\n"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
    }

    [Fact]
    public void MalformedFollowupTo_IsRejected()
    {
        AssertRejected(Build(extraHeaders: "Followup-To: ..bad\r\n"), PostingFailureCategory.InvalidFollowupTo);
    }

    [Fact]
    public void InReplyToNotInReferences_IsRejected()
    {
        AssertRejected(
            Build(extraHeaders: "References: <a@example.com>\r\nIn-Reply-To: <b@example.com>\r\n"),
            PostingFailureCategory.InvalidReferences);
    }

    [Fact]
    public void ControlHeader_IsRejected()
    {
        AssertRejected(
            Build(extraHeaders: "Control: cancel <a@example.com>\r\n"),
            PostingFailureCategory.InvalidControl);
    }

    [Fact]
    public void ControlHeader_AuthorizedCancel_IsAccepted()
    {
        var parsed = Parse(Build(
            messageId: "<cancel-article@example.com>",
            extraHeaders: "Control: cancel <original@example.com>\r\n"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _,
            controlCancelPermitted: true));
        Assert.Equal("<cancel-article@example.com>", parsed.MessageId);
    }

    [Fact]
    public void ControlHeader_Authorized_RejectsOtherVerb()
    {
        var parsed = Parse(Build(extraHeaders: "Control: newgroup misc.test\r\n"));
        Assert.False(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out var failure,
            controlCancelPermitted: true));
        Assert.Equal(PostingFailureCategory.InvalidControl, failure.Category);
    }

    [Fact]
    public void ControlHeader_Authorized_RejectsMalformedCancel()
    {
        var parsed = Parse(Build(extraHeaders: "Control: cancel not-an-id\r\n"));
        Assert.False(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out var failure,
            controlCancelPermitted: true));
        Assert.Equal(PostingFailureCategory.InvalidControl, failure.Category);
    }

    [Fact]
    public void ControlHeader_Authorized_RejectsCancelWithExtraArgument()
    {
        var parsed = Parse(Build(extraHeaders: "Control: cancel <a@example.com> extra\r\n"));
        Assert.False(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out var failure,
            controlCancelPermitted: true));
        Assert.Equal(PostingFailureCategory.InvalidControl, failure.Category);
    }

    [Fact]
    public void MalformedApproved_IsRejected()
    {
        AssertRejected(Build(extraHeaders: "Approved: not-a-mailbox\r\n"), PostingFailureCategory.InvalidApproved);
    }

    [Fact]
    public void MailRoutingHeader_IsPolicyRejected()
    {
        AssertRejected(Build(extraHeaders: "Received: from nowhere\r\n"), PostingFailureCategory.PolicyRejected);
    }

    [Fact]
    public void LeadingFold_IsRejected()
    {
        Assert.False(PostHeaderParser.TryParse(
            Encoding.ASCII.GetBytes(" Subject: folded\r\n\r\nbody\r\n"),
            out _,
            out var failure));
        Assert.Equal(PostingFailureCategory.MalformedHeader, failure.Category);
    }

    [Fact]
    public void EmbeddedNul_IsRejected()
    {
        var bytes = Encoding.ASCII.GetBytes(Build());
        bytes[10] = 0;
        Assert.False(PostHeaderParser.TryParse(bytes, out _, out var failure));
        Assert.Equal(PostingFailureCategory.MalformedHeader, failure.Category);
    }

    [Fact]
    public void NewsgroupPolicy_ReportsCatalogUnavailable()
    {
        var evaluation = SyntaxOnlyNewsgroupPostingPolicy.Instance.Evaluate(["misc.test"]);
        Assert.Equal(NewsgroupCatalogStatus.CatalogUnavailable, evaluation.Status);
        Assert.Null(evaluation.Failure);
    }

    [Fact]
    public void Normalize_ReplacesServerOwnedHeaders_AndPreservesDate()
    {
        var date = PostRfcDate.Format(Now);
        var parsed = Parse(Build(
            date: date,
            extraHeaders:
                "Path: client.path\r\n" +
                "Injection-Date: forged\r\n" +
                "Injection-Info: forged\r\n" +
                "NNTP-Posting-Date: forged\r\n" +
                "NNTP-Posting-Host: 1.2.3.4\r\n" +
                "X-Trace: forged\r\n" +
                "Xref: nntpd01.usenet.ninja misc.test:1\r\n"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _));
        var protector = AesGcmPostingTraceProtector.Create(
            new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey });
        var peer = IPAddress.Parse("192.0.2.10");
        var normalized = PostHeaderNormalizer.Normalize(
            parsed,
            Now,
            "nntpd01.usenet.ninja",
            ConnectionClientIdentity.Direct(new IPEndPoint(peer, 119)),
            NntpdOptions.DefaultMailComplaintsTo,
            protector);
        var text = Encoding.ASCII.GetString(normalized.Span);
        Assert.Contains("Date: " + date, text, StringComparison.Ordinal);
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Injection-Date: " + date, text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Date", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTP-Posting-Host", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3.4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("posting-host", text, StringComparison.Ordinal);
        Assert.Contains(
            "Injection-Info: nntpd01.usenet.ninja; logging-data=\"<ok@example.com>\"; mail-complaints-to=\""
            + NntpdOptions.DefaultMailComplaintsTo + "\"",
            text,
            StringComparison.Ordinal);
        var xtraceStart = text.IndexOf("X-Trace: ", StringComparison.Ordinal);
        Assert.True(xtraceStart >= 0);
        var xtrace = text[(xtraceStart + "X-Trace: ".Length)..text.IndexOf("\r\n", xtraceStart, StringComparison.Ordinal)];
        Assert.StartsWith(AesGcmPostingTraceProtector.TokenPrefix, xtrace, StringComparison.Ordinal);
        Assert.NotEqual(Convert.ToBase64String(peer.GetAddressBytes()), xtrace);
        Assert.True(protector.TryUnprotect(xtrace, out var recovered));
        Assert.Equal(peer, recovered.Address);
        Assert.Equal(119, recovered.Port);
        Assert.Equal(Now, recovered.InjectedAtUtc);
        Assert.DoesNotContain("Path: client.path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Xref:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "Path:"));
        Assert.Equal(1, CountHeader(text, "Date: "));
        Assert.Equal(1, CountOccurrences(text, "Injection-Date:"));
        Assert.Equal(1, CountOccurrences(text, "X-Trace:"));
    }

    [Fact]
    public void Normalize_PreservesClientXPgpSig_AndDoesNotTreatItAsServerOwned()
    {
        var date = PostRfcDate.Format(Now);
        var parsed = Parse(Build(
            date: date,
            extraHeaders:
                "Control: cancel <original@example.com>\r\n" +
                "X-PGP-Sig: BouncyCastle Subject,Control,Message-ID,Date,From,Sender\r\n" +
                "\tiQCVAwUBNhiHnsJdOtO4janBAQGFFwP/SCsq1Isgw8DXHDaRkr0cdkZidVH41N6d\r\n"));
        Assert.True(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out _,
            controlCancelPermitted: true));
        var protector = AesGcmPostingTraceProtector.Create(
            new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey });
        var normalized = PostHeaderNormalizer.Normalize(
            parsed,
            Now,
            "nntpd01.usenet.ninja",
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 119)),
            NntpdOptions.DefaultMailComplaintsTo,
            protector);
        var text = Encoding.ASCII.GetString(normalized.Span);
        Assert.Contains("X-PGP-Sig: BouncyCastle Subject,Control,Message-ID,Date,From,Sender\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\tiQCVAwUBNhiHnsJdOtO4janBAQGFFwP/SCsq1Isgw8DXHDaRkr0cdkZidVH41N6d\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Path: .POSTED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Trace: ", text, StringComparison.Ordinal);
    }

    private static void AssertRejected(string article, PostingFailureCategory category)
    {
        var parsed = Parse(article);
        Assert.False(PostArticleValidator.TryValidate(
            parsed,
            Now,
            SyntaxOnlyNewsgroupPostingPolicy.Instance,
            out var failure));
        Assert.Equal(category, failure.Category);
    }

    private static ParsedPostArticle Parse(string article)
    {
        Assert.True(PostHeaderParser.TryParse(Encoding.ASCII.GetBytes(article), out var parsed, out var failure), failure.Detail);
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static string Build(
        string? date = "25 Sep 2026 12:00:00 +0000",
        string? from = "poster@example.com",
        string? newsgroups = "misc.test",
        string? subject = "test",
        string? messageId = "<ok@example.com>",
        string extraHeaders = "",
        string body = "body\r\n")
    {
        var sb = new StringBuilder();
        if (date is not null)
        {
            sb.Append("Date: ").Append(date).Append("\r\n");
        }

        if (from is not null)
        {
            sb.Append("From: ").Append(from).Append("\r\n");
        }

        if (newsgroups is not null)
        {
            sb.Append("Newsgroups: ").Append(newsgroups).Append("\r\n");
        }

        if (subject is not null)
        {
            sb.Append("Subject: ").Append(subject).Append("\r\n");
        }

        if (messageId is not null)
        {
            sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        }

        sb.Append(extraHeaders);
        sb.Append("\r\n");
        sb.Append(body);
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static int CountHeader(string text, string header)
    {
        var count = text.StartsWith(header, StringComparison.Ordinal) ? 1 : 0;
        return count + CountOccurrences(text, "\r\n" + header);
    }
}
