using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.RabbitMq.ArticleWork;

namespace VectorNNTPD.TransitService.Tests.RabbitMq.ArticleWork;

public sealed class ArticleWorkRpcResponseRouterTests
{
    private const string MessageId = "<12345@example.invalid>";
    private const string SuccessFqdn = "backfiller01.usenet.ninja";
    private const int SuccessVatpPort = 119;
    private const string SuccessArticleIdHex = "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";

    [Fact]
    public void StaleGeneration_CannotCompleteNewerGenerationRequest()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "cache.requests", generation: 2);

        router.Dispatch(correlationId, SuccessBody(requestId), requestId.ToString("D"), deliveryGeneration: 1);
        Assert.False(operation.IsCompleted);

        Assert.Equal(1, router.OutstandingCount);
        router.Dispatch(correlationId, SuccessBody(requestId), requestId.ToString("D"), deliveryGeneration: 2);
        Assert.True(operation.TryGetResult(out var result));
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("cache.requests", result.SourceExchange);
        Assert.Equal(0, router.OutstandingCount);
    }

    [Fact]
    public void Success_RemovesAllLocalCorrelationsImmediately()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        router.Register(first, operation, "cache.requests", generation: 0);
        router.Register(second, operation, "backfiller.eweka", generation: 0);
        Assert.Equal(2, router.OutstandingCount);

        router.Dispatch(first, SuccessBody(requestId), requestId.ToString("D"), deliveryGeneration: 0);
        Assert.True(operation.TryGetResult(out var result));
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(0, router.OutstandingCount);
    }

    [Fact]
    public void ResponseRacingWithLocalCompletion_CannotResurrectLookup()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "cache.requests", generation: 0);

        operation.TryComplete(ArticleWorkRpcResult.NotFound(requestId, MessageId, "deadline"));
        router.UnregisterAll(operation);
        router.Register(Guid.NewGuid().ToString("D"), operation, "backfiller.eweka", generation: 0);
        Assert.Equal(0, router.OutstandingCount);

        router.Dispatch(correlationId, SuccessBody(requestId), requestId.ToString("D"), deliveryGeneration: 0);
        Assert.True(operation.TryGetResult(out var result));
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(0, router.OutstandingCount);
    }

    [Fact]
    public void ConcurrentSuccess_CompletesExactlyOnce_AndClearsCorrelations()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        var third = Guid.NewGuid().ToString("D");
        router.Register(first, operation, "backfiller.eweka", generation: 0);
        router.Register(second, operation, "backfiller.giganews", generation: 0);
        router.Register(third, operation, "backfiller.abavia", generation: 0);

        Parallel.Invoke(
            () => router.Dispatch(first, SuccessBody(requestId, "Eweka"), requestId.ToString("D"), 0),
            () => router.Dispatch(second, SuccessBody(requestId, "Giganews"), requestId.ToString("D"), 0),
            () => router.Dispatch(third, SuccessBody(requestId, "Abavia"), requestId.ToString("D"), 0));

        Assert.True(operation.TryGetResult(out var result));
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.True(
            result.SourceExchange is "backfiller.eweka" or "backfiller.giganews" or "backfiller.abavia",
            result.SourceExchange);
        Assert.Equal(0, router.OutstandingCount);
    }

    [Fact]
    public void MismatchedAmqpRequestId_IsIgnored()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "cache.requests", generation: 0);

        router.Dispatch(correlationId, SuccessBody(requestId), Guid.NewGuid().ToString("D"), deliveryGeneration: 0);
        Assert.False(operation.IsCompleted);
        Assert.Equal(1, router.OutstandingCount);
    }

    [Fact]
    public void MissingAmqpRequestId_IsIgnored()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "cache.requests", generation: 0);

        router.Dispatch(correlationId, SuccessBody(requestId), amqpRequestId: null, deliveryGeneration: 0);
        Assert.False(operation.IsCompleted);
    }

    [Fact]
    public void JsonRequestIdMismatch_IsIgnoredEvenWhenAmqpMatchesAnotherValue()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var otherRequestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "cache.requests", generation: 0);

        router.Dispatch(correlationId, SuccessBody(otherRequestId), requestId.ToString("D"), deliveryGeneration: 0);
        Assert.False(operation.IsCompleted);
    }

    [Fact]
    public void ArticleNotFound_CompletesAttempt_ButIsNotLookupSuccess()
    {
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, MessageId);
        var correlationId = Guid.NewGuid().ToString("D");
        router.Register(correlationId, operation, "backfiller.giganews", generation: 0);

        router.Dispatch(correlationId, FailureBody(requestId, "ArticleNotFound"), requestId.ToString("D"), 0);
        Assert.True(operation.IsCompleted);
        Assert.True(operation.TryGetResult(out var result));
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.True(ArticleWorkAggregatePolicy.IsAttemptTerminal(ArticleWorkOutcome.ArticleNotFound));
        Assert.False(ArticleWorkAggregatePolicy.ShouldTryNextBackbone(ArticleWorkOutcome.ArticleNotFound));
        Assert.False(ArticleWorkAggregatePolicy.IsLookupSuccess(ArticleWorkOutcome.ArticleNotFound));
        Assert.True(ArticleWorkAggregatePolicy.IsLookupSuccess(ArticleWorkOutcome.Success));
    }

    private static byte[] SuccessBody(Guid requestId, string backbone = "Storage")
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"{{MessageId}}","backbone":"{{backbone}}","outcome":"Success","fqdn":"{{SuccessFqdn}}","vatpPort":{{SuccessVatpPort}},"articleId":"{{SuccessArticleIdHex}}"}""");
    }

    private static byte[] FailureBody(Guid requestId, string outcome)
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"{{MessageId}}","backbone":"Storage","outcome":"{{outcome}}","error":"missing"}""");
    }
}
