using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

[Collection("NntpDbPostFilter")]
public sealed class MySqlPostFilterEvidenceAndLivePolicyTests
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;

    public MySqlPostFilterEvidenceAndLivePolicyTests(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    [NntpDbIntegrationFact]
    public async Task InsertRejection_PersistsMetadataAndPayload()
    {
        RequireReady();
        var messageId = "<ev-" + Guid.NewGuid().ToString("N") + "@example.com>";
        var payload = Encoding.UTF8.GetBytes("Message-ID: " + messageId + "\r\n\r\nbody\r\n");
        var evidence = new PostFilterRejectionEvidence(
            DateTimeOffset.UtcNow,
            policyRevision: 1,
            accountName: "poster",
            sourceAddress: IPAddress.Parse("198.51.100.10"),
            artType: ArticleType.Default,
            messageId: messageId,
            articleSize: payload.Length,
            stage: PostFilterStage.Deny,
            reason: "denied",
            spamAssassinStatus: null,
            spamAssassinScore: null,
            spamAssassinThreshold: null,
            articlePayload: payload);
        await _fixture.InsertRejectionAsync(evidence);

        var count = await _fixture.ExecuteRawScalarAsync(
            "SELECT COUNT(*) FROM nntppostfilterrejections WHERE message_id = @id AND reason = 'denied'",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(1L, Convert.ToInt64(count, CultureInfo.InvariantCulture));

        var storedName = await _fixture.ExecuteRawScalarAsync(
            "SELECT account_name FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(PostFilterAccountIdentity.FromUsername("poster"), Convert.ToString(storedName));
        Assert.NotEqual("poster", Convert.ToString(storedName));

        var storedType = await _fixture.ExecuteRawScalarAsync(
            "SELECT art_type FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal((uint)ArticleType.Default, Convert.ToUInt32(storedType, CultureInfo.InvariantCulture));

        var storedStage = await _fixture.ExecuteRawScalarAsync(
            "SELECT stage FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(nameof(PostFilterStage.Deny), Convert.ToString(storedStage));

        var storedPayload = await _fixture.ExecuteRawScalarAsync(
            "SELECT article_payload FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(payload, Assert.IsType<byte[]>(storedPayload));
    }

    [NntpDbIntegrationFact]
    public async Task InsertRejection_AllowsNullPayload()
    {
        RequireReady();
        var evidence = new PostFilterRejectionEvidence(
            DateTimeOffset.UtcNow,
            policyRevision: 1,
            accountName: null,
            sourceAddress: IPAddress.Loopback,
            artType: ArticleType.None,
            messageId: null,
            articleSize: 0,
            stage: PostFilterStage.Gate,
            reason: "closed",
            spamAssassinStatus: null,
            spamAssassinScore: null,
            spamAssassinThreshold: null,
            articlePayload: null);
        await _fixture.InsertRejectionAsync(evidence);
        var storedPayload = await _fixture.ExecuteRawScalarAsync(
            "SELECT article_payload FROM nntppostfilterrejections WHERE reason = 'closed' AND account_name IS NULL");
        Assert.True(storedPayload is null or DBNull);
    }

    [NntpDbIntegrationFact]
    public async Task PublishLiveActiveSpamAssassinRevision()
    {
        RequireReady();
        var revision = _fixture.NextPersistentRevision();
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                MaxArticleSize = 131072,
                Hosts = ["198.18.0.70"],
                ExcludeArtTypes = ["YEncoded"],
            },
        };
        await _fixture.PublishOptionsAsync(revision, options);
        _fixture.RememberPublishedRevision(revision);

        var record = await _fixture.LoadAsync();
        Assert.Equal(revision, record.Revision);
        Assert.Equal(PostFilterGateState.Active, record.Options.Gate);
        Assert.True(record.Options.SpamAssassin.Enabled);
        Assert.Equal(131072, record.Options.SpamAssassin.MaxArticleSize);
        Assert.Equal(["198.18.0.70"], record.Options.SpamAssassin.Hosts);
        Assert.Equal(["YEncoded"], record.Options.SpamAssassin.ExcludeArtTypes);

        await using var service = new PostFilterPolicyService(
            _fixture.Repository!,
            NullLogger<PostFilterPolicyService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(revision, service.Current.Revision);
        Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        Assert.True(service.Current.SpamAssassinEnabled);
        Assert.Equal(131072, service.Current.SpamAssassinMaxArticleSize);
        Assert.Equal(["198.18.0.70"], service.Current.SpamAssassinHosts);
        Assert.Equal(ArticleType.YEncoded, service.Current.SpamAssassinExcludeArtTypes);
        await service.StopAsync(CancellationToken.None);
    }

    [NntpDbIntegrationFact]
    public async Task QueueService_WritesBinaryIpsAndPayload_ForPublishedRevision()
    {
        RequireReady();
        var revision = _fixture.NextPersistentRevision();
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            SpamAssassin = new PostFilterSpamAssassinOptions
            {
                Enabled = true,
                OnFailure = PostFilterSpamOnFailure.Reject,
                MaxArticleSize = 131072,
                Hosts = ["198.18.0.70"],
                ExcludeArtTypes = ["YEncoded"],
            },
        };
        await _fixture.PublishOptionsAsync(revision, options);
        _fixture.RememberPublishedRevision(revision);

        var suffix = Guid.NewGuid().ToString("N");
        var payload = Encoding.UTF8.GetBytes(
            "Message-ID: <bin-" + suffix + "@example.com>\r\nFrom: a@b\r\n\r\nbody\r\n");
        var v4Id = "<v4-" + suffix + "@example.com>";
        var v6Id = "<v6-" + suffix + "@example.com>";
        var mappedId = "<map-" + suffix + "@example.com>";

        var connectionString = NntpDbIntegration.TryGetConnectionString()
            ?? throw new InvalidOperationException(NntpDbIntegration.SkipReason);
        await using var nntpDb = new NntpDbService(
            new MySqlNntpDbConnectionFactory(),
            Options.Create(NntpDbIntegration.CreateOptions(connectionString)),
            NullLogger<NntpDbService>.Instance);
        await nntpDb.StartAsync(CancellationToken.None);
        var queue = new PostFilterRejectionEvidenceQueue();
        var writer = new PostFilterRejectionEvidenceService(
            nntpDb,
            queue,
            NullLogger<PostFilterRejectionEvidenceService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Evidence(revision, IPAddress.Parse("198.18.0.70"), v4Id, payload)));
        Assert.True(queue.TryEnqueue(Evidence(revision, IPAddress.Parse("2001:db8::1234"), v6Id, payload)));
        Assert.True(queue.TryEnqueue(
            Evidence(revision, IPAddress.Parse("::ffff:198.18.0.70"), mappedId, payload)));
        await WaitForAsync(() => queue.Written == 3);
        await writer.StopAsync(CancellationToken.None);
        await nntpDb.DisposeAsync();

        var current = Convert.ToInt64(
            await _fixture.ExecuteRawScalarAsync("SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1"),
            CultureInfo.InvariantCulture);
        Assert.Equal(revision, current);

        await AssertBinaryIpAsync(v4Id, "198.18.0.70", expectedLength: 4, revision, payload);
        await AssertBinaryIpAsync(v6Id, "2001:db8::1234", expectedLength: 16, revision, payload);
        await AssertBinaryIpAsync(mappedId, "198.18.0.70", expectedLength: 4, revision, payload);
        Assert.Equal(
            SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("198.18.0.70")),
            SourceAddressIdentity.ToNetworkBytes(IPAddress.Parse("::ffff:198.18.0.70")));
    }

    private async Task AssertBinaryIpAsync(
        string messageId,
        string expectedText,
        int expectedLength,
        long revision,
        byte[] payload)
    {
        var text = await _fixture.ExecuteRawScalarAsync(
            "SELECT INET6_NTOA(source_ip) FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(expectedText, Convert.ToString(text, CultureInfo.InvariantCulture));

        var length = await _fixture.ExecuteRawScalarAsync(
            "SELECT OCTET_LENGTH(source_ip) FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(expectedLength, Convert.ToInt32(length, CultureInfo.InvariantCulture));

        var storedRevision = await _fixture.ExecuteRawScalarAsync(
            "SELECT revision FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(revision, Convert.ToInt64(storedRevision, CultureInfo.InvariantCulture));

        var articleSize = await _fixture.ExecuteRawScalarAsync(
            "SELECT article_size FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        var payloadLength = await _fixture.ExecuteRawScalarAsync(
            "SELECT OCTET_LENGTH(article_payload) FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(payload.Length, Convert.ToInt32(articleSize, CultureInfo.InvariantCulture));
        Assert.Equal(payload.Length, Convert.ToInt32(payloadLength, CultureInfo.InvariantCulture));

        var storedPayload = await _fixture.ExecuteRawScalarAsync(
            "SELECT article_payload FROM nntppostfilterrejections WHERE message_id = @id",
            command => command.Parameters.AddWithValue("@id", messageId));
        Assert.Equal(payload, Assert.IsType<byte[]>(storedPayload));
    }

    private static PostFilterRejectionEvidence Evidence(
        long revision,
        IPAddress source,
        string messageId,
        byte[] payload) =>
        new(
            DateTimeOffset.UtcNow,
            revision,
            "poster",
            source,
            ArticleType.Default,
            messageId,
            payload.Length,
            PostFilterStage.Deny,
            "denied",
            spamAssassinStatus: null,
            spamAssassinScore: null,
            spamAssassinThreshold: null,
            payload);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private void RequireReady()
    {
        if (_fixture.IsConfigured)
        {
            return;
        }

        if (NntpDbIntegration.TryGetConnectionString() is null)
        {
            return;
        }

        Assert.Fail(_fixture.SkipReason ?? "PostFilter MySQL fixture is not configured.");
    }
}
