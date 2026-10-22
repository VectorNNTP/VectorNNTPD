using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Tests.Support.Placement;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    /// <summary>
    /// Live TLS proof that STORE withholds body credit until the isolated queue can reserve ArtSize.
    /// The host binds 127.0.0.1 on an ephemeral port and does not publish DNS or touch a spool.
    /// </summary>
    public sealed class StorageVatpLiveBackpressureTests
    {
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Available_capacity_orders_reservation_window_data_result_before_publication()
        {
            var article = Article("avail-0001");
            var budget = article.ArtSize * 4L;
            var publisher = new GatePublisher();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: true);
            await using var trace = await TracingClient.ConnectAsync(host.Port);

            await trace.SendStoreAndMetaAsync(article);
            var outcome = await trace.SendBodyAfterWindowAsync(article);
            Assert.Equal(VatpStoreOutcome.Accepted, outcome);
            Assert.Equal(0, trace.DataFramesBeforeWindow);
            Assert.Equal(1, trace.WindowsBeforeData);
            Assert.Equal((uint)ArticleTransferLimits.Default.InitialStreamWindowBytes, trace.FirstWindowCredit);
            Assert.True(trace.DataFrames >= 1);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.True(host.Queue.AccountedBytes <= budget);

            await WaitUntilAsync(() => publisher.Entered >= 1);
            Assert.Equal(0, publisher.Finished);
            publisher.Release();
            await WaitUntilAsync(() => publisher.Finished >= 1);
            await WaitUntilAsync(() => host.Queue.AccountedBytes == 0 && host.Queue.AdmissionWaiters == 0);

            await using var client = PlacementClient();
            var beforeData = client.Snapshot().DataFramesWritten;
            var placed = await client.PlaceAsync(article, host.Target, CancellationToken.None);
            Assert.Equal(ArticlePlacementKind.Accepted, placed.Kind);
            Assert.True(client.Snapshot().DataFramesWritten > beforeData);
            await WaitUntilAsync(() => publisher.Finished >= 2 && host.Queue.AccountedBytes == 0);

            WriteEvidence(
                "available",
                budget,
                article.ArtSize,
                host,
                deferred: host.Logger.Count("waiting for queue capacity"),
                granted: 2,
                dataBeforeAuthorization: trace.DataFramesBeforeWindow,
                results: "Accepted,Accepted",
                published: publisher.Finished,
                cleanup: "accounted=0",
                trace.TraceText());
        }

        [Fact]
        public async Task Full_queue_withholds_body_until_capacity_returns_and_then_publishes()
        {
            var filler = Article("full-filler");
            var waiter = Article("full-waiter");
            Assert.Equal(filler.ArtSize, waiter.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false);

            await using var fillerClient = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await fillerClient.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);
            Assert.Equal(budget, host.Queue.QueuedBytes);
            Assert.Equal(0, host.Queue.ReservedBytes);

            await using var blocked = await TracingClient.ConnectAsync(host.Port);
            await blocked.SendStoreAndMetaAsync(waiter);
            await WaitUntilAsync(
                () => host.Queue.AdmissionWaiters == 1
                    && host.Logger.Text.Contains("waiting for queue capacity", StringComparison.Ordinal));
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(budget, host.Queue.AccountedBytes);
            Assert.Equal(0, blocked.DataFrames);
            Assert.Equal(0, blocked.WindowsBeforeData);
            Assert.Equal(1, host.Queue.EnqueuedCount);
            Assert.Contains("waiting for queue capacity", host.Logger.Text, StringComparison.Ordinal);

            var delivery = await host.Queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            await blocked.WindowGranted.Task.WaitAsync(Safety);
            Assert.Equal(1, blocked.WindowsBeforeData);
            Assert.Equal(0, blocked.DataFramesBeforeWindow);
            Assert.Equal(0, blocked.DataFrames);
            Assert.Equal(waiter.ArtSize, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.QueuedBytes);
            Assert.True(host.Queue.AccountedBytes <= budget);

            Assert.Equal(0, publisher.Published);
            var outcome = await blocked.SendBodyAfterWindowAsync(waiter);
            Assert.Equal(VatpStoreOutcome.Accepted, outcome);
            Assert.Equal(0, blocked.DataFramesBeforeWindow);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(waiter.ArtSize, host.Queue.QueuedBytes);
            Assert.Equal(0, publisher.Published);
            await publisher.PublishAsync(delivery.Article);
            Assert.Equal(1, publisher.Published);

            var queued = await host.Queue.DequeueAsync(CancellationToken.None);
            Assert.True(queued.HasArticle);
            Assert.True(queued.Article.ArtData.Span.SequenceEqual(waiter.ArtData.Span));
            await publisher.PublishAsync(queued.Article);
            await WaitUntilAsync(() =>
                host.Queue.AccountedBytes == 0
                && host.Queue.ReservedBytes == 0
                && host.Queue.AdmissionWaiters == 0);

            await using var production = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await production.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);
            var dataBefore = production.Snapshot().DataFramesWritten;
            var creditBefore = production.Snapshot().CreditWaitTicks;
            var pending = production.PlaceAsync(waiter, host.Target, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1 && !pending.IsCompleted);
            Assert.Equal(dataBefore, production.Snapshot().DataFramesWritten);
            Assert.Equal(creditBefore, production.Snapshot().CreditWaitTicks);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(budget, host.Queue.AccountedBytes);

            var held = await host.Queue.DequeueAsync(CancellationToken.None);
            var accepted = await pending.WaitAsync(Safety);
            Assert.Equal(ArticlePlacementKind.Accepted, accepted.Kind);
            Assert.True(production.Snapshot().CreditWaitTicks > creditBefore);
            Assert.True(production.Snapshot().DataFramesWritten > dataBefore);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(waiter.ArtSize, host.Queue.QueuedBytes);
            await publisher.PublishAsync(held.Article);
            var second = await host.Queue.DequeueAsync(CancellationToken.None);
            await publisher.PublishAsync(second.Article);
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);

            WriteEvidence(
                "full-and-recovery",
                budget,
                waiter.ArtSize,
                host,
                deferred: host.Logger.Count("waiting for queue capacity"),
                granted: 4,
                dataBeforeAuthorization: blocked.DataFramesBeforeWindow,
                results: "Accepted",
                published: publisher.Published,
                cleanup: "reserved=0 waiters=0",
                blocked.TraceText());
        }

        [Fact]
        public async Task Cancel_and_disconnect_release_waiters_reservations_and_partial_bodies()
        {
            var filler = Article("drop-filler");
            var cancelled = Article("drop-cancel");
            var disconnected = Article("drop-closeX");
            var reserved = Article("drop-window");
            var partial = Article("drop-partia");
            var follow = Article("drop-follow");
            Assert.Equal(filler.ArtSize, cancelled.ArtSize);
            Assert.Equal(filler.ArtSize, disconnected.ArtSize);
            Assert.Equal(filler.ArtSize, reserved.ArtSize);
            Assert.Equal(filler.ArtSize, partial.ArtSize);
            Assert.Equal(filler.ArtSize, follow.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false);
            await using var owner = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);

            await using var cancelClient = PlacementClient();
            using var cancel = new CancellationTokenSource();
            var dataBefore = cancelClient.Snapshot().DataFramesWritten;
            var cancelTask = cancelClient.PlaceAsync(cancelled, host.Target, cancel.Token).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1);
            Assert.Equal(dataBefore, cancelClient.Snapshot().DataFramesWritten);
            Assert.Equal(0, host.Queue.ReservedBytes);
            await cancel.CancelAsync();
            var cancelResult = await cancelTask.WaitAsync(Safety);
            Assert.Equal(ArticlePlacementKind.Cancelled, cancelResult.Kind);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 0);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(budget, host.Queue.QueuedBytes);
            Assert.Equal(dataBefore, cancelClient.Snapshot().DataFramesWritten);

            await using var dropClient = PlacementClient();
            var dropTask = dropClient.PlaceAsync(disconnected, host.Target, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1);
            Assert.Equal(0, dropClient.Snapshot().DataFramesWritten);
            await dropClient.DisposeAsync();
            var dropResult = await dropTask.WaitAsync(Safety);
            Assert.True(
                dropResult.Kind is ArticlePlacementKind.Cancelled or ArticlePlacementKind.TransportFailure,
                dropResult.Kind.ToString());
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 0 && host.Queue.ReservedBytes == 0);
            Assert.Equal(budget, host.Queue.QueuedBytes);

            await using var windowClient = await TracingClient.ConnectAsync(host.Port);
            await windowClient.SendStoreAndMetaAsync(reserved);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1);
            var freed = await host.Queue.DequeueAsync(CancellationToken.None);
            await publisher.PublishAsync(freed.Article);
            await windowClient.WindowGranted.Task.WaitAsync(Safety);
            Assert.Equal(0, windowClient.DataFrames);
            Assert.Equal(reserved.ArtSize, host.Queue.ReservedBytes);
            await windowClient.DisposeAsync();
            await WaitUntilAsync(() => host.Queue.ReservedBytes == 0 && host.Queue.AdmissionWaiters == 0);
            Assert.Equal(0, host.Queue.QueuedBytes);

            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);
            await using var partialClient = await TracingClient.ConnectAsync(host.Port);
            await partialClient.SendStoreAndMetaAsync(partial);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1);
            var again = await host.Queue.DequeueAsync(CancellationToken.None);
            await publisher.PublishAsync(again.Article);
            await partialClient.WindowGranted.Task.WaitAsync(Safety);
            await partialClient.SendPartialDataAsync(partial);
            Assert.Equal(1, partialClient.DataFrames);
            Assert.Equal(0, partialClient.DataFramesBeforeWindow);
            Assert.Equal(partial.ArtSize, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.QueuedBytes);
            var enqueuedBeforeDrop = host.Queue.EnqueuedCount;
            await partialClient.DisposeAsync();
            await WaitUntilAsync(() => host.Queue.ReservedBytes == 0 && host.Queue.AdmissionWaiters == 0);
            Assert.Equal(enqueuedBeforeDrop, host.Queue.EnqueuedCount);
            Assert.Equal(0, host.Queue.AccountedBytes);

            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(follow, host.Target, CancellationToken.None)).Kind);
            var delivered = await host.Queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivered.Article.ArtData.Span.SequenceEqual(follow.ArtData.Span));
            await publisher.PublishAsync(delivered.Article);
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);

            WriteEvidence(
                "cancel-disconnect",
                budget,
                filler.ArtSize,
                host,
                deferred: host.Logger.Count("waiting for queue capacity"),
                granted: 3,
                dataBeforeAuthorization: windowClient.DataFramesBeforeWindow + partialClient.DataFramesBeforeWindow,
                results: cancelResult.Kind + "," + dropResult.Kind + ",Accepted",
                published: publisher.Published,
                cleanup: "reserved=0 waiters=0 accounted=0",
                windowClient.TraceText());
        }

        [Fact]
        public async Task Repeated_admission_does_not_leak_reservations_or_send_early_data()
        {
            const int cycles = 8;
            var sample = Article("loop-000-a");
            var budget = sample.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false);
            await using var client = PlacementClient();
            var earlyData = 0;
            var deferred = 0;
            var granted = 0;

            for (var i = 0; i < cycles; i++)
            {
                var filler = Article(FormattableString.Invariant($"loop-{i:000}-a"));
                var waiter = Article(FormattableString.Invariant($"loop-{i:000}-b"));
                Assert.Equal(budget, filler.ArtSize);
                Assert.Equal(budget, waiter.ArtSize);

                Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);
                granted++;
                var dataBefore = client.Snapshot().DataFramesWritten;
                var creditBefore = client.Snapshot().CreditWaitTicks;
                var pending = client.PlaceAsync(waiter, host.Target, CancellationToken.None).AsTask();
                await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1 && !pending.IsCompleted);
                deferred++;
                if (client.Snapshot().DataFramesWritten != dataBefore || pending.IsCompleted)
                {
                    earlyData++;
                }

                Assert.Equal(dataBefore, client.Snapshot().DataFramesWritten);
                Assert.Equal(creditBefore, client.Snapshot().CreditWaitTicks);
                Assert.False(pending.IsCompleted);
                Assert.Equal(0, host.Queue.ReservedBytes);
                Assert.Equal(budget, host.Queue.QueuedBytes);
                Assert.Equal(budget, host.Queue.AccountedBytes);

                var held = await host.Queue.DequeueAsync(CancellationToken.None);
                var accepted = await pending.WaitAsync(Safety);
                Assert.Equal(ArticlePlacementKind.Accepted, accepted.Kind);
                granted++;
                Assert.True(client.Snapshot().CreditWaitTicks > creditBefore);
                Assert.True(client.Snapshot().DataFramesWritten > dataBefore);
                Assert.Equal(0, host.Queue.ReservedBytes);
                Assert.Equal(waiter.ArtSize, host.Queue.QueuedBytes);
                await publisher.PublishAsync(held.Article);
                var queued = await host.Queue.DequeueAsync(CancellationToken.None);
                Assert.True(queued.Article.ArtData.Span.SequenceEqual(waiter.ArtData.Span));
                await publisher.PublishAsync(queued.Article);
                Assert.Equal(0, host.Queue.ReservedBytes);
                Assert.Equal(0, host.Queue.QueuedBytes);
                Assert.Equal(0, host.Queue.AccountedBytes);
                Assert.Equal(0, host.Queue.AdmissionWaiters);
            }

            Assert.Equal(0, earlyData);
            Assert.Equal(cycles, deferred);
            Assert.Equal(cycles * 2, granted);
            Assert.Equal(cycles * 2, publisher.Published);

            WriteEvidence(
                "repeated",
                budget,
                sample.ArtSize,
                host,
                deferred,
                granted,
                earlyData,
                results: "Accepted",
                published: publisher.Published,
                cleanup: "reserved=0 queued=0 waiters=0",
                trace: host.Logger.Text);
        }

        [Fact]
        public async Task Capacity_wait_outlives_io_progress_timeout_then_admits_and_publishes()
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            var filler = Article("wait-filler");
            var waiter = Article("wait-waiter");
            Assert.Equal(filler.ArtSize, waiter.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false, ioProgressTimeout: timeout);
            await using var owner = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);

            await using var client = PlacementClient(timeout);
            var dataBefore = client.Snapshot().DataFramesWritten;
            var creditBefore = client.Snapshot().CreditWaitTicks;
            var pending = client.PlaceAsync(waiter, host.Target, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1 && !pending.IsCompleted);

            // The delay is the subject: admission must outlive both no-progress timeouts.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, host.Queue.AdmissionWaiters);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(budget, host.Queue.AccountedBytes);
            Assert.Equal(dataBefore, client.Snapshot().DataFramesWritten);
            Assert.Equal(creditBefore, client.Snapshot().CreditWaitTicks);

            var held = await host.Queue.DequeueAsync(CancellationToken.None);
            var accepted = await pending.WaitAsync(Safety);
            Assert.Equal(ArticlePlacementKind.Accepted, accepted.Kind);
            Assert.True(client.Snapshot().DataFramesWritten > dataBefore);
            Assert.True(client.Snapshot().CreditWaitTicks > creditBefore);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);
            Assert.Equal(waiter.ArtSize, host.Queue.QueuedBytes);
            await publisher.PublishAsync(held.Article);
            var second = await host.Queue.DequeueAsync(CancellationToken.None);
            Assert.True(second.HasArticle);
            Assert.True(second.Article.ArtData.Span.SequenceEqual(waiter.ArtData.Span));
            await publisher.PublishAsync(second.Article);
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);
            Assert.Equal(2, publisher.Published);
        }

        [Fact]
        public async Task Cancel_during_a_long_capacity_wait_releases_the_waiter()
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            var filler = Article("cncl-filler");
            var waiting = Article("cncl-waiter");
            Assert.Equal(filler.ArtSize, waiting.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false, ioProgressTimeout: timeout);
            await using var owner = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);

            await using var client = PlacementClient(timeout);
            using var cancel = new CancellationTokenSource();
            var dataBefore = client.Snapshot().DataFramesWritten;
            var pending = client.PlaceAsync(waiting, host.Target, cancel.Token).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1 && !pending.IsCompleted);

            // The delay is the subject: cancellation must still be observed after the no-progress timeout.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assert.False(pending.IsCompleted);
            Assert.Equal(dataBefore, client.Snapshot().DataFramesWritten);
            await cancel.CancelAsync();
            var result = await pending.WaitAsync(Safety);
            Assert.Equal(ArticlePlacementKind.Cancelled, result.Kind);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 0);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(budget, host.Queue.QueuedBytes);
            Assert.Equal(budget, host.Queue.AccountedBytes);
            Assert.Equal(dataBefore, client.Snapshot().DataFramesWritten);
        }

        [Fact]
        public async Task Disconnect_during_a_long_capacity_wait_lets_a_later_store_proceed()
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            var filler = Article("disc-filler");
            var waiting = Article("disc-waiter");
            var follow = Article("disc-follow");
            Assert.Equal(filler.ArtSize, waiting.ArtSize);
            Assert.Equal(filler.ArtSize, follow.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false, ioProgressTimeout: timeout);
            await using var owner = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);

            await using var client = PlacementClient(timeout);
            var dataBefore = client.Snapshot().DataFramesWritten;
            var pending = client.PlaceAsync(waiting, host.Target, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1 && !pending.IsCompleted);

            // The delay is the subject: disconnect cleanup must still run after the no-progress timeout.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assert.False(pending.IsCompleted);
            Assert.Equal(dataBefore, client.Snapshot().DataFramesWritten);
            await client.DisposeAsync();
            var result = await pending.WaitAsync(Safety);
            Assert.Equal(ArticlePlacementKind.Cancelled, result.Kind);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 0 && host.Queue.ReservedBytes == 0);
            Assert.Equal(budget, host.Queue.QueuedBytes);

            var held = await host.Queue.DequeueAsync(CancellationToken.None);
            await publisher.PublishAsync(held.Article);
            await using var next = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await next.PlaceAsync(follow, host.Target, CancellationToken.None)).Kind);
            var queued = await host.Queue.DequeueAsync(CancellationToken.None);
            Assert.True(queued.HasArticle);
            Assert.True(queued.Article.ArtData.Span.SequenceEqual(follow.ArtData.Span));
            await publisher.PublishAsync(queued.Article);
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);
            Assert.Equal(2, publisher.Published);
        }

        [Fact]
        public async Task Silent_connection_after_hello_still_times_out()
        {
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(1024 * 1024, publisher, startWorker: false, ioProgressTimeout: TimeSpan.FromMilliseconds(200));
            await using var trace = await TracingClient.ConnectAsync(host.Port);
            await trace.Closed.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, host.Queue.ReservedBytes);
            Assert.Equal(0, host.Queue.AdmissionWaiters);
            Assert.Equal(0, publisher.Published);
        }

        [Fact]
        public async Task Silence_after_window_still_times_out_and_releases_the_reservation()
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            var filler = Article("stall-fillr");
            var waiter = Article("stall-waite");
            Assert.Equal(filler.ArtSize, waiter.ArtSize);
            var budget = filler.ArtSize;
            var publisher = new PublishCounter();
            await using var host = await LiveHost.StartAsync(budget, publisher, startWorker: false, ioProgressTimeout: timeout);
            await using var owner = PlacementClient();
            Assert.Equal(ArticlePlacementKind.Accepted, (await owner.PlaceAsync(filler, host.Target, CancellationToken.None)).Kind);

            await using var trace = await TracingClient.ConnectAsync(host.Port);
            await trace.SendStoreAndMetaAsync(waiter);
            await WaitUntilAsync(() => host.Queue.AdmissionWaiters == 1);
            var held = await host.Queue.DequeueAsync(CancellationToken.None);
            await trace.WindowGranted.Task.WaitAsync(Safety);
            Assert.Equal(waiter.ArtSize, host.Queue.ReservedBytes);
            Assert.Equal(0, trace.DataFrames);
            await trace.Closed.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => host.Queue.ReservedBytes == 0 && host.Queue.AdmissionWaiters == 0);
            Assert.Equal(0, host.Queue.QueuedBytes);
            Assert.Equal(0, host.Queue.AccountedBytes);
            Assert.Equal(0, trace.DataFrames);
            await publisher.PublishAsync(held.Article);
            Assert.Equal(1, publisher.Published);
        }

        private static void WriteEvidence(
            string scenario,
            long budget,
            int artSize,
            LiveHost host,
            int deferred,
            int granted,
            int dataBeforeAuthorization,
            string results,
            int published,
            string cleanup,
            string trace)
        {
            var line =
                $"EVIDENCE scenario={scenario} budget={budget} artSize={artSize} queued={host.Queue.QueuedBytes} reserved={host.Queue.ReservedBytes} accounted={host.Queue.AccountedBytes} deferred={deferred} granted={granted} dataBeforeAuthorization={dataBeforeAuthorization} results={results} published={published} cleanup={cleanup}";
            Console.WriteLine(line);
            Console.WriteLine(trace);
        }

        private static ArticlePlacementClient PlacementClient(TimeSpan? ioTimeout = null) =>
            new(
                NullLogger<ArticlePlacementClient>.Instance,
                new VatpClientOptions { IoTimeout = ioTimeout ?? TimeSpan.FromSeconds(30) },
                ArticleTransferLimits.Default,
                maxOutstandingStores: 1)
            {
                TestTcpConnectHost = "127.0.0.1",
                ServerCertificateValidationCallback = static (_, _, _, _) => true,
            };

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(Safety);
            while (!condition())
            {
                await Task.Delay(1, timeout.Token);
            }
        }

        private static ArticleRecord Article(string token)
        {
            var parser = new NntpArticleParser("nntpd01.usenet.ninja");
            var text = new StringBuilder()
                .Append("Path: peer.example\r\n")
                .Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n")
                .Append("Message-ID: <").Append(token).Append("@seg.test>\r\n")
                .Append("Newsgroups: alt.test\r\n")
                .Append("From: user@example.test\r\n")
                .Append("Subject: vatp\r\n")
                .Append("\r\n")
                .Append("live-body\r\n");
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text.ToString()));
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return created.Record;
        }

        private sealed class LiveHost : IAsyncDisposable
        {
            private readonly StorageVatpListenerService _listener;
            private readonly AcceptedArticleWorker? _worker;
            private readonly TlsCertificateContextProvider _certificates;
            private readonly GatePublisher? _gate;

            private LiveHost(
                StorageVatpListenerService listener,
                AcceptedArticleWorker? worker,
                TlsCertificateContextProvider certificates,
                GatePublisher? gate,
                AcceptedArticleQueue queue,
                ListLogger logger,
                StorageServerFleetEntry target,
                int port)
            {
                _listener = listener;
                _worker = worker;
                _certificates = certificates;
                _gate = gate;
                Queue = queue;
                Logger = logger;
                Target = target;
                Port = port;
            }

            public AcceptedArticleQueue Queue { get; }

            public ListLogger Logger { get; }

            public StorageServerFleetEntry Target { get; }

            public int Port { get; }

            public static async Task<LiveHost> StartAsync(
                long budget,
                IAcceptedArticleProcessor processor,
                bool startWorker,
                TimeSpan? ioProgressTimeout = null)
            {
                var port = StorageServerTestOptions.GetFreeTcpPort();
                var identity = StorageServerTestOptions.CreateValid();
                identity.BindAddress = ["127.0.0.1"];
                identity.BindPortTls = port;
                identity.Listener.IoProgressTimeoutSeconds = 30;
                var acme = StorageServerTestOptions.CreateValidAcme(identity);
                acme.BindAddress = ["127.0.0.1"];
                acme.BindPortTls = port;
                var runtime = StorageServerRuntimeOptionsFactory.Create(identity, acme);
                if (ioProgressTimeout is { } progress)
                {
                    runtime = runtime with { Listener = runtime.Listener with { IoProgressTimeout = progress } };
                }
                var readiness = new AcmeCertificateReadiness();
                readiness.MarkReady();
                var certificates = TestListenerCertificates.CreatePublishedProvider();
                var logger = new ListLogger();
                var queue = new AcceptedArticleQueue(budget, TimeProvider.System);
                GatePublisher? gate = processor as GatePublisher;
                var listener = new StorageVatpListenerService(
                    runtime,
                    certificates,
                    NullStorageArticleOpenBoundary.Instance,
                    readiness,
                    logger,
                    queue,
                    new StoreAssemblyAdmission(8));
                await listener.StartAsync(CancellationToken.None);
                AcceptedArticleWorker? worker = null;
                if (startWorker)
                {
                    worker = new AcceptedArticleWorker(
                        queue,
                        processor,
                        NullLogger<AcceptedArticleWorker>.Instance,
                        new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 });
                    await worker.StartAsync(CancellationToken.None);
                }

                return new LiveHost(
                    listener,
                    worker,
                    certificates,
                    gate,
                    queue,
                    logger,
                    new StorageServerFleetEntry(identity.ServerId!.Value, identity.Fqdn, 1, 0, 1, DateTimeOffset.UtcNow, port),
                    port);
            }

            public async ValueTask DisposeAsync()
            {
                _gate?.Release();
                await _listener.DisposeAsync();
                if (_worker is not null)
                {
                    using var stop = new CancellationTokenSource(Safety);
                    await _worker.StopAsync(stop.Token);
                }

                await _certificates.DisposeAsync();
            }
        }

        private sealed class ListLogger : ILogger<StorageVatpListenerService>
        {
            private readonly List<string> _messages = [];

            public string Text
            {
                get
                {
                    lock (_messages)
                    {
                        return string.Join('\n', _messages);
                    }
                }
            }

            public int Count(string fragment)
            {
                lock (_messages)
                {
                    return _messages.Count(message => message.Contains(fragment, StringComparison.Ordinal));
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_messages)
                {
                    _messages.Add(formatter(state, exception));
                }
            }
        }

        private sealed class PublishCounter : IAcceptedArticleProcessor
        {
            private int _published;

            public int Published => Volatile.Read(ref _published);

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> PublishAsync(AcceptedArticle article) =>
                ProcessAsync(article, CancellationToken.None);

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _published);
                return new ValueTask<AcceptedArticleProcessResult>(AcceptedArticleProcessResult.Completed);
            }
        }

        private sealed class GatePublisher : IAcceptedArticleProcessor
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _entered;
            private int _finished;

            public int Entered => Volatile.Read(ref _entered);

            public int Finished => Volatile.Read(ref _finished);

            public void Release() => _release.TrySetResult();

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _entered);
                await _release.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref _finished);
                return AcceptedArticleProcessResult.Completed;
            }
        }

        private sealed class TracingClient : IAsyncDisposable
        {
            private readonly TcpClient _tcp;
            private readonly SslStream _ssl;
            private readonly CancellationTokenSource _dispose = new();
            private readonly object _gate = new();
            private readonly List<string> _trace = [];
            private readonly Task _reader;
            private int _disposedClient;
            private long _credit;
            private uint _maxFrame = VatpProtocol.DefaultMaxFramePayload;
            private int _dataFrames;
            private int _windowsBeforeData;
            private readonly TaskCompletionSource _peerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private TracingClient(TcpClient tcp, SslStream ssl)
            {
                _tcp = tcp;
                _ssl = ssl;
                WindowGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Result = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                _reader = ReadLoopAsync();
            }

            public TaskCompletionSource WindowGranted { get; }

            public TaskCompletionSource<byte> Result { get; }

            public Task Closed => _peerClosed.Task;

            public int DataFrames => Volatile.Read(ref _dataFrames);

            public int DataFramesBeforeWindow { get; private set; }

            public int WindowsBeforeData => Volatile.Read(ref _windowsBeforeData);

            public uint FirstWindowCredit { get; private set; }

            public static async Task<TracingClient> ConnectAsync(int port)
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
                    CancellationToken.None);
                var client = new TracingClient(tcp, ssl);
                await client.WriteAsync(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload), "C HELLO");
                await client.Hello.Task.WaitAsync(Safety);
                return client;
            }

            private TaskCompletionSource Hello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string TraceText()
            {
                lock (_gate)
                {
                    return string.Join('\n', _trace);
                }
            }

            public async Task SendStoreAndMetaAsync(ArticleRecord record)
            {
                var id = new byte[ArticleId.Length];
                record.ArtId.CopyTo(id);
                await WriteAsync(VatpFrameEncoder.EncodeStore(1, id), "C STORE");
                Assert.True(ArticlePlacementMeta.TryFromRecord(in record, out var meta));
                await WriteAsync(VatpFrameEncoder.EncodeMeta(1, VatpMetaCodec.Encode(in meta)), "C META");
            }

            public async Task<byte> SendBodyAfterWindowAsync(ArticleRecord record)
            {
                await WindowGranted.Task.WaitAsync(Safety);
                Assert.True(record.ArtSize <= ArticleTransferLimits.Default.InitialStreamWindowBytes);
                await SendDataAsync(record.ArtData, fin: true);
                await WriteAsync(VatpFrameEncoder.EncodeEnd(1), "C END");
                return await Result.Task.WaitAsync(Safety);
            }

            public async Task SendPartialDataAsync(ArticleRecord record)
            {
                await WindowGranted.Task.WaitAsync(Safety);
                await SendDataAsync(record.ArtData.Slice(0, 1), fin: false);
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposedClient, 1) != 0)
                {
                    return;
                }

                await _dispose.CancelAsync();
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException)
                {
                }

                await _ssl.DisposeAsync();
                _tcp.Dispose();
                _dispose.Dispose();
            }

            private async Task SendDataAsync(ReadOnlyMemory<byte> payload, bool fin)
            {
                if (WindowsBeforeData == 0)
                {
                    DataFramesBeforeWindow++;
                }

                Interlocked.Increment(ref _dataFrames);
                await WriteAsync(
                    VatpFrameEncoder.EncodeData(1, payload, fin),
                    fin ? $"C DATA {payload.Length} fin" : $"C DATA {payload.Length}");
            }

            private async Task WriteAsync(VatpFrameEncoder.EncodedFrame frame, string trace)
            {
                lock (_gate)
                {
                    _trace.Add(trace);
                }

                var bytes = VatpFrameEncoder.ToSingleBuffer(frame);
                await _ssl.WriteAsync(bytes, _dispose.Token);
            }

            private async Task ReadLoopAsync()
            {
                var buffer = new byte[64 * 1024];
                var count = 0;
                try
                {
                    while (!_dispose.IsCancellationRequested)
                    {
                        while (TryTakeFrame(buffer, ref count))
                        {
                        }

                        if (count == buffer.Length)
                        {
                            Array.Resize(ref buffer, buffer.Length * 2);
                        }

                        var read = await _ssl.ReadAsync(buffer.AsMemory(count), _dispose.Token);
                        if (read == 0)
                        {
                            _peerClosed.TrySetResult();
                            return;
                        }

                        count += read;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _peerClosed.TrySetResult();
                }
            }

            private bool TryTakeFrame(byte[] buffer, ref int count)
            {
                if (count < VatpProtocol.HeaderLengthBytes)
                {
                    return false;
                }

                var parsed = VatpFrameParser.ParseOneFrame(new ReadOnlySequence<byte>(buffer, 0, count), _maxFrame);
                if (parsed.Status == VatpFrameParseStatus.Incomplete)
                {
                    return false;
                }

                if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                {
                    throw new InvalidOperationException(parsed.Error.ToString());
                }

                Note(frame);
                var consumed = checked((int)parsed.ConsumedBytes);
                Buffer.BlockCopy(buffer, consumed, buffer, 0, count - consumed);
                count -= consumed;
                return true;
            }

            private void Note(VatpParsedFrame frame)
            {
                var payload = new byte[frame.Payload.Length];
                frame.Payload.CopyTo(payload);
                switch (frame.Header.Type)
                {
                    case VatpFrameType.Hello:
                        if (VatpHello.TryDecode(payload, out var hello, out _))
                        {
                            _maxFrame = Math.Min(hello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
                        }

                        lock (_gate)
                        {
                            _trace.Add("S HELLO");
                        }

                        Hello.TrySetResult();
                        break;
                    case VatpFrameType.Window:
                        Assert.True(VatpControlPayload.TryDecodeWindow(payload, out var add));
                        lock (_gate)
                        {
                            _trace.Add($"S WINDOW {add}");
                            _credit += add;
                            if (_dataFrames == 0)
                            {
                                if (FirstWindowCredit == 0)
                                {
                                    FirstWindowCredit = add;
                                }

                                _windowsBeforeData++;
                            }
                        }

                        WindowGranted.TrySetResult();
                        break;
                    case VatpFrameType.Result:
                        lock (_gate)
                        {
                            _trace.Add($"S RESULT {payload[0]}");
                        }

                        Result.TrySetResult(payload[0]);
                        break;
                    case VatpFrameType.Fail:
                        lock (_gate)
                        {
                            _trace.Add("S FAIL");
                        }

                        break;
                    default:
                        lock (_gate)
                        {
                            _trace.Add("S " + frame.Header.Type);
                        }

                        break;
                }
            }
        }
    }
}
