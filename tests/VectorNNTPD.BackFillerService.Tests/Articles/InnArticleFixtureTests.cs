using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Dot-stuffing and unstuffing against the imported INN article bytes.
    /// </summary>
    public sealed class InnArticleFixtureTests
    {
        /// <summary>Imported INN logical fixture names exercised by theory data and stuffing tests.</summary>
        private static readonly string[] LogicalArticles =
        [
            "1", "2", "3", "4", "5", "6", "7", "xref",
            "bad-empty", "bad-hdr-empty", "bad-hdr-nospc", "bad-hdr-space", "bad-hdr-trunc",
            "bad-long-cont", "bad-long-hdr", "bad-msgid", "bad-no-body", "bad-no-header",
            "bad-nul-body", "bad-nul-header", "bad-subj",
        ];

        /// <summary>Theory rows naming each logical INN fixture under <c>tests/fixtures/articles</c>.</summary>
        /// <returns>Single-element object arrays containing fixture names.</returns>
        public static IEnumerable<object[]> LogicalArticleNames()
        {
            foreach (var name in LogicalArticles)
            {
                yield return [name];
            }
        }

        /// <summary>NNTP framing and reader destuffing round-trip each logical fixture to identical bytes.</summary>
        /// <param name="name">Fixture file name without path.</param>
        [Theory]
        [MemberData(nameof(LogicalArticleNames))]
        public async Task Framed_fixture_round_trips_through_the_reader(string name)
        {
            var logical = Load(name);
            var framed = FrameLogicalArticle(logical);
            var read = await ReadPayloadAsync(framed);

            Assert.Equal(logical, read);
        }

        /// <summary>Body stuffer adds one dot per dot-leading body line for every logical INN fixture.</summary>
        /// <param name="name">Fixture file name without path.</param>
        [Theory]
        [MemberData(nameof(LogicalArticleNames))]
        public void Stuffer_adds_one_dot_only_on_body_lines_that_begin_with_a_dot(string name)
        {
            var logical = Load(name);
            var stuffed = ArticleBodyWireStuffer.StuffBody(logical);
            AssertStuffedBody(logical, stuffed);
        }

        /// <summary>Pre-framed <c>wire-7</c> destuffs to CRLF-normalized native <c>7</c> and matches stuffer expectations.</summary>
        [Fact]
        public async Task Wire7_destuffs_to_the_crlf_form_of_native_7()
        {
            var native = Load("7");
            var expected = ConvertBareLfToCrlf(native);
            var read = await ReadPayloadAsync(Load("wire-7"));

            Assert.Equal(expected, read);
            AssertStuffedBody(expected, ArticleBodyWireStuffer.StuffBody(expected));
        }

        /// <summary>Truncated wire fixtures must fail destuffing with <see cref="EndOfStreamException"/>.</summary>
        /// <param name="name">Incomplete wire fixture name.</param>
        [Theory]
        [InlineData("wire-no-body")]
        [InlineData("wire-strange")]
        [InlineData("wire-truncated")]
        public async Task Incomplete_wire_fixtures_end_before_the_terminator(string name)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => ReadPayloadAsync(Load(name)));
        }

        /// <summary>Accepted INN samples retain stuffed body bytes through canonical materialization.</summary>
        [Fact]
        public void Accepted_articles_keep_the_stuffed_body_through_canonical_materialization()
        {
            var parser = new NntpArticleParser("backfiller.test");
            foreach (var name in new[] { "1", "2", "3", "6", "7", "xref" })
            {
                var logical = Load(name);
                var parsed = parser.Parse(logical);
                Assert.True(parsed.IsAccepted, name + " " + parsed.FailureCode);

                var materialized = NntpArticleCanonicalMaterializer.Materialize(
                    in parsed,
                    ArticlePathMode.Normalize);
                Assert.True(materialized.IsAccepted, name);
                if (materialized.ArticleBytes is not { } canonical)
                {
                    throw new InvalidOperationException(name);
                }

                var logicalBody = logical.AsSpan(BodyStart(logical));
                var canonicalBody = canonical.AsSpan(BodyStart(canonical.AsSpan(0, materialized.CanonicalLength)));
                var stuffed = ArticleBodyWireStuffer.StuffBody(logical);
                Assert.Equal(stuffed.AsSpan(BodyStart(stuffed)).ToArray(), canonicalBody.ToArray());
                Assert.Equal(CountLeadingDotLines(logicalBody), ArticleBodyWireStuffer.CountExtraDots(logicalBody));
            }
        }

        /// <summary>NUL in the body of <c>bad-nul-body</c> remains an accepted parse (legacy INN behavior).</summary>
        [Fact]
        public void Body_nul_is_not_a_parse_failure()
        {
            var parsed = new NntpArticleParser("backfiller.test").Parse(Load("bad-nul-body"));
            Assert.True(parsed.IsAccepted);
        }

        /// <summary>Negative fixtures map to the expected parse failure codes.</summary>
        /// <param name="name">Fixture file name.</param>
        /// <param name="expected">Expected rejection reason.</param>
        [Theory]
        [InlineData("bad-nul-header", nameof(NntpArticleParseFailureCode.ContainsIllegalControlByte))]
        [InlineData("bad-no-body", nameof(NntpArticleParseFailureCode.MissingHeaderBodySeparator))]
        [InlineData("bad-no-header", nameof(NntpArticleParseFailureCode.MissingMessageId))]
        public void Parser_rejects_incomplete_or_nul_fixtures(string name, string expected)
        {
            var parsed = new NntpArticleParser("backfiller.test").Parse(Load(name));
            Assert.False(parsed.IsAccepted);
            Assert.Equal(Enum.Parse<NntpArticleParseFailureCode>(expected), parsed.FailureCode);
        }

        /// <summary>Compares logical and stuffed bodies line-by-line, accounting for one added dot per dot-leading line.</summary>
        /// <param name="logical">Unstuffed article bytes.</param>
        /// <param name="stuffed">Output of <see cref="ArticleBodyWireStuffer.StuffBody"/>.</param>
        private static void AssertStuffedBody(byte[] logical, byte[] stuffed)
        {
            var body = BodyStart(logical);
            Assert.Equal(logical.AsSpan(0, body).ToArray(), stuffed.AsSpan(0, Math.Min(body, stuffed.Length)).ToArray());

            var leadingDots = CountLeadingDotLines(logical.AsSpan(body));
            if (leadingDots == 0)
            {
                Assert.Same(logical, stuffed);
                return;
            }

            var logicalIndex = body;
            var stuffedIndex = body;
            while (logicalIndex < logical.Length)
            {
                var lineEnd = logicalIndex;
                while (lineEnd < logical.Length && logical[lineEnd] != (byte)'\n')
                {
                    lineEnd++;
                }

                if (lineEnd < logical.Length)
                {
                    lineEnd++;
                }

                var line = logical.AsSpan(logicalIndex, lineEnd - logicalIndex);
                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    Assert.Equal((byte)'.', stuffed[stuffedIndex]);
                    stuffedIndex++;
                }

                Assert.True(line.SequenceEqual(stuffed.AsSpan(stuffedIndex, line.Length)));
                stuffedIndex += line.Length;
                logicalIndex = lineEnd;
            }

            Assert.Equal(stuffed.Length, stuffedIndex);
            Assert.Equal(logical.Length + leadingDots, stuffed.Length);
        }

        /// <summary>Counts body lines that begin with a dot (expected stuffing candidates).</summary>
        /// <param name="body">Body span after the header separator.</param>
        /// <returns>Number of dot-leading lines.</returns>
        private static int CountLeadingDotLines(ReadOnlySpan<byte> body)
        {
            var count = 0;
            var index = 0;
            while (index < body.Length)
            {
                if (body[index] == (byte)'.')
                {
                    count++;
                }

                var newline = body[index..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    break;
                }

                index += newline + 1;
            }

            return count;
        }

        /// <summary>Index of the first body byte after the earliest recognized header/body separator.</summary>
        /// <param name="article">Full article span.</param>
        /// <returns>Body start offset, or <paramref name="article"/>.Length when no separator exists.</returns>
        private static int BodyStart(ReadOnlySpan<byte> article)
        {
            var crlf = article.IndexOf("\r\n\r\n"u8);
            var lf = article.IndexOf("\n\n"u8);
            if (crlf < 0 && lf < 0)
            {
                return article.Length;
            }

            if (crlf < 0)
            {
                return lf + 2;
            }

            if (lf < 0)
            {
                return crlf + 4;
            }

            return crlf < lf ? crlf + 4 : lf + 2;
        }

        /// <summary>
        /// INN <c>wire-7</c> is native <c>7</c> with bare LF converted to CRLF, one extra dot on every
        /// line that already begins with a dot, and a terminating <c>.CRLF</c>. This helper builds only
        /// the CRLF form. The reader is what removes the dots and the terminator.
        /// </summary>
        private static byte[] ConvertBareLfToCrlf(ReadOnlySpan<byte> native)
        {
            var extra = 0;
            for (var i = 0; i < native.Length; i++)
            {
                if (native[i] == (byte)'\n' && (i == 0 || native[i - 1] != (byte)'\r'))
                {
                    extra++;
                }
            }

            var converted = new byte[native.Length + extra];
            var written = 0;
            for (var i = 0; i < native.Length; i++)
            {
                if (native[i] == (byte)'\n' && (i == 0 || native[i - 1] != (byte)'\r'))
                {
                    converted[written++] = (byte)'\r';
                }

                converted[written++] = native[i];
            }

            return converted;
        }

        /// <summary>Builds NNTP multiline framing with dot stuffing and terminator for reader tests.</summary>
        /// <param name="logical">Destuffed logical bytes.</param>
        /// <returns>Framed wire bytes.</returns>
        private static byte[] FrameLogicalArticle(ReadOnlySpan<byte> logical)
        {
            var extra = 0;
            var index = 0;
            while (index < logical.Length)
            {
                if (logical[index] == (byte)'.')
                {
                    extra++;
                }

                var newline = logical[index..].IndexOf((byte)'\n');
                index = newline < 0 ? logical.Length : index + newline + 1;
            }

            var framed = new byte[logical.Length + extra + 3];
            var written = 0;
            index = 0;
            while (index < logical.Length)
            {
                if (logical[index] == (byte)'.')
                {
                    framed[written++] = (byte)'.';
                }

                var newline = logical[index..].IndexOf((byte)'\n');
                var length = newline < 0 ? logical.Length - index : newline + 1;
                logical.Slice(index, length).CopyTo(framed.AsSpan(written));
                written += length;
                index += length;
            }

            framed[written++] = (byte)'.';
            framed[written++] = (byte)'\r';
            framed[written] = (byte)'\n';
            return framed;
        }

        /// <summary>Reads and destuffs a framed payload using production reader limits.</summary>
        /// <param name="framed">NNTP multiline bytes including terminator.</param>
        /// <returns>Logical article bytes.</returns>
        private static async Task<byte[]> ReadPayloadAsync(byte[] framed)
        {
            var stream = new MemoryStream(framed, writable: false);
            var reader = new NntpStreamReader(stream, 4096);
            return await reader.ReadArticlePayloadAsync(
                VectorNNTPD.Common.Articles.ArticleResourceLimits.MaxArticleBytes,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
        }

        /// <summary>Loads one INN fixture file from the test articles directory.</summary>
        /// <param name="name">Fixture file name.</param>
        /// <returns>Raw file bytes.</returns>
        private static byte[] Load(string name)
            => File.ReadAllBytes(RepositoryFiles.Under("tests", "fixtures", "articles", name));
    }
}
