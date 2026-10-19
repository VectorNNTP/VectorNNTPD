using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Shared-configuration preparation for <see cref="ProviderArticleWorkHandler"/> is reused until the snapshot fields that feed it change.
    /// </summary>
    public sealed class ProviderArticleWorkHandlerConfigurationCacheTests
    {
        /// <summary>Path hop supplied by the fixture article. The materializer keeps it after the tracker and the application FQDN.</summary>
        private const string Peer = "peer.example";

        /// <summary>
        /// Two articles against one snapshot use the same parser instance and the same UTF-8 site-name array.
        /// </summary>
        [Fact]
        public async Task Unchanged_snapshot_reuses_parser_and_site_name_bytes()
        {
            var snapshot = Snapshot(1_048_576, "site-a.example", "a.test");
            var catalogue = new MutableCatalogue(snapshot);
            await using var retention = CreateRetention();
            var retriever = new RecordingRetriever();
            var injected = new NntpArticleParser("injected.example");
            var handler = CreateHandler(retriever, retention, catalogue, serverId: 1, injected);

            await HandleAsync(handler, "<cache-1@example.invalid>");
            var parser = handler.CachedParser;
            var siteName = handler.CachedSiteNameUtf8;
            Assert.NotNull(parser);
            Assert.NotSame(injected, parser);
            Assert.NotNull(siteName);
            Assert.Equal(Encoding.UTF8.GetBytes(snapshot.SiteName), siteName);
            Assert.Equal(snapshot.MaxArticleBytes, handler.CachedMaxArticleBytes);
            Assert.Equal(ExpectedFqdn(1, snapshot.DnsSuffix), handler.CachedEndpointFqdn);
            Assert.Equal(ExpectedPath(snapshot.SiteName, ExpectedFqdn(1, snapshot.DnsSuffix)), retriever.Observations[0].Path);
            Assert.Equal(snapshot.MaxArticleBytes, retriever.Observations[0].MaxArticleBytes);
            Assert.Equal(ExpectedFqdn(1, snapshot.DnsSuffix), handler.LastFqdn);

            await HandleAsync(handler, "<cache-2@example.invalid>");

            Assert.Same(parser, handler.CachedParser);
            Assert.Same(siteName, handler.CachedSiteNameUtf8);
            Assert.Equal(ExpectedPath(snapshot.SiteName, ExpectedFqdn(1, snapshot.DnsSuffix)), retriever.Observations[1].Path);

            const int iterations = 16;
            long allocated = 0;
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                await HandleAsync(handler, $"<cache-steady-{i}@example.invalid>");
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Same(parser, handler.CachedParser);
            Assert.Same(siteName, handler.CachedSiteNameUtf8);
            Console.WriteLine(
                $"shared-config steady articles n={iterations} alloc/op={allocated / iterations} (includes the test retriever's new payload and the canonical article; NullLogger does not format the Information event; parser and site-name bytes are the reused instances)");
        }

        /// <summary>
        /// A snapshot whose article limit, site name, or DNS suffix changed publishes a new preparation.
        /// A snapshot that differs only in an unused field keeps the previous instances.
        /// </summary>
        [Fact]
        public async Task Snapshot_field_changes_rebuild_and_unrelated_fields_do_not()
        {
            var first = Snapshot(1_048_576, "site-a.example", "a.test");
            var catalogue = new MutableCatalogue(first);
            await using var retention = CreateRetention();
            var retriever = new RecordingRetriever();
            var handler = CreateHandler(retriever, retention, catalogue, serverId: 1, new NntpArticleParser("injected.example"));
            await HandleAsync(handler, "<rebuild-1@example.invalid>");
            var parser = handler.CachedParser;
            var siteName = handler.CachedSiteNameUtf8;
            Assert.NotNull(parser);
            Assert.NotNull(siteName);

            catalogue.Publish(Snapshot(first.MaxArticleBytes, first.SiteName, first.DnsSuffix, "https://acme.other.test/directory"));
            await HandleAsync(handler, "<rebuild-2@example.invalid>");
            Assert.Same(parser, handler.CachedParser);
            Assert.Same(siteName, handler.CachedSiteNameUtf8);

            var limitOnly = Snapshot(2_097_152, first.SiteName, first.DnsSuffix);
            catalogue.Publish(limitOnly);
            await HandleAsync(handler, "<rebuild-3@example.invalid>");
            Assert.NotSame(parser, handler.CachedParser);
            Assert.NotSame(siteName, handler.CachedSiteNameUtf8);
            Assert.Equal(Encoding.UTF8.GetBytes(limitOnly.SiteName), handler.CachedSiteNameUtf8);
            Assert.Equal(limitOnly.MaxArticleBytes, retriever.Observations[^1].MaxArticleBytes);
            Assert.Equal(ExpectedPath(limitOnly.SiteName, ExpectedFqdn(1, limitOnly.DnsSuffix)), retriever.Observations[^1].Path);

            var second = Snapshot(2_097_152, "site-b.example", "b.test");
            catalogue.Publish(second);
            await HandleAsync(handler, "<rebuild-4@example.invalid>");
            Assert.NotSame(parser, handler.CachedParser);
            Assert.Equal(Encoding.UTF8.GetBytes(second.SiteName), handler.CachedSiteNameUtf8);
            Assert.Equal(ExpectedFqdn(1, second.DnsSuffix), handler.CachedEndpointFqdn);
            Assert.Equal(ExpectedPath(second.SiteName, ExpectedFqdn(1, second.DnsSuffix)), retriever.Observations[^1].Path);
            Assert.Equal(ExpectedFqdn(1, second.DnsSuffix), handler.LastFqdn);
            Assert.Equal(second.MaxArticleBytes, retriever.Observations[^1].MaxArticleBytes);
        }

        /// <summary>
        /// Server id 255 still builds <c>backfiller255</c>. Server ids outside 1–255 keep the injected parser and omit the endpoint.
        /// </summary>
        [Fact]
        public async Task Server_id_range_keeps_the_existing_fallback()
        {
            var snapshot = Snapshot(1_048_576, "site-a.example", "a.test");
            await using var upperRetention = CreateRetention();
            var upperRetriever = new RecordingRetriever();
            var upperInjected = new NntpArticleParser("injected.example");
            var upper = CreateHandler(upperRetriever, upperRetention, new MutableCatalogue(snapshot), serverId: 255, upperInjected);
            await HandleAsync(upper, "<sid-255@example.invalid>");
            Assert.NotSame(upperInjected, upper.CachedParser);
            Assert.Equal(ExpectedFqdn(255, snapshot.DnsSuffix), upper.CachedEndpointFqdn);
            Assert.Equal(ExpectedPath(snapshot.SiteName, ExpectedFqdn(255, snapshot.DnsSuffix)), upperRetriever.Observations[0].Path);
            Assert.Equal(ExpectedFqdn(255, snapshot.DnsSuffix), upper.LastFqdn);

            foreach (var serverId in new[] { 0, 256 })
            {
                await using var retention = CreateRetention();
                var retriever = new RecordingRetriever();
                var injected = new NntpArticleParser("injected.example");
                var catalogue = new MutableCatalogue(snapshot);
                var handler = CreateHandler(retriever, retention, catalogue, serverId, injected);
                await HandleAsync(handler, $"<sid-{serverId}@example.invalid>");
                var siteName = handler.CachedSiteNameUtf8;
                Assert.Same(injected, handler.CachedParser);
                Assert.Null(handler.CachedEndpointFqdn);
                Assert.Equal(snapshot.MaxArticleBytes, handler.CachedMaxArticleBytes);
                Assert.Equal(Encoding.UTF8.GetBytes(snapshot.SiteName), siteName);
                Assert.Equal($"{snapshot.SiteName}!injected.example!{Peer}", retriever.Observations[0].Path);
                Assert.Equal("retention.example", handler.LastFqdn);

                var renamed = Snapshot(snapshot.MaxArticleBytes, "site-b.example", snapshot.DnsSuffix);
                catalogue.Publish(renamed);
                await HandleAsync(handler, $"<sid-{serverId}-b@example.invalid>");
                Assert.Same(injected, handler.CachedParser);
                Assert.NotSame(siteName, handler.CachedSiteNameUtf8);
                Assert.Equal(Encoding.UTF8.GetBytes(renamed.SiteName), handler.CachedSiteNameUtf8);
                Assert.Equal($"{renamed.SiteName}!injected.example!{Peer}", retriever.Observations[1].Path);
            }
        }

        /// <summary>
        /// No catalogue leaves the injected parser in place and does not publish a preparation.
        /// </summary>
        [Fact]
        public async Task Absent_catalogue_keeps_the_injected_parser_and_default_tracker()
        {
            await using var retention = CreateRetention();
            var retriever = new RecordingRetriever();
            var injected = new NntpArticleParser("injected.example");
            var handler = CreateHandler(retriever, retention, catalogue: null, serverId: 1, injected);
            var first = await HandleAsync(handler, "<absent-1@example.invalid>");
            var second = await HandleAsync(handler, "<absent-2@example.invalid>");

            Assert.Equal(ArticleWorkOutcome.Success, first.Outcome);
            Assert.Equal(ArticleWorkOutcome.Success, second.Outcome);
            Assert.Null(handler.CachedParser);
            Assert.Null(handler.CachedSiteNameUtf8);
            Assert.Null(handler.CachedEndpointFqdn);
            Assert.Null(handler.CachedMaxArticleBytes);
            Assert.Equal(0, retriever.Observations[0].MaxArticleBytes);
            Assert.Equal(0, retriever.Observations[1].MaxArticleBytes);
            Assert.Equal($"news.usenet.ninja!injected.example!{Peer}", retriever.Observations[0].Path);
            Assert.Equal(retriever.Observations[0].Path, retriever.Observations[1].Path);
            Assert.Equal("retention.example", handler.LastFqdn);
        }

        /// <summary>
        /// An unpublished catalogue still throws, and a suffix that cannot build an FQDN does not replace a good preparation.
        /// </summary>
        [Fact]
        public async Task Unpublished_and_invalid_suffix_keep_the_previous_error_behavior()
        {
            await using var retention = CreateRetention();
            var unpublished = CreateHandler(
                new RecordingRetriever(),
                retention,
                new MutableCatalogue(),
                serverId: 1,
                new NntpArticleParser("injected.example"));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await HandleAsync(unpublished, "<missing@example.invalid>"));
            Assert.Equal("nntpsharedconfig has not been published.", missing.Message);
            Assert.Null(unpublished.CachedParser);

            var catalogue = new MutableCatalogue(Snapshot(1_048_576, "site-a.example", "a.test"));
            var retriever = new RecordingRetriever();
            var handler = CreateHandler(retriever, retention, catalogue, serverId: 1, new NntpArticleParser("injected.example"));
            await HandleAsync(handler, "<valid-before-bad-suffix@example.invalid>");
            var parser = handler.CachedParser;
            var siteName = handler.CachedSiteNameUtf8;
            catalogue.Publish(Snapshot(1_048_576, "site-a.example", "   "));
            await Assert.ThrowsAsync<ArgumentException>(async () => await HandleAsync(handler, "<bad-suffix@example.invalid>"));
            Assert.Same(parser, handler.CachedParser);
            Assert.Same(siteName, handler.CachedSiteNameUtf8);

            var recovered = Snapshot(1_048_576, "site-c.example", "c.test");
            catalogue.Publish(recovered);
            await HandleAsync(handler, "<recovered@example.invalid>");
            Assert.NotSame(parser, handler.CachedParser);
            Assert.Equal(ExpectedPath(recovered.SiteName, ExpectedFqdn(1, recovered.DnsSuffix)), retriever.Observations[^1].Path);
        }

        /// <summary>
        /// The cached preparation parses the same canonical bytes as a parser built directly from that snapshot.
        /// A multi-byte site name is stored as UTF-8 and produces the same factory outcome.
        /// </summary>
        [Fact]
        public async Task Cached_preparation_matches_a_direct_parse_including_utf8_site_name()
        {
            var snapshot = Snapshot(1_048_576, "site-a.example", "a.test");
            await using var retention = CreateRetention();
            var retriever = new RecordingRetriever();
            var handler = CreateHandler(
                retriever,
                retention,
                new MutableCatalogue(snapshot),
                serverId: 1,
                new NntpArticleParser("injected.example"));
            var result = await HandleAsync(handler, "<direct@example.invalid>");
            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            var direct = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(ExpectedFqdn(1, snapshot.DnsSuffix)),
                retriever.Observations[0].Payload,
                ArticlePathMode.Traverse,
                snapshot.MaxArticleBytes,
                Encoding.UTF8.GetBytes(snapshot.SiteName));
            try
            {
                Assert.True(direct.IsAccepted);
                Assert.Equal(direct.Record.ArtSize, retriever.Observations[0].LogicalBytes.Length);
                Assert.True(direct.Record.ArtData.Span.Slice(0, direct.Record.ArtSize).SequenceEqual(retriever.Observations[0].LogicalBytes));
            }
            finally
            {
                if (direct.IsAccepted)
                {
                    direct.Record.ReleaseUnusedCanonicalBuffer();
                }
            }

            var utf8Site = "caf\u00e9";
            var utf8 = Snapshot(1_048_576, utf8Site, "a.test");
            var utf8Retriever = new RecordingRetriever();
            var utf8Handler = CreateHandler(
                utf8Retriever,
                retention,
                new MutableCatalogue(utf8),
                serverId: 1,
                new NntpArticleParser("injected.example"));
            var utf8Result = await HandleAsync(utf8Handler, "<utf8@example.invalid>");
            Assert.Equal(Encoding.UTF8.GetBytes(utf8Site), utf8Handler.CachedSiteNameUtf8);
            var utf8Direct = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(ExpectedFqdn(1, utf8.DnsSuffix)),
                utf8Retriever.Observations[0].Payload,
                ArticlePathMode.Traverse,
                utf8.MaxArticleBytes,
                Encoding.UTF8.GetBytes(utf8Site));
            try
            {
                Assert.Equal(utf8Direct.IsAccepted, utf8Result.Outcome == ArticleWorkOutcome.Success);
                if (utf8Direct.IsAccepted)
                {
                    Assert.True(utf8Direct.Record.ArtData.Span.Slice(0, utf8Direct.Record.ArtSize).SequenceEqual(utf8Retriever.Observations[0].LogicalBytes));
                }
                else
                {
                    var expectedError = utf8Direct.ParseFailure != NntpArticleParseFailureCode.None
                        ? utf8Direct.ParseFailure.ToString()
                        : utf8Direct.MaterializeFailure.ToString();
                    Assert.Equal(ArticleWorkOutcome.InvalidArticle, utf8Result.Outcome);
                    Assert.Equal(expectedError, utf8Result.Error);
                }
            }
            finally
            {
                if (utf8Direct.IsAccepted)
                {
                    utf8Direct.Record.ReleaseUnusedCanonicalBuffer();
                }
            }
        }

        /// <summary>
        /// Concurrent replacement and article handling never pair one snapshot's limit with another snapshot's path.
        /// </summary>
        [Fact]
        public async Task Concurrent_replacement_keeps_each_article_on_one_snapshot()
        {
            var first = Snapshot(1_048_576, "site-a.example", "a.test");
            var second = Snapshot(2_097_152, "site-b.example", "b.test");
            var catalogue = new MutableCatalogue(first);
            await using var retention = CreateRetention();
            var retriever = new RecordingRetriever();
            var handler = CreateHandler(retriever, retention, catalogue, serverId: 1, new NntpArticleParser("injected.example"));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = 0;
            var workers = new Task[4];
            for (var worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    await release.Task.ConfigureAwait(false);
                    for (var i = 0; i < 25; i++)
                    {
                        var n = Interlocked.Increment(ref next);
                        await handler.HandleAsync(Item($"<mix-{n}@example.invalid>"), CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            var flipper = Task.Run(async () =>
            {
                await release.Task.ConfigureAwait(false);
                for (var i = 0; i < 200; i++)
                {
                    catalogue.Publish(i % 2 == 0 ? first : second);
                }
            });
            release.SetResult();
            await Task.WhenAll(workers.Append(flipper));

            Assert.Equal(100, retriever.Observations.Count);
            foreach (var observation in retriever.Observations)
            {
                var firstPath = ExpectedPath(first.SiteName, ExpectedFqdn(1, first.DnsSuffix));
                var secondPath = ExpectedPath(second.SiteName, ExpectedFqdn(1, second.DnsSuffix));
                if (observation.MaxArticleBytes == first.MaxArticleBytes)
                {
                    Assert.Equal(firstPath, observation.Path);
                }
                else
                {
                    Assert.Equal(second.MaxArticleBytes, observation.MaxArticleBytes);
                    Assert.Equal(secondPath, observation.Path);
                }
            }
        }

        /// <summary>Handles one article and returns the handler result.</summary>
        /// <param name="handler">Handler under test.</param>
        /// <param name="messageId">Request and article Message-ID.</param>
        /// <returns>The handler result.</returns>
        private static ValueTask<ArticleWorkHandlerResult> HandleAsync(ProviderArticleWorkHandler handler, string messageId) =>
            handler.HandleAsync(Item(messageId), CancellationToken.None);

        /// <summary>Builds the handler with the test catalogue and server id.</summary>
        private static ProviderArticleWorkHandler CreateHandler(
            INntpArticleRetriever retriever,
            ArticleRetentionAuthority retention,
            INntpSharedConfigurationCatalogue? catalogue,
            int serverId,
            NntpArticleParser parser) =>
            new(retriever, retention, parser, catalogue, serverId, NullLogger<ProviderArticleWorkHandler>.Instance);

        /// <summary>Creates a retention authority whose own FQDN is distinct from a built application FQDN.</summary>
        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(4 * 1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 8),
                "retention.example",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        /// <summary>Builds one shared snapshot. Fields other than the three cache inputs stay at the test placeholders.</summary>
        private static NntpSharedConfiguration Snapshot(int maxArticleBytes, string siteName, string dnsSuffix, string? directory = null) =>
            new(
                maxArticleBytes,
                siteName,
                null,
                directory ?? "https://acme.example.test/directory",
                14,
                "zone",
                dnsSuffix);

        /// <summary>Application FQDN the handler builds for <paramref name="serverId"/> and <paramref name="dnsSuffix"/>.</summary>
        private static string ExpectedFqdn(int serverId, string dnsSuffix) =>
            ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, dnsSuffix);

        /// <summary>Canonical Path value for a traverse of <paramref name="siteName"/> and <paramref name="fqdn"/>.</summary>
        private static string ExpectedPath(string siteName, string fqdn) => $"{siteName}!{fqdn}!{Peer}";

        /// <summary>Minimal work item. Settlement is not asserted.</summary>
        private static ArticleWorkItem Item(string messageId)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, "Giganews"),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        /// <summary>One observed article: the limit passed to retrieval and the canonical Path.</summary>
        /// <param name="MaxArticleBytes">Limit supplied to <see cref="INntpArticleRetriever.RetrieveAsync"/>.</param>
        /// <param name="Path">Canonical Path value.</param>
        /// <param name="Payload">Destuffed bytes passed to the handler callback.</param>
        /// <param name="LogicalBytes">Accepted canonical article, or empty when creation was rejected.</param>
        private readonly record struct Observation(int MaxArticleBytes, string Path, byte[] Payload, byte[] LogicalBytes);

        /// <summary>
        /// Retriever that parses the fixture article for the request Message-ID and records the preparation the callback observed.
        /// </summary>
        private sealed class RecordingRetriever : INntpArticleRetriever
        {
            /// <summary>Observations appended under <see cref="_gate"/>.</summary>
            private readonly List<Observation> _observations = [];

            /// <summary>Guards <see cref="_observations"/>.</summary>
            private readonly Lock _gate = new();

            /// <summary>Copy of the observations so far.</summary>
            internal IReadOnlyList<Observation> Observations
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _observations];
                    }
                }
            }

            /// <inheritdoc />
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(item);
                ArgumentNullException.ThrowIfNull(consumePayload);
                cancellationToken.ThrowIfCancellationRequested();
                var payload = ArticleWorkTestArticles.Valid(item.Request.MessageId, extraHeaders: "Path: " + Peer + "\r\n");
                var created = consumePayload(payload);
                var path = string.Empty;
                var logical = Array.Empty<byte>();
                if (created.IsAccepted)
                {
                    logical = created.Record.ArtData.Span.Slice(0, created.Record.ArtSize).ToArray();
                    path = PathValue(logical);
                }

                lock (_gate)
                {
                    _observations.Add(new Observation(maxArticleBytes, path, payload, logical));
                }

                var result = ArticleRetrievalResult.Retrieved(220, "follows");
                result.CommandStartedTimestamp = 0;
                return Task.FromResult(result);
            }

            /// <summary>Reads the Path header value from canonical article bytes.</summary>
            private static string PathValue(byte[] article)
            {
                var text = Encoding.ASCII.GetString(article);
                const string prefix = "Path: ";
                var start = text.IndexOf(prefix, StringComparison.Ordinal);
                Assert.True(start >= 0);
                start += prefix.Length;
                var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
                Assert.True(end > start);
                return text[start..end];
            }
        }

        /// <summary>Test catalogue. <see cref="Current"/> throws until <see cref="Publish"/>.</summary>
        private sealed class MutableCatalogue : INntpSharedConfigurationCatalogue
        {
            /// <summary>Guards <see cref="_current"/> and <see cref="_published"/>.</summary>
            private readonly Lock _gate = new();

            /// <summary>Last published snapshot.</summary>
            private NntpSharedConfiguration _current;

            /// <summary>False until <see cref="Publish"/>.</summary>
            private bool _published;

            /// <summary>Creates a catalogue with nothing published.</summary>
            internal MutableCatalogue()
            {
            }

            /// <summary>Creates a catalogue that already publishes <paramref name="current"/>.</summary>
            /// <param name="current">Initial snapshot.</param>
            internal MutableCatalogue(NntpSharedConfiguration current)
            {
                _current = current;
                _published = true;
            }

            /// <inheritdoc />
            public NntpSharedConfiguration Current
            {
                get
                {
                    lock (_gate)
                    {
                        if (!_published)
                        {
                            throw new InvalidOperationException("nntpsharedconfig has not been published.");
                        }

                        return _current;
                    }
                }
            }

            /// <summary>Replaces the published snapshot.</summary>
            /// <param name="current">Snapshot returned by the next <see cref="Current"/>.</param>
            internal void Publish(NntpSharedConfiguration current)
            {
                lock (_gate)
                {
                    _current = current;
                    _published = true;
                }
            }
        }
    }
}
