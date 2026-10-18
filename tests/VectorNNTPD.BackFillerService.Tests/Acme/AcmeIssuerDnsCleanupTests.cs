using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Acme
{
    /// <summary>
    /// DNS cleanup after ACME failures does not mask the original exception.
    /// </summary>

    public sealed class AcmeIssuerDnsCleanupTests
    {
        /// <summary>ACME directory URL passed to scripted HTTP responses.</summary>
        private const string DirectoryUrl = "http://acme.test/directory";

        /// <summary>
        /// Contract: Cancelled cleanup does not replace the original acme exception.
        /// </summary>

        [Fact]
        public async Task Cancelled_cleanup_does_not_replace_the_original_acme_exception()
        {
            using var cts = new CancellationTokenSource();
            var dns = new RecordingDnsClient();
            var logger = new CollectingLogger<AcmeIssuer>();
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ =>
                {
                    cts.Cancel();
                    throw new AcmeChallengeException("propagation_failed", "injected");
                }),
                logger);

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                scope.Issuer.IssueAsync(["example.com"], cts.Token));

            Assert.Equal("propagation_failed", ex.Category);
            Assert.Equal(0, dns.DeleteCalls);
            AssertResponsesDisposed(scope);
        }

        /// <summary>
        /// Contract: Cleanup failure does not replace the original acme exception.
        /// </summary>

        [Fact]
        public async Task Cleanup_failure_does_not_replace_the_original_acme_exception()
        {
            var dns = new RecordingDnsClient
            {
                DeleteException = new InvalidOperationException("delete fault"),
            };
            var logger = new CollectingLogger<AcmeIssuer>();
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ => throw new AcmeChallengeException("propagation_failed", "injected")),
                logger);

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                scope.Issuer.IssueAsync(["example.com"], CancellationToken.None));

            Assert.Equal("propagation_failed", ex.Category);
            Assert.Equal(1, dns.DeleteCalls);
            Assert.DoesNotContain("txt_cleanup_failed", ex.Message, StringComparison.Ordinal);
            AssertResponsesDisposed(scope);
        }

        /// <summary>
        /// Contract: Cancelled issuance still throws cancellation after cleanup.
        /// </summary>

        [Fact]
        public async Task Cancelled_issuance_still_throws_cancellation_after_cleanup()
        {
            using var cts = new CancellationTokenSource();
            var dns = new RecordingDnsClient
            {
                OnCreate = () => cts.Cancel(),
            };
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ => throw new InvalidOperationException("lookup should not run")),
                NullLogger<AcmeIssuer>.Instance);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                scope.Issuer.IssueAsync(["example.com"], cts.Token));
            Assert.Equal(1, dns.DeleteCalls);
            AssertResponsesDisposed(scope);
        }

        /// <summary>
        /// Provisions a temp account store, scripted ACME HTTP, and an issuer wired to the supplied DNS and TXT doubles.
        /// </summary>
        /// <param name="dns">Cloudflare DNS double whose create/delete behavior drives cleanup scenarios.</param>
        /// <param name="resolver">Authoritative TXT lookup used during DNS-01 propagation.</param>
        /// <param name="logger">Logger captured for warning assertions.</param>
        private static IssuerScope CreateIssuer(
            RecordingDnsClient dns,
            IAuthoritativeTxtResolver resolver,
            ILogger<AcmeIssuer> logger)
        {
            var stateDir = Directory.CreateTempSubdirectory("acme-issuer-cleanup-").FullName;
            using var rsa = RSA.Create(2048);
            var store = new AccountStore(stateDir);
            store.Save(new AcmeAccountState(
                "http://acme.test/acct/1",
                DirectoryUrl,
                rsa.ExportPkcs8PrivateKey(),
                "{}"));
            var http = new ScriptedAcmeHttpFactory();

            return new IssuerScope(
                stateDir,
                http,
                new AcmeIssuer(
                Options.Create(new AcmeCloudflareOptions
                {
                    AcmeDirectoryUrl = DirectoryUrl,
                    AcmeCertificatePassword = "pw",
                    AcmeEmail = "ops@example.com",
                    AcmeRenewalThresholdDays = 30,
                }),
                store,
                new Dns01Solver(dns, "zone", resolver, "example.com", propagationTimeout: TimeSpan.FromSeconds(2)),
                http,
                logger));
        }

        /// <summary>
        /// Asserts every scripted ACME HTTP response was disposed after issuance completes or fails.
        /// </summary>
        /// <param name="scope">Issuer scope whose HTTP factory recorded responses.</param>
        private static void AssertResponsesDisposed(IssuerScope scope)
        {
            Assert.NotEmpty(scope.Http.Responses);
            Assert.All(scope.Http.Responses, static response => Assert.True(response.WasDisposed));
        }

        /// <summary>Owns a temp ACME state directory and exposes the issuer plus scripted HTTP for disposal checks.</summary>
        private sealed class IssuerScope(string stateDir, ScriptedAcmeHttpFactory http, AcmeIssuer issuer) : IDisposable
        {
            /// <summary>
            /// Issuer under test for DNS cleanup behavior.
            /// </summary>

            public AcmeIssuer Issuer { get; } = issuer;

            /// <summary>
            /// Factory that served ACME HTTP and tracked response disposal.
            /// </summary>

            public ScriptedAcmeHttpFactory Http { get; } = http;

            /// <summary>
            /// Deletes the temp ACME state directory created for the scope.
            /// </summary>

            public void Dispose() => Directory.Delete(stateDir, recursive: true);
        }

        /// <summary>Returns canned TXT answers from a test delegate for DNS-01 propagation checks.</summary>
        private sealed class ScriptedResolver(Func<CancellationToken, IReadOnlyList<string>> lookup) : IAuthoritativeTxtResolver
        {
            /// <summary>
            /// Returns TXT strings from the configured delegate without performing real DNS.
            /// </summary>
            /// <param name="name">Queried DNS name (unused by the delegate wiring).</param>
            /// <param name="cancellationToken">Token forwarded to the lookup delegate.</param>
            public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken) =>
                Task.FromResult(lookup(cancellationToken));
        }

        /// <summary>HttpClientFactory wired to a single in-memory ACME handler that records responses for disposal tests.</summary>
        private sealed class ScriptedAcmeHttpFactory : IHttpClientFactory
        {
            /// <summary>
            /// In-memory ACME handler that records each response for disposal checks.
            /// </summary>

            private readonly ScriptedAcmeHandler _handler = new();

            /// <summary>Tracked HTTP responses created by the scripted handler.</summary>
            internal IReadOnlyList<ScriptedAcmeHandler.TrackedResponse> Responses => _handler.Responses;

            /// <summary>
            /// Returns an HTTP client bound to the shared scripted handler without disposing it with the client.
            /// </summary>
            /// <param name="name">Named client key (ignored).</param>
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        /// <summary>Minimal ACME directory/order/authz JSON responses; tracks HttpResponseMessage disposal.</summary>
        private sealed class ScriptedAcmeHandler : HttpMessageHandler
        {
            /// <summary>
            /// Responses handed to ACME clients; guarded for concurrent SendAsync calls.
            /// </summary>

            private readonly List<TrackedResponse> _responses = [];

            /// <summary>Snapshot of responses returned to ACME clients (thread-safe copy).</summary>
            internal IReadOnlyList<TrackedResponse> Responses
            {
                get
                {
                    lock (_responses)
                    {
                        return [.. _responses];
                    }
                }
            }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult<HttpResponseMessage>(CreateResponse(request));

            /// <summary>
            /// Builds canned directory/order/authz JSON (or empty HEAD body) and registers the response for tracking.
            /// </summary>
            /// <param name="request">Incoming ACME HTTP request.</param>
            private TrackedResponse CreateResponse(HttpRequestMessage request)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var response = new TrackedResponse(HttpStatusCode.OK);
                lock (_responses)
                {
                    _responses.Add(response);
                }

                response.Headers.TryAddWithoutValidation("Replay-Nonce", "nonce");
                if (request.Method == HttpMethod.Head)
                {
                    response.Content = new ByteArrayContent([]);
                    return response;
                }

                var json = path.EndsWith("/directory", StringComparison.Ordinal)
                    ? """
                      {"newNonce":"http://acme.test/nonce","newAccount":"http://acme.test/new-account","newOrder":"http://acme.test/new-order"}
                      """
                    : path.EndsWith("/new-order", StringComparison.Ordinal)
                        ? """
                          {"status":"pending","authorizations":["http://acme.test/authz/1"],"finalize":"http://acme.test/order/1/finalize","identifiers":[{"type":"dns","value":"example.com"}]}
                          """
                        : path.EndsWith("/authz/1", StringComparison.Ordinal)
                            ? """
                              {"status":"pending","identifier":{"type":"dns","value":"example.com"},"challenges":[{"type":"dns-01","url":"http://acme.test/chall/1","token":"tok"}]}
                              """
                            : "{}";
                if (path.EndsWith("/new-order", StringComparison.Ordinal))
                {
                    response.StatusCode = HttpStatusCode.Created;
                    response.Headers.Location = new Uri("http://acme.test/order/1");
                }

                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return response;
            }

            /// <summary>HttpResponseMessage that records whether Dispose was called.</summary>
            internal sealed class TrackedResponse : HttpResponseMessage
            {
                /// <summary>Creates a tracked response with the given status.</summary>
                /// <param name="statusCode">Initial HTTP status.</param>
                internal TrackedResponse(HttpStatusCode statusCode)
                    : base(statusCode)
                {
                }

                /// <summary>
                /// Whether <see cref="HttpResponseMessage.Dispose"/> was invoked on this instance.
                /// </summary>

                public bool WasDisposed { get; private set; }

                /// <inheritdoc />
                protected override void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        WasDisposed = true;
                    }

                    base.Dispose(disposing);
                }
            }
        }

        /// <summary>Cloudflare DNS client double that counts deletes and can throw or hook create for cleanup tests.</summary>
        private sealed class RecordingDnsClient : ICloudflareDnsClient
        {
            /// <summary>
            /// Number of times delete was invoked during issuance cleanup.
            /// </summary>

            public int DeleteCalls { get; private set; }

            /// <summary>
            /// When set, thrown from delete after incrementing <see cref="DeleteCalls"/>.
            /// </summary>

            public Exception? DeleteException { get; init; }

            /// <summary>
            /// Optional hook invoked when a DNS record is created (for cancellation during create).
            /// </summary>

            public Action? OnCreate { get; init; }

            /// <summary>
            /// Always fails because list operations are outside DNS cleanup scenarios under test.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="type">DNS record type (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("list is not used by this test");

            /// <summary>
            /// Always fails because list operations are outside DNS cleanup scenarios under test.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("list is not used by this test");

            /// <summary>
            /// Returns a synthetic record and invokes <see cref="OnCreate"/> when configured.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="request">TXT record payload for DNS-01.</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken)
            {
                OnCreate?.Invoke();
                return Task.FromResult(new CloudflareDnsRecord
                {
                    Id = "rec-1",
                    Type = request.Type,
                    Name = request.Name,
                    Content = request.Content,
                });
            }

            /// <summary>
            /// Always fails because updates are outside DNS cleanup scenarios under test.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Existing record identifier (unused).</param>
            /// <param name="request">Updated record payload (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("update is not used by this test");

            /// <summary>
            /// Increments the delete counter and optionally throws <see cref="DeleteException"/>.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Record to delete (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken)
            {
                DeleteCalls++;
                if (DeleteException is not null)
                {
                    throw DeleteException;
                }

                return Task.CompletedTask;
            }
        }
    }
}
