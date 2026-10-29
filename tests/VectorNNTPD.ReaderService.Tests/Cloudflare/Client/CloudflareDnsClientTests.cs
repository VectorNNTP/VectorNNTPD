using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Client;

public sealed class CloudflareDnsClientTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task ListRecordsAsync_ValidSinglePage_ReturnsRecords()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var client = CreateClient(handler);
        var records = await client.ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None);

        Assert.Single(records);
        Assert.Equal("1", records[0].Id);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRecordsAsync_FollowsPagination_AndFiltersExactName()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false },
                { "id": "x", "type": "A", "name": "other.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "2", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.3", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 2, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);

        var client = CreateClient(handler);
        var records = await client.ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(Fqdn, r.Name));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ListRecordsAsync_MissingResultInfo_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": [] }""");

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.False(ex.IsOutcomeUncertain);
        Assert.Contains("result_info", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRecordsAsync_MissingTotalPages_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [],
              "result_info": { "page": 1, "per_page": 100, "count": 0, "total_count": 0 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("total_pages", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListRecordsAsync_NonPositiveTotalPages_IsPermanentFailure(int totalPages)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            $$"""
            {
              "success": true,
              "result": [],
              "result_info": { "page": 1, "per_page": 100, "total_pages": {{totalPages}}, "count": 0, "total_count": 0 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("total_pages", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListRecordsAsync_TotalPagesChangesBetweenPages_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [ { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false } ],
              "result_info": { "page": 1, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [ { "id": "2", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false } ],
              "result_info": { "page": 2, "per_page": 1, "total_pages": 3, "count": 1, "total_count": 3 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("total_pages changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListRecordsAsync_PageMetadataMismatch_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [],
              "result_info": { "page": 9, "per_page": 100, "total_pages": 1, "count": 0, "total_count": 0 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("page does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListRecordsAsync_ExceedsMaxPages_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        var declaredTotal = CloudflareDnsClient.MaxListPages + 1;
        for (var page = 1; page <= CloudflareDnsClient.MaxListPages; page++)
        {
            handler.EnqueueJson(
                HttpStatusCode.OK,
                $$"""
                {
                  "success": true,
                  "result": [ { "id": "{{page}}", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.{{page}}", "ttl": 1, "proxied": false } ],
                  "result_info": { "page": {{page}}, "per_page": 1, "total_pages": {{declaredTotal}}, "count": 1, "total_count": {{declaredTotal}} }
                }
                """);
        }

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CloudflareDnsClient.MaxListPages.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Equal(CloudflareDnsClient.MaxListPages, handler.Requests.Count);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRecordsAsync_CancellationDuringPagination_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler
        {
            OnRequest = (_, requestIndex) =>
            {
                if (requestIndex >= 1)
                {
                    cts.Cancel();
                }
            },
        };
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [ { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false } ],
              "result_info": { "page": 1, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [ { "id": "2", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false } ],
              "result_info": { "page": 2, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", cts.Token));
    }

    [Fact]
    public async Task ListRecordsAsync_MatchingZoneId_IsAccepted()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false, "zone_id": "zone-test" }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var records = await CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None);

        Assert.Single(records);
        Assert.Equal(ZoneId, records[0].ZoneId);
    }

    [Fact]
    public async Task ListRecordsAsync_OmittedZoneId_IsAcceptedPerApiContract()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var records = await CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None);

        Assert.Single(records);
        Assert.Null(records[0].ZoneId);
    }

    [Fact]
    public async Task ListRecordsAsync_MismatchedZoneId_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false, "zone_id": "other-zone" }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("zone_id", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other-zone", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task ListRecordsAsync_EmptyOrWhitespaceZoneId_IsPermanentFailure(string zoneJson)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            $$"""
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false, "zone_id": {{zoneJson}} }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("zone_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRecordAsync_MismatchedZoneId_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": {
                "id": "new",
                "type": "A",
                "name": "nntpd01.usenet.ninja",
                "content": "198.18.0.1",
                "ttl": 1,
                "proxied": false,
                "zone_id": "other-zone"
              }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).CreateRecordAsync(
                ZoneId,
                new CloudflareDnsRecordWriteRequest
                {
                    Type = "A",
                    Name = Fqdn,
                    Content = "198.18.0.1",
                },
                CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("zone_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconcile_PaginationIntegrityFailure_DoesNotMutate()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": [ { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.99", "ttl": 1, "proxied": false } ] }""");

        var client = CreateClient(handler);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);
        var desired = new ResolvedBindAddresses([IPAddress.Parse("198.18.0.10")]);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Reconcile_ZoneMismatch_DoesNotMutate()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.99", "ttl": 1, "proxied": false, "zone_id": "other-zone" }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var client = CreateClient(handler);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);
        var desired = new ResolvedBindAddresses([IPAddress.Parse("198.18.0.10")]);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RemoveAll_PaginationIntegrityFailure_DoesNotMutate()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [ { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false } ],
              "result_info": { "page": 1, "per_page": 100, "count": 1 }
            }
            """);

        var client = CreateClient(handler);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Send_AddsBearerAuthorization_WithoutExposingKeyInExceptions()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.Unauthorized,
            """
            { "success": false, "errors": [ { "code": 10000, "message": "Authentication error" } ], "result": null }
            """);

        var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("Authentication error", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal(TestHostFactory.TestCloudFlareApiKey, handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Send_UnsuccessfulSuccessFlag_IsFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            { "success": false, "errors": [ { "code": 9109, "message": "Unauthorized to access requested resource" } ], "result": [] }
            """);

        var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.Contains("Unauthorized to access requested resource", ex.Message, StringComparison.Ordinal);
        Assert.Contains(9109, ex.CloudflareErrorCodes);
        Assert.True(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task Send_RateLimit_RetriesThenSucceeds()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":1,"message":"rate"}]}""", Encoding.UTF8, "application/json"),
            });
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            { "success": true, "result": [], "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 0, "total_count": 0 } }
            """);

        var client = CreateClient(handler);
        var records = await client.ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None);

        Assert.Empty(records);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CreateUpdateDelete_UseExpectedPaths()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false } }""");
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false } }""");
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new" } }""");

        var client = CreateClient(handler);
        var created = await client.CreateRecordAsync(
            ZoneId,
            new CloudflareDnsRecordWriteRequest
            {
                Type = "A",
                Name = Fqdn,
                Content = "198.18.0.1",
            },
            CancellationToken.None);
        Assert.Equal("new", created.Id);

        await client.UpdateRecordAsync(
            ZoneId,
            "new",
            new CloudflareDnsRecordWriteRequest
            {
                Type = "A",
                Name = Fqdn,
                Content = "198.18.0.2",
            },
            CancellationToken.None);

        await client.DeleteRecordAsync(ZoneId, "new", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.EndsWith($"/zones/{ZoneId}/dns_records", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith($"/zones/{ZoneId}/dns_records/new", handler.Requests[2].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_Cancellation_Propagates()
    {
        var handler = new ScriptedHttpMessageHandler
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        handler.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "result": [], "result_info": { "page": 1, "per_page": 100, "total_pages": 1 } }""");

        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", cts.Token));
    }

    [Fact]
    public void RequireTotalPages_RejectsNullAndInvalidMetadata()
    {
        Assert.Throws<CloudflareDnsException>(() => CloudflareDnsClient.RequireTotalPages(null, expectedPage: 1));
        Assert.Throws<CloudflareDnsException>(() =>
            CloudflareDnsClient.RequireTotalPages(new CloudflareResultInfo { TotalPages = null }, expectedPage: 1));
        Assert.Throws<CloudflareDnsException>(() =>
            CloudflareDnsClient.RequireTotalPages(new CloudflareResultInfo { TotalPages = 0 }, expectedPage: 1));
        Assert.Throws<CloudflareDnsException>(() =>
            CloudflareDnsClient.RequireTotalPages(new CloudflareResultInfo { TotalPages = 1, Page = 2 }, expectedPage: 1));

        Assert.Equal(3, CloudflareDnsClient.RequireTotalPages(new CloudflareResultInfo { TotalPages = 3, Page = 1 }, expectedPage: 1));
    }

    [Fact]
    public void EnsureRecordZoneMatches_AppliesOmissionAndMismatchPolicy()
    {
        CloudflareDnsClient.EnsureRecordZoneMatches(
            new CloudflareDnsRecord { Id = "1", ZoneId = null },
            ZoneId,
            "List");

        CloudflareDnsClient.EnsureRecordZoneMatches(
            new CloudflareDnsRecord { Id = "1", ZoneId = ZoneId },
            ZoneId,
            "List");

        var mismatch = Assert.Throws<CloudflareDnsException>(() =>
            CloudflareDnsClient.EnsureRecordZoneMatches(
                new CloudflareDnsRecord { Id = "1", ZoneId = "other" },
                ZoneId,
                "List"));
        Assert.True(mismatch.IsPermanentFailure);

        var empty = Assert.Throws<CloudflareDnsException>(() =>
            CloudflareDnsClient.EnsureRecordZoneMatches(
                new CloudflareDnsRecord { Id = "1", ZoneId = "  " },
                ZoneId,
                "List"));
        Assert.True(empty.IsPermanentFailure);
    }

    private static CloudflareDnsClient CreateClient(ScriptedHttpMessageHandler handler)
    {
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        };

        var options = Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions());
        return new CloudflareDnsClient(httpClient, options, NullLogger<CloudflareDnsClient>.Instance);
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        public Action<HttpRequestMessage, int>? OnRequest { get; set; }

        public void EnqueueJson(HttpStatusCode statusCode, string json) =>
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Headers.Authorization is { } auth)
            {
                copy.Headers.Authorization = auth;
            }

            Requests.Add(copy);
            OnRequest?.Invoke(request, Requests.Count - 1);

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            }

            return _responses.Dequeue();
        }
    }
}
