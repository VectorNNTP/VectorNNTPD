using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Cloudflare
{
    /// <summary>
    /// Locks Cloudflare DNS JSON request/response contracts used by <see cref="CloudflareDnsClient"/>.
    /// </summary>
    public sealed class CloudflareJsonContractTests
    {
        /// <summary>
        /// Asserts that a write request serializes every field with snake_case names and no omitted properties.
        /// </summary>
        [Fact]
        public void WriteRequest_Serialize_OmitsNoFields_AndUsesSnakeNames()
        {
            var json = JsonSerializer.Serialize(
                new CloudflareDnsRecordWriteRequest
                {
                    Type = "A",
                    Name = "nntpd01.usenet.ninja",
                    Content = "1.2.3.4",
                    Ttl = 120,
                    Proxied = false,
                },
                CloudflareJsonSerializerContext.Default.CloudflareDnsRecordWriteRequest);

            Assert.Equal(
                """{"type":"A","name":"nntpd01.usenet.ninja","content":"1.2.3.4","ttl":120,"proxied":false}""",
                json);
        }

        /// <summary>
        /// Asserts that the closed list-response metadata deserializes success, the record id, TTL, proxied flag, and result-info page count.
        /// </summary>
        [Fact]
        public void ListResponse_Deserialize_ClosedGeneric()
        {
            const string payload =
                """
                {
                  "success": true,
                  "errors": [],
                  "messages": [],
                  "result": [
                    {
                      "id": "rec-1",
                      "type": "A",
                      "name": "nntpd01.usenet.ninja",
                      "content": "1.2.3.4",
                      "ttl": 120,
                      "proxied": false,
                      "zone_id": "zone-1"
                    }
                  ],
                  "result_info": {
                    "page": 1,
                    "per_page": 100,
                    "total_pages": 1,
                    "count": 1,
                    "total_count": 1
                  }
                }
                """;

            var envelope = JsonSerializer.Deserialize(
                payload,
                CloudflareJsonSerializerContext.Default.CloudflareApiResponseListCloudflareDnsRecord);

            Assert.NotNull(envelope);
            Assert.True(envelope.Success);
            var record = Assert.Single(envelope.Result!);
            Assert.Equal("rec-1", record.Id);
            Assert.Equal(120, record.Ttl);
            Assert.False(record.Proxied);
            Assert.Equal(1, envelope.ResultInfo!.TotalPages);
        }

        /// <summary>
        /// Asserts that a single-record envelope accepts a case-insensitive success flag and record type, and leaves a missing proxied value null.
        /// </summary>
        [Fact]
        public void RecordResponse_Deserialize_ClosedGeneric()
        {
            const string payload =
                """
                {
                  "Success": true,
                  "errors": [],
                  "messages": [],
                  "result": {
                    "id": "rec-2",
                    "TYPE": "TXT",
                    "name": "_acme-challenge.example",
                    "content": "token",
                    "ttl": 60
                  }
                }
                """;

            var envelope = JsonSerializer.Deserialize(
                payload,
                CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDnsRecord);

            Assert.NotNull(envelope);
            Assert.True(envelope.Success);
            Assert.NotNull(envelope.Result);
            Assert.Equal("rec-2", envelope.Result.Id);
            Assert.Equal("TXT", envelope.Result.Type);
            Assert.Null(envelope.Result.Proxied);
        }

        /// <summary>
        /// Asserts that a delete envelope deserializes success and the deleted record id.
        /// </summary>
        [Fact]
        public void DeleteResponse_Deserialize_ClosedGeneric()
        {
            const string payload =
                """
                {
                  "success": true,
                  "errors": [],
                  "messages": [],
                  "result": { "id": "rec-del" }
                }
                """;

            var envelope = JsonSerializer.Deserialize(
                payload,
                CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDeleteResult);

            Assert.NotNull(envelope);
            Assert.True(envelope.Success);
            Assert.Equal("rec-del", envelope.Result!.Id);
        }

        /// <summary>
        /// Asserts that a failed envelope keeps a null result and preserves the error code and message.
        /// </summary>
        [Fact]
        public void ErrorResponse_Deserialize_PreservesCodes()
        {
            const string payload =
                """
                {
                  "success": false,
                  "errors": [ { "code": 10000, "message": "Authentication error" } ],
                  "messages": [],
                  "result": null
                }
                """;

            var envelope = JsonSerializer.Deserialize(
                payload,
                CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDnsRecord);

            Assert.NotNull(envelope);
            Assert.False(envelope.Success);
            Assert.Null(envelope.Result);
            var error = Assert.Single(envelope.Errors);
            Assert.Equal(10000, error.Code);
            Assert.Equal("Authentication error", error.Message);
        }

        /// <summary>
        /// Asserts that <see cref="CloudflareDnsClient.CreateRecordAsync"/> sends the source-generated snake_case body and returns the created record id.
        /// </summary>
        /// <returns>A task that completes when the request body and created id are asserted.</returns>
        [Fact]
        public async Task Client_CreateRecord_UsesAotSafeRequestBody()
        {
            string? body = null;
            var handler = new CaptureHandler(async (request, cancellationToken) =>
            {
                body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "success": true,
                          "errors": [],
                          "messages": [],
                          "result": {
                            "id": "created",
                            "type": "A",
                            "name": "nntpd01.usenet.ninja",
                            "content": "1.2.3.4",
                            "ttl": 120,
                            "proxied": false,
                            "zone_id": "zone-1"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            });

            var client = CreateClient(handler);
            var created = await client.CreateRecordAsync(
                "zone-1",
                new CloudflareDnsRecordWriteRequest
                {
                    Type = CloudflareDnsRecordTypes.A,
                    Name = "nntpd01.usenet.ninja",
                    Content = "1.2.3.4",
                    Ttl = 120,
                    Proxied = false,
                },
                CancellationToken.None);

            Assert.Equal("created", created.Id);
            Assert.Equal(
                """{"type":"A","name":"nntpd01.usenet.ninja","content":"1.2.3.4","ttl":120,"proxied":false}""",
                body);
        }

        /// <summary>
        /// Asserts that list and delete use the closed JSON responses and that the scripted handler observes both requests.
        /// </summary>
        /// <returns>A task that completes when the listed id and request count are asserted.</returns>
        [Fact]
        public async Task Client_ListAndDelete_DeserializeClosedResponses()
        {
            var handler = new ScriptedHandler(
            [
                """
                {
                  "success": true,
                  "errors": [],
                  "messages": [],
                  "result": [
                    {
                      "id": "txt-1",
                      "type": "TXT",
                      "name": "_acme-challenge.nntpd01.usenet.ninja",
                      "content": "v",
                      "ttl": 60,
                      "proxied": false,
                      "zone_id": "zone-1"
                    }
                  ],
                  "result_info": {
                    "page": 1,
                    "per_page": 100,
                    "total_pages": 1,
                    "count": 1,
                    "total_count": 1
                  }
                }
                """,
                """
                {
                  "success": true,
                  "errors": [],
                  "messages": [],
                  "result": { "id": "txt-1" }
                }
                """,
            ]);

            var client = CreateClient(handler);
            var listed = await client.ListRecordsAsync(
                "zone-1",
                "_acme-challenge.nntpd01.usenet.ninja",
                CloudflareDnsRecordTypes.TXT,
                CancellationToken.None);
            Assert.Equal("txt-1", Assert.Single(listed).Id);

            await client.DeleteRecordAsync("zone-1", "txt-1", CancellationToken.None);
            Assert.Equal(2, handler.RequestCount);
        }

        /// <summary>
        /// Builds a <see cref="CloudflareDnsClient"/> against the Cloudflare v4 base address with a test API token.
        /// </summary>
        /// <param name="handler">Handler that supplies scripted HTTP responses.</param>
        /// <returns>The client under test. The caller owns the handler through the client.</returns>
        private static CloudflareDnsClient CreateClient(HttpMessageHandler handler)
        {
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            };
            return new CloudflareDnsClient(
                http,
                Options.Create(new AcmeCloudflareOptions { CloudFlareApiKey = "test-token" }),
                NullLogger<CloudflareDnsClient>.Instance);
        }

        /// <summary>
        /// HTTP handler that forwards <see cref="SendAsync"/> to a caller-supplied callback so the test can read the request body.
        /// </summary>
        private sealed class CaptureHandler : HttpMessageHandler
        {
            /// <summary>Callback invoked for each request.</summary>
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            /// <summary>
            /// Stores <paramref name="handler"/> as the send callback.
            /// </summary>
            /// <param name="handler">Callback that reads the request and returns the response.</param>
            public CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
                _handler = handler;

            /// <summary>
            /// Forwards <paramref name="request"/> and <paramref name="cancellationToken"/> to the stored callback.
            /// </summary>
            /// <param name="request">Request whose body the test captures.</param>
            /// <param name="cancellationToken">Token forwarded to the callback.</param>
            /// <returns>The response returned by the callback.</returns>
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                _handler(request, cancellationToken);
        }

        /// <summary>
        /// HTTP handler that replies to each request with the next queued JSON payload and counts the requests.
        /// </summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            /// <summary>JSON payloads returned in order, one per request.</summary>
            private readonly Queue<string> _payloads;

            /// <summary>
            /// Copies <paramref name="payloads"/> into the response queue.
            /// </summary>
            /// <param name="payloads">JSON bodies, in the order requests will arrive.</param>
            public ScriptedHandler(IEnumerable<string> payloads) =>
                _payloads = new Queue<string>(payloads);

            /// <summary>Number of requests observed, including a request that then finds the queue empty.</summary>
            public int RequestCount { get; private set; }

            /// <summary>
            /// Increments <see cref="RequestCount"/> and returns HTTP 200 with the next queued JSON body. The request and cancellation token are not inspected.
            /// </summary>
            /// <param name="request">Incoming request. The contents are ignored.</param>
            /// <param name="cancellationToken">Cancellation token. It is not observed.</param>
            /// <returns>A completed task containing the next scripted response.</returns>
            /// <exception cref="InvalidOperationException">Thrown when the payload queue is empty.</exception>
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                var payload = _payloads.Dequeue();
                // codeql[cs/local-not-disposed] HttpResponseMessage ownership transfers to HttpClient; the caller disposes the response.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
