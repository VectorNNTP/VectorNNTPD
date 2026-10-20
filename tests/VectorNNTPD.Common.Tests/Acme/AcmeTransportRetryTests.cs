using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme.Protocol;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Regression coverage for ACME transport retry semantics: state-changing POSTs are not replayed
    /// after an ambiguous outcome; safe reads and newAccount may retry with a fresh JWS.
    /// </summary>
    public sealed class AcmeTransportRetryTests
    {
        private static readonly Uri DirectoryUri = new("https://acme.test/directory");
        private static readonly Uri NewNonceUri = new("https://acme.test/new-nonce");
        private static readonly Uri NewAccountUri = new("https://acme.test/new-account");
        private static readonly Uri NewOrderUri = new("https://acme.test/new-order");
        private static readonly Uri AccountUri = new("https://acme.test/acct/1");
        private static readonly Uri OrdersUri = new("https://acme.test/acct/1/orders");
        private static readonly Uri OrderUri = new("https://acme.test/order/1");
        private static readonly Uri FinalizeUri = new("https://acme.test/order/1/finalize");
        private static readonly Uri ChallengeUri = new("https://acme.test/chall/1");
        private static readonly Uri OrderResourceUri = new("https://acme.test/order/1");

        /// <summary>
        /// When newOrder is accepted but the response is lost, a second newOrder POST must not be sent.
        /// </summary>
        [Fact]
        public async Task NewOrder_ambiguous_outcome_does_not_submit_a_second_order()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => AccountWithOrdersResponse());
            handler.On(HttpMethod.Post, OrdersUri, _ => OrdersListResponse([]));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Contains("could not be identified reliably", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("-----BEGIN", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Expected transport failure while inspecting the orders list becomes the fallback diagnostic without a second newOrder.
        /// </summary>
        [Fact]
        public async Task NewOrder_orders_inspection_http_failure_uses_fallback_without_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => throw new HttpRequestException("account lookup failed"));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, new ImmediateDelayTimeProvider());
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Contains("orders list could not be inspected", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Caller cancellation during orders-list inspection propagates and is not converted to the diagnostic fallback.
        /// </summary>
        [Fact]
        public async Task NewOrder_orders_inspection_cancellation_propagates()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            using var cts = new CancellationTokenSource();
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    cts.Token));

            Assert.Equal(1, newOrderPosts);
        }

        /// <summary>
        /// Unexpected programming errors during inspection are not converted into the orders-list fallback message.
        /// </summary>
        [Fact]
        public async Task NewOrder_orders_inspection_unexpected_exception_propagates()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => throw new InvalidOperationException("unexpected bug"));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Equal("unexpected bug", ex.Message);
        }

        /// <summary>
        /// A single matching order is not adopted after ambiguous newOrder: it may be a pre-existing or concurrent order.
        /// </summary>
        [Fact]
        public async Task NewOrder_unique_matching_order_is_not_adopted()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => AccountWithOrdersResponse());
            handler.On(HttpMethod.Post, OrdersUri, _ => OrdersListResponse([OrderUri]));
            handler.On(HttpMethod.Post, OrderUri, _ => OrderPendingResponse("host.example.com"));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Contains("not attributable to this request", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Concurrent/pre-existing matching orders must not cause a second newOrder or silent adoption of either order.
        /// </summary>
        [Fact]
        public async Task NewOrder_multiple_matching_orders_fail_closed_without_replay()
        {
            var orderA = new Uri("https://acme.test/order/a");
            var orderB = new Uri("https://acme.test/order/b");
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => AccountWithOrdersResponse());
            handler.On(HttpMethod.Post, OrdersUri, _ => OrdersListResponse([orderA, orderB]));
            handler.On(HttpMethod.Post, orderA, _ => OrderPendingResponse("host.example.com"));
            handler.On(HttpMethod.Post, orderB, _ => OrderPendingResponse("host.example.com"));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Contains("multiple non-invalid orders", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// An incomplete/paginated orders list (Link rel=next) is not treated as a complete recovery basis.
        /// </summary>
        [Fact]
        public async Task NewOrder_paginated_orders_list_fail_closed_without_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, AccountUri, _ => AccountWithOrdersResponse());
            handler.On(HttpMethod.Post, OrdersUri, _ => OrdersListResponse([OrderUri], nextPage: new Uri("https://acme.test/acct/1/orders?cursor=2")));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
            Assert.Contains("incomplete", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ambiguous finalize polls the known order URL and does not POST finalize again when processing.
        /// </summary>
        [Fact]
        public async Task Finalize_ambiguous_outcome_polls_order_and_does_not_resubmit()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int finalizePosts = 0;
            handler.On(HttpMethod.Post, FinalizeUri, _ =>
            {
                finalizePosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, OrderResourceUri, _ => OrderProcessingResponse());

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeOrderResource order = await client.FinalizeOrderAsync(
                OrderResourceUri,
                FinalizeUri,
                [0x30, 0x03, 0x02, 0x01, 0x00],
                CancellationToken.None);

            Assert.Equal(1, finalizePosts);
            Assert.Equal(AcmeStatus.Processing, order.Status);
        }

        /// <summary>
        /// Order status ready after a lost finalize is not treated as rejection; finalize is not resubmitted while polling.
        /// </summary>
        [Fact]
        public async Task Finalize_ready_status_is_not_treated_as_rejection()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int finalizePosts = 0;
            int orderPolls = 0;
            handler.On(HttpMethod.Post, FinalizeUri, _ =>
            {
                finalizePosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, OrderResourceUri, _ =>
            {
                orderPolls++;
                return orderPolls < 3 ? OrderReadyResponse() : OrderProcessingResponse();
            });

            var time = new AdvancingDelayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, time);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeOrderResource order = await client.FinalizeOrderAsync(
                OrderResourceUri,
                FinalizeUri,
                [0x30, 0x03, 0x02, 0x01, 0x00],
                CancellationToken.None);

            Assert.Equal(1, finalizePosts);
            Assert.True(orderPolls >= 3);
            Assert.Equal(AcmeStatus.Processing, order.Status);
        }

        /// <summary>
        /// Exhausted finalize recovery while the order remains ready keeps the outcome explicitly ambiguous.
        /// </summary>
        [Fact]
        public async Task Finalize_exhausted_ready_polling_stays_ambiguous_without_resubmit()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int finalizePosts = 0;
            handler.On(HttpMethod.Post, FinalizeUri, _ =>
            {
                finalizePosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, OrderResourceUri, _ => OrderReadyResponse());

            var time = new AdvancingDelayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, time);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.FinalizeOrderAsync(
                    OrderResourceUri,
                    FinalizeUri,
                    [0x30, 0x03, 0x02, 0x01, 0x00],
                    CancellationToken.None));

            Assert.Equal(1, finalizePosts);
            Assert.Contains("recovery deadline", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ready", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Cancellation during finalize recovery stops polling and does not resubmit finalize.
        /// </summary>
        [Fact]
        public async Task Finalize_cancellation_during_recovery_does_not_resubmit()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int finalizePosts = 0;
            int orderPolls = 0;
            using var cts = new CancellationTokenSource();
            handler.On(HttpMethod.Post, FinalizeUri, _ =>
            {
                finalizePosts++;
                throw new HttpRequestException("response lost after accept");
            });
            handler.On(HttpMethod.Post, OrderResourceUri, _ =>
            {
                orderPolls++;
                if (orderPolls >= 2)
                {
                    cts.Cancel();
                }

                return OrderReadyResponse();
            });

            var time = new AdvancingDelayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, time);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.FinalizeOrderAsync(
                    OrderResourceUri,
                    FinalizeUri,
                    [0x30, 0x03, 0x02, 0x01, 0x00],
                    cts.Token));

            Assert.Equal(1, finalizePosts);
        }

        /// <summary>
        /// Ambiguous challenge POST recovers via challenge POST-as-GET when status is processing.
        /// </summary>
        [Fact]
        public async Task Challenge_ambiguous_outcome_recovers_without_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int challengePostsWithBody = 0;
            int challengeReads = 0;
            handler.On(HttpMethod.Post, ChallengeUri, request =>
            {
                string body = request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                // Challenge submit signs payload "{}"; POST-as-GET recovery uses an empty payload.
                if (IsEmptyObjectPayload(body))
                {
                    challengePostsWithBody++;
                    throw new HttpRequestException("response lost after accept");
                }

                challengeReads++;
                return ChallengeProcessingResponse();
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await client.SubmitChallengeAsync(ChallengeUri, CancellationToken.None);

            Assert.Equal(1, challengePostsWithBody);
            Assert.Equal(1, challengeReads);
        }

        /// <summary>
        /// Challenge POST-as-GET returning pending does not count as success and does not replay the submit.
        /// </summary>
        [Fact]
        public async Task Challenge_pending_after_ambiguous_stays_ambiguous_without_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int challengePostsWithBody = 0;
            handler.On(HttpMethod.Post, ChallengeUri, request =>
            {
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsEmptyObjectPayload(body))
                {
                    challengePostsWithBody++;
                    throw new HttpRequestException("response lost after accept");
                }

                return ChallengeStatusResponse("pending");
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeAmbiguousOutcomeException ex = await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.SubmitChallengeAsync(ChallengeUri, CancellationToken.None));

            Assert.Equal(1, challengePostsWithBody);
            Assert.Contains("pending", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Challenge POST-as-GET returning invalid is a confirmed CA rejection, not a successful recovery.
        /// </summary>
        [Fact]
        public async Task Challenge_invalid_after_ambiguous_is_confirmed_failure_without_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int challengePostsWithBody = 0;
            handler.On(HttpMethod.Post, ChallengeUri, request =>
            {
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsEmptyObjectPayload(body))
                {
                    challengePostsWithBody++;
                    throw new HttpRequestException("response lost after accept");
                }

                return ChallengeStatusResponse("invalid", detail: "incorrect TXT");
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeCaException ex = await Assert.ThrowsAsync<AcmeCaException>(() =>
                client.SubmitChallengeAsync(ChallengeUri, CancellationToken.None));

            Assert.Equal(1, challengePostsWithBody);
            Assert.IsNotType<AcmeAmbiguousOutcomeException>(ex);
            Assert.Contains("incorrect TXT", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// POST-as-GET retries a transient 503 with a fresh JWS and eventually succeeds.
        /// </summary>
        [Fact]
        public async Task PostAsGet_retries_transient_failures_with_fresh_jws()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            var noncesSeen = new List<string>();
            int attempts = 0;
            handler.On(HttpMethod.Post, OrderResourceUri, request =>
            {
                attempts++;
                noncesSeen.Add(ExtractNonce(request));
                if (attempts == 1)
                {
                    return ServerErrorResponse(HttpStatusCode.ServiceUnavailable);
                }

                return OrderPendingResponse("host.example.com");
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, new ImmediateDelayTimeProvider());
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeOrderResource order = await client.GetOrderAsync(OrderResourceUri, CancellationToken.None);

            Assert.Equal(2, attempts);
            Assert.Equal(2, noncesSeen.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("pending", order.Status);
        }

        /// <summary>
        /// A received badNonce response re-signs with a fresh nonce and does not treat the attempt as ambiguous.
        /// </summary>
        [Fact]
        public async Task BadNonce_triggers_fresh_nonce_without_ambiguous_failure()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int attempts = 0;
            var noncesSeen = new List<string>();
            handler.On(HttpMethod.Post, NewOrderUri, request =>
            {
                attempts++;
                noncesSeen.Add(ExtractNonce(request));
                if (attempts == 1)
                {
                    return BadNonceResponse();
                }

                return CreatedOrderResponse();
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            AcmeOrder order = await client.CreateOrderAsync(
                [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                CancellationToken.None);

            Assert.Equal(2, attempts);
            Assert.Equal(2, noncesSeen.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(OrderUri, order.Location);
        }

        /// <summary>
        /// newAccount remains retryable after a transport failure because same-key registration is idempotent.
        /// </summary>
        [Fact]
        public async Task NewAccount_retries_after_transport_failure()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int attempts = 0;
            handler.On(HttpMethod.Post, NewAccountUri, _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException("connection reset");
                }

                return CreatedAccountResponse();
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, new ImmediateDelayTimeProvider());

            string accountUrl = await client.RegisterAccountAsync([], termsOfServiceAgreed: true, null, CancellationToken.None);

            Assert.Equal(2, attempts);
            Assert.Equal(AccountUri.AbsoluteUri, accountUrl);
        }

        /// <summary>
        /// HTTP 503 on newOrder is treated as ambiguous for state-changing POSTs: no second newOrder is sent.
        /// </summary>
        [Fact]
        public async Task NewOrder_http_503_does_not_blindly_replay()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int newOrderPosts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                newOrderPosts++;
                return ServerErrorResponse(HttpStatusCode.ServiceUnavailable);
            });
            handler.On(HttpMethod.Post, AccountUri, _ => AccountWithOrdersResponse());
            handler.On(HttpMethod.Post, OrdersUri, _ => OrdersListResponse([]));

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    CancellationToken.None));

            Assert.Equal(1, newOrderPosts);
        }

        /// <summary>
        /// Caller cancellation is not converted into a retry or an ambiguous-outcome recovery path.
        /// </summary>
        [Fact]
        public async Task Caller_cancellation_is_not_retried()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            using var cts = new CancellationTokenSource();
            int posts = 0;
            handler.On(HttpMethod.Post, NewOrderUri, _ =>
            {
                posts++;
                cts.Cancel();
                throw new TaskCanceledException();
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.CreateOrderAsync(
                    [new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = "host.example.com" }],
                    cts.Token));

            Assert.Equal(1, posts);
        }

        /// <summary>
        /// Safe-read retry exhaustion fails deterministically after the configured attempts without inventing success.
        /// </summary>
        [Fact]
        public async Task PostAsGet_retry_exhaustion_is_deterministic()
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int attempts = 0;
            handler.On(HttpMethod.Post, OrderResourceUri, _ =>
            {
                attempts++;
                throw new HttpRequestException("always down");
            });

            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, new ImmediateDelayTimeProvider());
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAsync<AcmeCaException>(() =>
                client.GetOrderAsync(OrderResourceUri, CancellationToken.None));

            // First attempt + MaxTransientRetries (4) = 5
            Assert.Equal(5, attempts);
        }

        /// <summary>
        /// HTTP 500/502/504 on finalize do not cause a second finalize POST.
        /// </summary>
        /// <param name="statusCode">Server error status under test.</param>
        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public async Task Finalize_server_errors_do_not_replay(HttpStatusCode statusCode)
        {
            var handler = new ScriptedHandler();
            handler.On(HttpMethod.Get, DirectoryUri, DirectoryResponse);
            handler.On(HttpMethod.Head, NewNonceUri, NonceResponse);
            int finalizePosts = 0;
            handler.On(HttpMethod.Post, FinalizeUri, _ =>
            {
                finalizePosts++;
                return ServerErrorResponse(statusCode);
            });
            handler.On(HttpMethod.Post, OrderResourceUri, _ => OrderPendingResponse("host.example.com"));

            var time = new AdvancingDelayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            using AcmeAccountKey key = CreateKey();
            AcmeClient client = CreateClient(handler, key, time);
            client.BindExistingAccount(AccountUri.AbsoluteUri);

            await Assert.ThrowsAsync<AcmeAmbiguousOutcomeException>(() =>
                client.FinalizeOrderAsync(
                    OrderResourceUri,
                    FinalizeUri,
                    [0x30, 0x03, 0x02, 0x01, 0x00],
                    CancellationToken.None));

            Assert.Equal(1, finalizePosts);
        }

        private static AcmeClient CreateClient(
            ScriptedHandler handler,
            AcmeAccountKey key,
            TimeProvider? time = null)
        {
            var http = new AcmeHttpTransport(
                new SingleHandlerFactory(handler),
                "acme-test",
                DirectoryUri,
                NullLogger.Instance,
                time ?? TimeProvider.System);
            return new AcmeClient(http, key, NullLogger.Instance, time ?? TimeProvider.System);
        }

        private static AcmeAccountKey CreateKey()
        {
            using var rsa = RSA.Create(2048);
            return AcmeAccountKey.ImportPkcs8Der(rsa.ExportPkcs8PrivateKey());
        }

        private static HttpResponseMessage DirectoryResponse(HttpRequestMessage _)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "newNonce": "{{NewNonceUri}}",
                      "newAccount": "{{NewAccountUri}}",
                      "newOrder": "{{NewOrderUri}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage NonceResponse(HttpRequestMessage _)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage AccountWithOrdersResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"status":"valid","orders":"{{OrdersUri}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage OrdersListResponse(IReadOnlyList<Uri> orders, Uri? nextPage = null)
        {
            string json = JsonSerializer.Serialize(new { orders = orders.Select(static u => u.AbsoluteUri).ToArray() });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            if (nextPage is not null)
            {
                response.Headers.TryAddWithoutValidation("Link", $"<{nextPage.AbsoluteUri}>;rel=\"next\"");
            }

            return response;
        }

        private static HttpResponseMessage OrderPendingResponse(string dnsName)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "status":"pending",
                      "identifiers":[{"type":"dns","value":"{{dnsName}}"}],
                      "authorizations":["https://acme.test/authz/1"],
                      "finalize":"{{FinalizeUri}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage OrderProcessingResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"processing","identifiers":[{"type":"dns","value":"host.example.com"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage OrderReadyResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "status":"ready",
                      "identifiers":[{"type":"dns","value":"host.example.com"}],
                      "authorizations":["https://acme.test/authz/1"],
                      "finalize":"{{FinalizeUri}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage ChallengeProcessingResponse() =>
            ChallengeStatusResponse("processing");

        private static HttpResponseMessage ChallengeStatusResponse(string status, string? detail = null)
        {
            string json = detail is null
                ? "{\"type\":\"dns-01\",\"status\":\"" + status + "\",\"token\":\"tok\",\"url\":\"https://acme.test/chall/1\"}"
                : "{\"type\":\"dns-01\",\"status\":\"" + status
                  + "\",\"token\":\"tok\",\"url\":\"https://acme.test/chall/1\",\"error\":{\"type\":\"urn:ietf:params:acme:error:incorrectResponse\",\"detail\":\""
                  + detail + "\"}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage CreatedOrderResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "status":"pending",
                      "identifiers":[{"type":"dns","value":"host.example.com"}],
                      "authorizations":["https://acme.test/authz/1"],
                      "finalize":"{{FinalizeUri}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.Location = OrderUri;
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage CreatedAccountResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"status":"valid"}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.Location = AccountUri;
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage BadNonceResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"type":"urn:ietf:params:acme:error:badNonce","detail":"Bad nonce"}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static HttpResponseMessage ServerErrorResponse(HttpStatusCode statusCode)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("temporary", Encoding.UTF8, "text/plain"),
            };
            response.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));
            return response;
        }

        private static string ExtractNonce(HttpRequestMessage request)
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(body);
            string protectedB64 = document.RootElement.GetProperty("protected").GetString()
                ?? throw new InvalidOperationException("missing protected");
            string padded = protectedB64.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            string protectedJson = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using JsonDocument protectedDocument = JsonDocument.Parse(protectedJson);
            return protectedDocument.RootElement.GetProperty("nonce").GetString()
                ?? throw new InvalidOperationException("missing nonce");
        }

        private static bool IsEmptyObjectPayload(string jwsBody)
        {
            using JsonDocument document = JsonDocument.Parse(jwsBody);
            string payload = document.RootElement.GetProperty("payload").GetString() ?? string.Empty;
            // "{}" base64url is "e30". Empty string is POST-as-GET, not a challenge submit.
            return payload == "e30";
        }

        /// <summary>Routes scripted ACME responses by method and absolute URI.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Dictionary<(HttpMethod Method, string Uri), Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();

            /// <summary>Registers a response factory for <paramref name="method"/> and <paramref name="uri"/>.</summary>
            internal void On(HttpMethod method, Uri uri, Func<HttpRequestMessage, HttpResponseMessage> factory) =>
                _routes[(method, uri.AbsoluteUri)] = factory;

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string uri = request.RequestUri?.AbsoluteUri
                    ?? throw new InvalidOperationException("missing request URI");
                if (!_routes.TryGetValue((request.Method, uri), out Func<HttpRequestMessage, HttpResponseMessage>? factory))
                {
                    throw new InvalidOperationException($"No scripted response for {request.Method} {uri}");
                }

                return Task.FromResult(factory(request));
            }
        }

        /// <summary>Returns the same handler for every named client.</summary>
        private sealed class SingleHandlerFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            /// <summary>Stores <paramref name="handler"/> for every <see cref="CreateClient"/> call.</summary>
            internal SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;

            /// <inheritdoc />
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        /// <summary>
        /// Completes <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> immediately so retry tests stay deterministic.
        /// </summary>
        private sealed class ImmediateDelayTimeProvider : TimeProvider
        {
            /// <inheritdoc />
            public override ITimer CreateTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    callback(state);
                }

                return new NoOpTimer();
            }

            /// <summary>Timer that ignores further schedule changes.</summary>
            private sealed class NoOpTimer : ITimer
            {
                /// <inheritdoc />
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <inheritdoc />
                public void Dispose()
                {
                }

                /// <inheritdoc />
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Advances <see cref="GetUtcNow"/> by the delay amount when a timer fires so recovery deadlines expire deterministically.
        /// </summary>
        private sealed class AdvancingDelayTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow;

            /// <summary>Starts the clock at <paramref name="utcNow"/>.</summary>
            internal AdvancingDelayTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

            /// <inheritdoc />
            public override DateTimeOffset GetUtcNow() => _utcNow;

            /// <inheritdoc />
            public override ITimer CreateTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _utcNow += dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime;
                    callback(state);
                }

                return new NoOpTimer();
            }

            /// <summary>Timer that ignores further schedule changes.</summary>
            private sealed class NoOpTimer : ITimer
            {
                /// <inheritdoc />
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <inheritdoc />
                public void Dispose()
                {
                }

                /// <inheritdoc />
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }
    }
}
