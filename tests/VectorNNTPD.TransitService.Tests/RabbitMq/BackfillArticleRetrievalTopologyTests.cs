using VectorNNTPD.TransitService.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.RabbitMq;

public sealed class BackfillArticleRetrievalTopologyTests
{
    private static readonly string[] ExpectedProviders =
    [
        "Abavia",
        "Altopia",
        "BaseIP",
        "Eweka",
        "Elbracht",
        "Giganews",
        "GTT",
        "Highwinds",
        "ItsHosted",
        "Novia",
        "UExpress",
        "UsenetNode1",
    ];

    [Fact]
    public void Providers_AreExactlyTheTwelveBackFillerIdentifiers()
    {
        Assert.Equal(ExpectedProviders, BackfillArticleRetrievalTopology.Providers);
        Assert.Equal(12, BackfillArticleRetrievalTopology.Definitions.Count);
        Assert.Equal(ExpectedProviders, BackfillArticleRetrievalTopology.Definitions.Select(static d => d.Provider));
        Assert.DoesNotContain("storage", BackfillArticleRetrievalTopology.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.requests", BackfillArticleRetrievalTopology.Providers, StringComparer.Ordinal);
        Assert.DoesNotContain(
            BackfillArticleRetrievalTopology.Definitions,
            static definition =>
                definition.ExchangeName == StorageArticleRetrievalTopology.EntityName
                || definition.QueueName == StorageArticleRetrievalTopology.EntityName
                || definition.ExchangeName.Contains("storage.requests", StringComparison.Ordinal)
                || definition.ExchangeName.Contains("grabbers.", StringComparison.Ordinal));
    }

    [Fact]
    public void Definitions_UseNormalizedBackFillerEntityNamesAndFanoutBinding()
    {
        foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
        {
            var entityName = $"backfiller.{definition.Provider.ToLowerInvariant()}";
            Assert.Equal(entityName, definition.ExchangeName);
            Assert.Equal(entityName, definition.QueueName);
            Assert.Equal(entityName, definition.RoutingKey);
            Assert.Equal(definition.ExchangeName, definition.QueueName);
            Assert.Equal(definition.QueueName, definition.RoutingKey);
            Assert.DoesNotContain("grabbers.", definition.ExchangeName, StringComparison.Ordinal);
            Assert.Equal("fanout", definition.ExchangeType);
            Assert.True(definition.ExchangeDurable);
            Assert.False(definition.ExchangeAutoDelete);
            Assert.True(definition.QueueDurable);
            Assert.False(definition.QueueExclusive);
            Assert.False(definition.QueueAutoDelete);
        }

        Assert.Equal("backfiller.abavia", BackfillArticleRetrievalTopology.BuildProviderEntityName("Abavia"));
        Assert.Equal("backfiller.abavia", BackfillArticleRetrievalTopology.BuildProviderEntityName("  Abavia  "));
        Assert.Equal("backfiller.abavia", BackfillArticleRetrievalTopology.BuildProviderEntityName("AbAvIa"));
        Assert.Equal("backfiller.gtt", BackfillArticleRetrievalTopology.BuildProviderEntityName("GTT"));
        Assert.Equal("backfiller.usenetnode1", BackfillArticleRetrievalTopology.BuildProviderEntityName("UsenetNode1"));
        Assert.Equal("backfiller.baseip", BackfillArticleRetrievalTopology.BuildProviderEntityName("BaseIP"));
        Assert.Equal("backfiller.itshosted", BackfillArticleRetrievalTopology.BuildProviderEntityName("ItsHosted"));
        Assert.Equal("backfiller.uexpress", BackfillArticleRetrievalTopology.BuildProviderEntityName("UExpress"));
    }

    [Fact]
    public void Definitions_DeclareQuorumQueuesWithQueueTypeArgument()
    {
        foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
        {
            Assert.NotNull(definition.QueueArguments);
            Assert.True(definition.QueueDurable);
            Assert.False(definition.QueueExclusive);
            Assert.False(definition.QueueAutoDelete);
            Assert.True(definition.QueueArguments.TryGetValue(
                BackfillArticleRetrievalTopology.QueueTypeArgumentName,
                out var queueType));
            Assert.Equal(BackfillArticleRetrievalTopology.QuorumQueueType, queueType);
            Assert.False(definition.QueueArguments.ContainsKey("x-message-ttl"));
            Assert.False(definition.QueueArguments.ContainsKey("x-expires"));
            Assert.Single(definition.QueueArguments);
        }
    }

    [Fact]
    public void BuildDefinitions_PreservesProviderCasing_AndNormalizesOnlyEntityNames()
    {
        var definition = Assert.Single(BackfillArticleRetrievalTopology.BuildDefinitions(["Giganews"]));
        Assert.Equal("Giganews", definition.Provider);
        Assert.Equal("backfiller.giganews", definition.ExchangeName);
        Assert.Equal("backfiller.giganews", definition.QueueName);
        Assert.Equal("backfiller.giganews", definition.RoutingKey);
    }

    [Fact]
    public void BuildDefinitions_NormalizesWhitespaceAndMixedCaseProviderNames()
    {
        var definition = Assert.Single(BackfillArticleRetrievalTopology.BuildDefinitions(["  Abavia  "]));
        Assert.Equal("Abavia", definition.Provider);
        Assert.Equal("backfiller.abavia", definition.ExchangeName);
        Assert.Equal("backfiller.abavia", definition.QueueName);
        Assert.Equal("backfiller.abavia", definition.RoutingKey);

        var mixed = Assert.Single(BackfillArticleRetrievalTopology.BuildDefinitions(["AbAvIa"]));
        Assert.Equal("AbAvIa", mixed.Provider);
        Assert.Equal("backfiller.abavia", mixed.ExchangeName);
    }

    [Fact]
    public void BuildDefinitions_SkipsBlanksAndDuplicateProviders()
    {
        var definitions = BackfillArticleRetrievalTopology.BuildDefinitions(
            ["Giganews", " ", "giganews", "Eweka", "Giganews"]);

        Assert.Equal(2, definitions.Count);
        Assert.Equal("Giganews", definitions[0].Provider);
        Assert.Equal("Eweka", definitions[1].Provider);
    }
}
