using System.Text;
using System.Text.Json;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;

namespace VectorNNTPD.ReaderService.Tests.RabbitMq.ArticleWork;

public sealed class ArticleWorkWireProtocolTests
{
    private static readonly Guid RequestId = Guid.Parse("7c1cb8a0-95f9-4c13-8e53-339773e3afaa");

    [Fact]
    public void SerializeRequestV1_IsCompactCanonicalJson_WithoutTransportFields()
    {
        var request = new ArticleWorkRequest(1, RequestId, "<12345@example.invalid>", "Giganews");
        var bytes = ArticleWorkWireProtocol.SerializeRequestV1(request);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""",
            json);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("CorrelationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplyTo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("correlationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("replyTo", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(RequestId, document.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal("<12345@example.invalid>", document.RootElement.GetProperty("messageId").GetString());
        Assert.Equal("Giganews", document.RootElement.GetProperty("backbone").GetString());
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
    }

    private const string SuccessFqdn = "backfiller01.usenet.ninja";
    private const int SuccessVatpPort = 119;
    private const string SuccessArticleIdHex = "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";

    [Fact]
    public void TryParseResponseV1_AcceptsCanonicalSuccess()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":119,"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"}"""u8;
        Assert.True(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.NotNull(response);
        Assert.Equal(ArticleWorkOutcome.Success, response.Outcome);
        Assert.Equal(RequestId, response.RequestId);
        Assert.Equal("<12345@example.invalid>", response.MessageId);
        Assert.Equal("Giganews", response.Backbone);
        Assert.Equal(SuccessFqdn, response.Fqdn);
        Assert.Equal(SuccessVatpPort, response.VatpPort);
        Assert.Equal(
            VectorNNTPD.Common.Articles.ArticleId.ParseLowerHex(SuccessArticleIdHex),
            response.ArticleId);
        Assert.Null(response.Error);
    }

    [Fact]
    public void TryParseResponseV1_RejectsSuccessWithoutArticleId()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":119}"""u8;
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("articleId", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseResponseV1_RejectsSuccessWithInvalidArticleId()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":119,"articleId":"NOT-HEX"}"""u8;
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("articleId", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nodots")]
    [InlineData("")]
    [InlineData("Backfiller01.usenet.ninja")]
    public void TryParseResponseV1_RejectsInvalidFqdn(string fqdn)
    {
        var json = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"{{fqdn}}","vatpPort":119,"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"}""");
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("fqdn", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void TryParseResponseV1_RejectsInvalidVatpPort(int port)
    {
        var json = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":{{port}},"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"}""");
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("vatpPort", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseResponseV1_AcceptsCanonicalArticleNotFound()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"ArticleNotFound","error":"No article with that message-id"}"""u8;
        Assert.True(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out _));
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, response!.Outcome);
        Assert.Null(response.Fqdn);
        Assert.Null(response.VatpPort);
        Assert.Null(response.ArticleId);
        Assert.Equal("No article with that message-id", response.Error);
    }

    [Fact]
    public void TryParseResponseV1_RejectsLegacyUriField()
    {
        var legacy = "vatp" + "://" + "backfiller01.usenet.ninja:119/" + SuccessArticleIdHex;
        var json = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":119,"articleId":"{{SuccessArticleIdHex}}","uri":"{{legacy}}"}""");
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("uri", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseResponseV1_RejectsUriOnlySuccess()
    {
        var legacy = "vatp" + "://" + "backfiller01.usenet.ninja:119/" + SuccessArticleIdHex;
        var json = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","uri":"{{legacy}}","articleId":"{{SuccessArticleIdHex}}"}""");
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("uri", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseResponseV1_RejectsSuccessWithError()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","vatpPort":119,"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14","error":"nope"}"""u8;
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("error", reason, StringComparison.Ordinal);
    }
}
