using System.IO.Hashing;
using System.Runtime.InteropServices;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.Retention;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Tests.Listener
{
    /// <summary>
    /// Cache listener NNTP subset: ARTICLE framing, DATE, QUIT, 403/430/501 responses, pipelining, and delivery claims.
    /// </summary>

    public sealed class NntpCacheListenerSessionTests
    {
        /// <summary>Expected 201 posting-prohibited banner emitted at session start.</summary>
        private static ReadOnlySpan<byte> Greeting => "201 NNTP Service Ready, posting prohibited\r\n"u8;

        /// <summary>Expected dot-terminated multiline end marker after ARTICLE payloads.</summary>
        private static ReadOnlySpan<byte> Terminator => ".\r\n"u8;

        /// <summary>
        /// Successful ARTICLE emits greeting, 220 status, retained bytes, one terminator; claim consumes retention.
        /// </summary>

        [Fact]

        public async Task Article_writes_the_status_line_exact_bytes_and_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<Ab@Cd>");
            var written = await RunAsync(authority, "ARTICLE <Ab@Cd>\r\n");

            var follows = Encoding.ASCII.GetBytes("220 0 <Ab@Cd> article follows\r\n");
            Assert.True(written.AsSpan(0, Greeting.Length).SequenceEqual(Greeting));
            var bodyStart = Greeting.Length + follows.Length;
            Assert.True(written.AsSpan(Greeting.Length, follows.Length).SequenceEqual(follows));
            Assert.True(written.AsSpan(bodyStart, prepared.ArtData.Length).SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(bodyStart + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        /// <summary>
        /// Pre-stuffed retained bytes are sent unchanged with a single trailing terminator.
        /// </summary>

        [Fact]

        public async Task Existing_dot_stuffing_is_preserved_and_exactly_one_terminator_is_appended()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", ".\r\n");
            Assert.Contains(Encoding.ASCII.GetBytes("\r\n..\r\n"), prepared.ArtData.AsSpan());

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            var follows = Status("<a@b>");
            var article = written.AsSpan(Greeting.Length + follows.Length, prepared.ArtData.Length);
            Assert.True(article.SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Articles not ending in CRLF return 403 without mutating retained bytes.
        /// </summary>

        [Theory]
        [InlineData("cr-only\r")]
        [InlineData("lf-only\n")]

        public async Task Article_ending_in_cr_or_lf_only_is_refused_without_mutation(string body)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.ArtData.ToArray();
            Assert.False(before.AsSpan().EndsWith("\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nQUIT\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(Encoding.ASCII.GetBytes(body), written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        /// <summary>
        /// Interior bare CR/LF in retained payload yields 403 before any 220 body bytes.
        /// </summary>

        [Theory]
        [InlineData("plain\nmid.dot\r\n")]
        [InlineData("plain\rmid.dot\r\n")]

        public async Task Interior_bare_cr_or_lf_returns_403_before_any_article_byte(string body)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.ArtData.ToArray();
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        /// <summary>
        /// NUL in retained article yields 403; retention and bytes remain unchanged.
        /// </summary>

        [Fact]

        public async Task Nul_in_a_retained_article_is_403_before_any_article_byte()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "body\r\n");
            var body = prepared.Record.ArtData.Span.IndexOf("body\r\n"u8);
            Assert.True(body > 0);
            WriteRetainedByte(prepared.Record, body, 0);
            var before = prepared.Record.ArtData.ToArray();
            Assert.Contains(before, static value => value == 0);
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<a@b>")).Kind);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        /// <summary>
        /// Not-frameable dot-leading header pattern returns 403 without wire body.
        /// </summary>

        [Fact]

        public async Task Dot_leading_line_without_a_separator_is_403_before_any_article_byte()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", ".only\r\n");
            var separator = prepared.Record.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            WriteRetainedByte(prepared.Record, separator, (byte)'Z');
            WriteRetainedByte(prepared.Record, separator + 1, (byte)'Z');
            var before = prepared.Record.ArtData.ToArray();
            Assert.True(before.AsSpan().IndexOf("\r\n\r\n"u8) < 0);
            Assert.Contains("..only\r\n"u8, before);

            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<a@b>")).Kind);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        /// <summary>
        /// Header dot-stuffing is applied on transmit; client destuff matches logical article bytes.
        /// </summary>

        [Fact]

        public async Task Dot_leading_header_is_stuffed_on_the_wire_and_the_body_is_decoded_once()
        {
            var logical = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: <dot-header@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: dots\r\n"
                + "Path: peer.example\r\n"
                + ".X-Dot: kept\r\n"
                + "\r\n"
                + ".only\r\n"
                + "..two\r\n"
                + "...three\r\n"
                + "mid.dot\r\n");
            var parser = new NntpArticleParser("backfiller.test");
            var created = ArticleRecordFactory.TryCreate(parser, logical);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var retained = created.Record.ArtData.ToArray();
            Assert.Contains(".X-Dot: kept\r\n"u8, retained);
            Assert.DoesNotContain("..X-Dot:"u8, retained);
            Assert.Contains("..only\r\n"u8, retained);
            Assert.Contains("...two\r\n"u8, retained);
            Assert.Contains("....three\r\n"u8, retained);

            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    "<dot-header@example.test>",
                    Guid.NewGuid(),
                    created.Record,
                    created.SelectedDateHeaderName).Kind);

            var written = await RunAsync(authority, "ARTICLE <dot-header@example.test>\r\n");
            var follows = Status("<dot-header@example.test>");
            var article = written.AsSpan(Greeting.Length + follows.Length);
            Assert.True(article.EndsWith(Terminator));
            Assert.Equal(1, Count(article, Terminator));
            var block = article[..^Terminator.Length];
            Assert.Contains("..X-Dot: kept\r\n"u8, block);
            Assert.Contains("..only\r\n"u8, block);
            Assert.Contains("...two\r\n"u8, block);
            Assert.Contains("....three\r\n"u8, block);
            Assert.DoesNotContain("...only"u8, block);
            Assert.DoesNotContain(".....three"u8, block);

            var decoded = DestuffMultiline(block);
            Assert.True(decoded.AsSpan().SequenceEqual(LogicalFromRetained(retained)));
            Assert.Contains(".only\r\n"u8, decoded);
            Assert.Contains("..two\r\n"u8, decoded);
            Assert.Contains("...three\r\n"u8, decoded);
            Assert.DoesNotContain("..only"u8, decoded);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(retained));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        /// <summary>
        /// Wire bytes read back through NntpStreamReader match expected logical article after stuff/destuff.
        /// </summary>

        [Fact]

        public async Task Listener_wire_round_trips_through_nntp_stream_reader()
        {
            var logical = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: <dot-roundtrip@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: dots\r\n"
                + "Path: peer.example\r\n"
                + ".X-Dot: kept\r\n"
                + "\r\n"
                + ".only\r\n"
                + ".\r\n"
                + "..two\r\n"
                + "...three\r\n"
                + "after\r\n");
            var parser = new NntpArticleParser("backfiller.test");
            var created = ArticleRecordFactory.TryCreate(parser, logical);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var retained = created.Record.ArtData.ToArray();
            var retainedSeparator = retained.AsSpan().IndexOf("\r\n\r\n"u8);
            var logicalSeparator = logical.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(retainedSeparator > 0);
            Assert.True(logicalSeparator > 0);
            Assert.Contains(".X-Dot: kept\r\n"u8, retained.AsSpan(0, retainedSeparator + 4));
            Assert.DoesNotContain("..X-Dot:"u8, retained);
            Assert.Contains("..only\r\n"u8, retained);
            Assert.Contains("..\r\n...two\r\n"u8, retained);
            Assert.Contains("....three\r\n"u8, retained);

            var expected = new byte[retainedSeparator + 4 + (logical.Length - (logicalSeparator + 4))];
            retained.AsSpan(0, retainedSeparator + 4).CopyTo(expected);
            logical.AsSpan(logicalSeparator + 4).CopyTo(expected.AsSpan(retainedSeparator + 4));

            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    "<dot-roundtrip@example.test>",
                    Guid.NewGuid(),
                    created.Record,
                    created.SelectedDateHeaderName).Kind);

            var written = await RunAsync(authority, "ARTICLE <dot-roundtrip@example.test>\r\n");
            var follows = Status("<dot-roundtrip@example.test>");
            Assert.True(written.AsSpan(0, Greeting.Length).SequenceEqual(Greeting));
            Assert.True(written.AsSpan(Greeting.Length, follows.Length).SequenceEqual(follows));
            var multiline = written.AsSpan(Greeting.Length + follows.Length).ToArray();
            Assert.True(multiline.AsSpan().EndsWith(Terminator));
            Assert.Contains("..X-Dot: kept\r\n"u8, multiline);
            Assert.Contains("..\r\n...two\r\n"u8, multiline);

            var contiguous = await ReadWithProductionReaderAsync(multiline, chunk: int.MaxValue);
            var fragmented = await ReadWithProductionReaderAsync(multiline, chunk: 1);

            Assert.Equal(expected, contiguous);
            Assert.Equal(expected, fragmented);
            Assert.Contains(".X-Dot: kept\r\n"u8, contiguous);
            Assert.DoesNotContain("..X-Dot:"u8, contiguous);
            Assert.Contains(".only\r\n"u8, contiguous);
            Assert.Contains(".\r\n..two\r\n"u8, contiguous);
            Assert.Contains("...three\r\n"u8, contiguous);
            Assert.Contains("after\r\n"u8, contiguous);
            Assert.DoesNotContain("..only\r\n"u8, contiguous);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(retained));
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// An empty body stays empty in ArtData. The listener writes that buffer once and appends one terminator.
        /// </summary>
        [Fact]
        public async Task Empty_body_is_sent_once_with_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<empty@b>", string.Empty);
            var separator = prepared.ArtData.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.Equal(prepared.ArtData.Length, separator + 4);
            Assert.Equal(0, prepared.Record.ArtLines);
            Assert.Equal(prepared.ArtData.Length, prepared.Record.ArtSize);
            Assert.Equal(XxHash3.HashToUInt64(prepared.ArtData), prepared.Record.ArtHash);
            Assert.Equal(ArticleId.FromMessageId("<empty@b>"u8), prepared.Record.ArtId);
            Assert.False(prepared.ArtData.AsSpan().EndsWith("\r\n.\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <empty@b>\r\n");
            var follows = Status("<empty@b>");
            var article = written.AsSpan(Greeting.Length + follows.Length, prepared.ArtData.Length);
            Assert.True(article.SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(written.Length, Greeting.Length + follows.Length + prepared.ArtData.Length + Terminator.Length);
            Assert.Equal(1, Count(written, Terminator));
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// CRLF-terminated retained article is sent once with exactly one terminator line.
        /// </summary>

        [Fact]

        public async Task Article_ending_in_crlf_is_transmitted_once_with_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "ends-crlf\r\n");
            Assert.True(prepared.ArtData.AsSpan().EndsWith("\r\n"u8));
            Assert.False(prepared.ArtData.AsSpan().EndsWith("\r\n.\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            var follows = Status("<a@b>");
            var article = written.AsSpan(Greeting.Length + follows.Length, prepared.ArtData.Length);
            Assert.True(article.SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(1, Count(written, Terminator));
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Unterminated retained article returns 403 and preserves retention.
        /// </summary>

        [Fact]

        public async Task Non_crlf_terminated_article_is_refused_without_mutation()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "unterminated");
            var before = prepared.ArtData.ToArray();

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nQUIT\r\n");

            Assert.Equal(Greeting.Length + "403 Article not available\r\n"u8.Length + "205 Connection closing\r\n"u8.Length, written.Length);
            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain(Encoding.ASCII.GetBytes("unterminated"), written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        /// <summary>
        /// Unknown id 430; malformed ARTICLE/QUIT forms return documented 4xx/5xx without leaking bodies.
        /// </summary>

        [Fact]

        public async Task Missing_article_is_430_and_invalid_forms_use_501_or_412()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var written = await RunAsync(
                authority,
                "ARTICLE <missing@b>\r\n"
                + "ARTICLE\r\n"
                + "ARTICLE 15\r\n"
                + "ARTICLE <not-an-id>\r\n"
                + "ARTICLE <a@b> extra\r\n"
                + "NOPE\r\n"
                + "QUIT extra\r\n"
                + "QUIT\r\n");

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("430 No Such Article Found\r\n"u8.ToArray())
                    .Concat("412 No newsgroup selected\r\n"u8.ToArray())
                    .Concat("412 No newsgroup selected\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        /// <summary>
        /// ARTICLE command matching is case-insensitive with flexible whitespace.
        /// </summary>

        [Fact]

        public async Task Keywords_are_case_insensitive_and_spaces_or_tabs_separate_the_argument()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "article\t<a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Extra spaces between ARTICLE and message-id are accepted.
        /// </summary>

        [Fact]

        public async Task Repeated_spaces_are_accepted()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "ARTICLE   <a@b>  \r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Line over 512 bytes yields one 501; subsequent command still processed.
        /// </summary>

        [Fact]

        public async Task Overlong_line_is_one_501_and_the_following_command_is_preserved()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var overlong = new string('X', 511) + "\r\n";
            var written = await RunAsync(authority, overlong + "ARTICLE <a@b>\r\n", maxRead: 64);

            Assert.Contains("501 Syntax error\r\n"u8, written);
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Exactly 512-byte command line is parsed as a single unknown command.
        /// </summary>

        [Fact]

        public async Task A_512_octet_line_is_parsed_as_one_command()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var line = new string('Y', 510) + "\r\n";
            Assert.Equal(512, Encoding.ASCII.GetByteCount(line));
            var written = await RunAsync(authority, line + "QUIT\r\n", maxRead: 64);
            Assert.Equal(
                Greeting.ToArray()
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        /// <summary>
        /// Multiple ARTICLE commands in one read preserve order and payloads.
        /// </summary>

        [Fact]

        public async Task Pipelined_commands_are_processed_in_order()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var first = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var second = RetentionTestArticles.RetainPrepared(authority, "<c@d>");
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nARTICLE <c@d>\r\n");

            var firstStatus = IndexOf(written, Status("<a@b>"));
            var secondStatus = IndexOf(written, Status("<c@d>"));
            Assert.True(firstStatus >= 0 && secondStatus > firstStatus);
            Assert.True(written.AsSpan(firstStatus + Status("<a@b>").Length, first.ArtData.Length).SequenceEqual(first.ArtData));
            Assert.True(written.AsSpan(secondStatus + Status("<c@d>").Length, second.ArtData.Length).SequenceEqual(second.ArtData));
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// QUIT closes session without processing pipelined commands after it.
        /// </summary>

        [Fact]

        public async Task Quit_does_not_process_a_following_pipelined_command()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "QUIT\r\nARTICLE <a@b>\r\n");
            Assert.Equal(
                Greeting.ToArray().Concat("205 Connection closing\r\n"u8.ToArray()).ToArray(),
                written);
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>
        /// Partial command with EOF yields greeting only.
        /// </summary>

        [Fact]

        public async Task Peer_close_before_a_command_sends_no_further_response()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var written = await RunAsync(authority, "ART");
            Assert.True(written.AsSpan().SequenceEqual(Greeting));
        }

        /// <summary>
        /// IO failures during status/body/terminator writes roll back claim; retention remains for retry.
        /// </summary>

        [Fact]

        public async Task Failed_status_body_and_terminator_writes_do_not_consume_the_entry()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var command = "ARTICLE <a@b>\r\n";

            var status = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 2 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(status, authority).RunAsync(CancellationToken.None));
            Assert.True(status.Written.AsSpan().SequenceEqual(Greeting));
            Assert.Equal(1, authority.RetainedCount);

            var body = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 3 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(body, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.DoesNotContain(prepared.ArtData, body.Written);

            var follows = Status("<a@b>");
            var midBody = new ScriptedTransport(Encoding.ASCII.GetBytes(command))
            {
                FailAfterAcceptedBytes = Greeting.Length + follows.Length + 4,
            };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(midBody, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(Greeting.Length + follows.Length + 4, midBody.Written.Length);
            Assert.False(midBody.Written.AsSpan(midBody.Written.Length - 3).SequenceEqual(Terminator));

            var terminator = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 4 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(terminator, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(prepared.ArtData.Length, authority.RetainedPayloadBytes);
            Assert.Contains(prepared.ArtData, terminator.Written);
            Assert.DoesNotContain(Terminator, terminator.Written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length));

            var recovered = await RunAsync(authority, command);
            Assert.Contains(prepared.ArtData, recovered);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Canceled write during ARTICLE leaves entry retained for a later successful delivery.
        /// </summary>

        [Fact]

        public async Task Cancellation_during_the_article_write_releases_the_claim_for_a_retry()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var blocked = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { BlockOnWriteNumber = 3 };
            using var cts = new CancellationTokenSource();
            var running = new NntpCacheListenerSession(blocked, authority).RunAsync(cts.Token);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(1, authority.RetainedCount);

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Concurrent claims: second session 403 during in-flight write; after commit next request is 430.
        /// </summary>

        [Fact]

        public async Task Two_sessions_cannot_deliver_the_same_entry_and_commit_makes_the_next_request_430()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var first = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { BlockOnWriteNumber = 3 };
            var second = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\nQUIT\r\n"));
            var running = new NntpCacheListenerSession(first, authority).RunAsync(CancellationToken.None);
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await new NntpCacheListenerSession(second, authority).RunAsync(CancellationToken.None);
            Assert.Contains("403 Article not available\r\n"u8, second.Written);
            Assert.Contains("205 Connection closing\r\n"u8, second.Written);
            Assert.Equal(1, authority.RetainedCount);

            first.Release.TrySetResult();
            await running;
            Assert.Equal(0, authority.RetainedCount);

            var third = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, third);
        }

        /// <summary>
        /// Successful delivery consumes entry even if client disconnects afterward.
        /// </summary>

        [Fact]

        public async Task A_completed_write_stays_consumed_when_the_client_then_disconnects()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);

            var again = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, again);
        }

        /// <summary>
        /// Failed write rollback allows retry until TTL expiry then 430.
        /// </summary>

        [Fact]

        public async Task A_retry_after_rollback_fails_once_ttl_has_expired()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000, TimeSpan.FromSeconds(60));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var failed = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { FailOnWriteNumber = 2 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(failed, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);

            time.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(prepared.ArtData.Length, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedCount);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// A command split before its LF, then a later command, still produces the exact status lines after the pending buffer is reused.
        /// </summary>
        [Fact]
        public async Task A_command_split_before_lf_is_followed_by_the_next_command()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-03-04T05:06:07Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var articleWireLength = Greeting.Length + Status("<a@b>").Length + prepared.ArtData.Length + Terminator.Length;
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nDATE\r\nQUIT\r\n", maxRead: 14, time: time);

            AssertArticleWire(written.AsSpan(0, articleWireLength).ToArray(), "<a@b>", prepared.ArtData);
            Assert.Equal(
                "111 20260304050607\r\n205 Connection closing\r\n"u8.ToArray(),
                written.AsSpan(articleWireLength).ToArray());
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>Successive DATE commands rewrite the session response and keep each timestamp.</summary>
        [Fact]
        public async Task Successive_date_commands_keep_each_timestamp()
        {
            var time = new SteppingTimeProvider(
                DateTimeOffset.Parse("2026-03-04T05:06:07Z"),
                DateTimeOffset.Parse("2026-12-31T23:59:58Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z")), 1_000_000);
            var written = await RunAsync(authority, "DATE\r\nDATE\r\nQUIT\r\n", time: time);

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("111 20260304050607\r\n"u8.ToArray())
                    .Concat("111 20261231235958\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        /// <summary>
        /// DATE returns RFC 3977 111 yyyymmddhhmmss from the retention clock and does not claim an article.
        /// </summary>
        [Fact]
        public async Task Date_returns_utc_timestamp_from_the_retention_clock()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-03-04T05:06:07Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");

            var written = await RunAsync(authority, "DATE\r\nQUIT\r\n", time: time);

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("111 20260304050607\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>DATE matching ignores ASCII case. Trailing tabs are not an argument.</summary>
        [Theory]
        [InlineData("date\r\n")]
        [InlineData("Date\r\n")]
        [InlineData("DATE\t\r\n")]
        public async Task Date_keyword_is_case_insensitive(string command)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);

            var written = await RunAsync(authority, command + "QUIT\r\n", time: time);

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("111 20260101000000\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        /// <summary>DATE with an argument is 501 and leaves the session open for a later ARTICLE.</summary>
        [Fact]
        public async Task Date_with_an_argument_is_a_syntax_error_and_does_not_block_article()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");

            var written = await RunAsync(authority, "DATE extra\r\nARTICLE <a@b>\r\n", time: time);

            Assert.Contains("501 Syntax error\r\n"u8, written);
            Assert.Contains("220 0 <a@b> article follows\r\n"u8, written);
            Assert.Contains(prepared.ArtData, written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// Commands outside ARTICLE, DATE, and QUIT are 500 and do not claim or remove a retained article.
        /// </summary>
        [Theory]
        [InlineData("CAPABILITIES")]
        [InlineData("capabilities")]
        [InlineData("MODE READER")]
        [InlineData("GROUP misc.test")]
        [InlineData("LIST")]
        [InlineData("STAT <a@b>")]
        [InlineData("HEAD <a@b>")]
        [InlineData("BODY <a@b>")]
        [InlineData("POST")]
        [InlineData("IHAVE <a@b>")]
        [InlineData("HELP")]
        [InlineData("AUTHINFO USER name")]
        [InlineData("STARTTLS")]
        [InlineData("COMPRESS DEFLATE")]
        [InlineData("NEWNEWS misc.test")]
        public async Task Unsupported_commands_are_rejected_without_article_side_effects(string command)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");

            var written = await RunAsync(authority, command + "\r\nQUIT\r\n", time: time);

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(prepared.ArtData.Length, authority.RetainedPayloadBytes);
        }

        /// <summary>Empty and whitespace-only lines are unknown commands and do not end the session.</summary>
        [Theory]
        [InlineData("\r\n")]
        [InlineData(" \r\n")]
        [InlineData("\t\r\n")]
        public async Task Empty_or_whitespace_command_lines_are_unknown_and_do_not_crash(string command)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);

            var written = await RunAsync(authority, command + "QUIT\r\n", time: time);

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        /// <summary>
        /// A payload larger than 32 KiB is one transport write, with one status line and one terminator.
        /// </summary>
        [Fact]
        public async Task Article_larger_than_32_kib_is_one_payload_write()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 8_000_000);
            var body = string.Concat(Enumerable.Repeat("x\r\n", 10_923));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<wide@b>", body);
            Assert.True(prepared.ArtData.Length > 32 * 1024);

            var transport = new ScriptedTransport("ARTICLE <wide@b>\r\n"u8.ToArray());
            await new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None);

            Assert.Equal(4, transport.WriteCallCount);
            Assert.Equal(prepared.ArtData.Length, transport.WriteLengths[2]);
            AssertArticleWire(transport.Written, "<wide@b>", prepared.ArtData);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>
        /// A typical article-sized body is written once, between the 220 line and one terminator.
        /// </summary>
        [Fact]
        public async Task Typical_article_is_one_payload_write_and_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 8_000_000);
            var body = string.Concat(Enumerable.Repeat("0123456789abcdef\r\n", 43_000));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<typical@b>", body);
            Assert.InRange(prepared.ArtData.Length, 740 * 1024, 800 * 1024);

            var transport = new ScriptedTransport("ARTICLE <typical@b>\r\n"u8.ToArray());
            await new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None);

            Assert.Equal(4, transport.WriteCallCount);
            Assert.Equal(prepared.ArtData.Length, transport.WriteLengths[2]);
            AssertArticleWire(transport.Written, "<typical@b>", prepared.ArtData);
            Assert.Equal(1, Count(transport.Written, Terminator));
        }

        /// <summary>
        /// Partial accepts resume at the next byte. The assembled wire image matches one full write.
        /// </summary>
        [Fact]
        public async Task Partial_accepts_resume_without_duplicating_or_dropping_bytes()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 8_000_000);
            var body = string.Concat(Enumerable.Repeat("partial-line\r\n", 400));
            RetentionTestArticles.RetainPrepared(authority, "<part@b>", body);
            var command = "ARTICLE <part@b>\r\n"u8.ToArray();

            var whole = new ScriptedTransport(command);
            await new NntpCacheListenerSession(whole, authority, time).RunAsync(CancellationToken.None);
            Assert.Equal(0, authority.RetainedCount);

            RetentionTestArticles.RetainPrepared(authority, "<part@b>", body);
            var partial = new ScriptedTransport(command) { MaxAcceptPerWrite = 1000 };
            await new NntpCacheListenerSession(partial, authority, time).RunAsync(CancellationToken.None);

            Assert.True(partial.WriteCallCount > whole.WriteCallCount);
            Assert.Equal(whole.Written, partial.Written);
            Assert.Equal(1, Count(partial.Written, Terminator));
            Assert.Equal(partial.Written.Length, partial.WriteLengths.Sum());
            Assert.All(partial.WriteLengths, static length => Assert.InRange(length, 1, 1000));
        }

        /// <summary>
        /// Two sequential ARTICLE commands each end with one terminator and do not share a write.
        /// </summary>
        [Fact]
        public async Task Two_articles_each_have_one_status_line_and_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 8_000_000);
            var first = RetentionTestArticles.RetainPrepared(authority, "<one@b>", "one\r\n");
            var second = RetentionTestArticles.RetainPrepared(authority, "<two@b>", "two\r\n");
            var transport = new ScriptedTransport("ARTICLE <one@b>\r\nARTICLE <two@b>\r\n"u8.ToArray());
            await new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None);

            Assert.Equal(7, transport.WriteCallCount);
            Assert.Equal(2, Count(transport.Written, Terminator));
            Assert.Contains(first.ArtData, transport.Written);
            Assert.Contains(second.ArtData, transport.Written);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>A transport that accepts zero bytes fails the write and does not commit the article.</summary>
        [Fact]
        public async Task Zero_accepted_bytes_fail_the_write_without_committing()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var transport = new ScriptedTransport("ARTICLE <a@b>\r\n"u8.ToArray()) { MaxAcceptPerWrite = 0 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>A count past the remaining buffer fails the article write and does not commit.</summary>
        [Fact]
        public async Task Accepted_count_past_the_remaining_buffer_fails_without_committing()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>", "body\r\n");
            var follows = Status("<a@b>");
            var transport = new ScriptedTransport("ARTICLE <a@b>\r\n"u8.ToArray()) { OverAcceptOnWriteNumber = 3 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(Greeting.Length + follows.Length, transport.Written.Length);
        }

        /// <summary>
        /// A zero count after a positive partial accept fails that write. The session does not retry the same bytes.
        /// </summary>
        [Fact]
        public async Task Zero_accept_after_progress_fails_without_committing_or_retrying()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>", "abcdefghijklmnopqrstuvwxyz\r\n");
            var follows = Status("<a@b>");
            const int chunk = 5;
            var writesBeforeBody = DivideRoundingUp(Greeting.Length, chunk) + DivideRoundingUp(follows.Length, chunk);
            var transport = new ScriptedTransport("ARTICLE <a@b>\r\n"u8.ToArray())
            {
                MaxAcceptPerWrite = chunk,
                ZeroAcceptOnWriteNumber = writesBeforeBody + 2,
            };

            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None));

            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, transport.WriteLengths[^1]);
            Assert.Equal(transport.Written.Length, transport.WriteLengths.Take(transport.WriteLengths.Count - 1).Sum());
            Assert.True(transport.Written.Length > Greeting.Length + follows.Length);
            Assert.False(transport.Written.AsSpan(transport.Written.Length - Terminator.Length).SequenceEqual(Terminator));
        }

        /// <summary>
        /// Bytes accepted before a stalled partial write stay a prefix. Cancellation does not commit, and a retry matches a full write.
        /// </summary>
        [Fact]
        public async Task Partial_progress_then_a_stall_keeps_the_prefix_and_a_retry_matches()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "abcdefghijklmnopqrstuvwxyz\r\n");
            var follows = Status("<a@b>");
            const int chunk = 5;
            var writesBeforeBody = DivideRoundingUp(Greeting.Length, chunk) + DivideRoundingUp(follows.Length, chunk);
            var blocked = new ScriptedTransport("ARTICLE <a@b>\r\n"u8.ToArray())
            {
                MaxAcceptPerWrite = chunk,
                BlockOnWriteNumber = writesBeforeBody + 2,
            };
            using var cts = new CancellationTokenSource();
            var running = new NntpCacheListenerSession(blocked, authority, time).RunAsync(cts.Token);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(blocked.Written.Length > Greeting.Length + follows.Length);
            Assert.True(blocked.Written.Length < Greeting.Length + follows.Length + prepared.ArtData.Length);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(1, authority.RetainedCount);

            var delivered = await RunAsync(authority, "ARTICLE <a@b>\r\n", time: time);
            Assert.True(delivered.AsSpan(0, blocked.Written.Length).SequenceEqual(blocked.Written));
            Assert.True(delivered.Length > blocked.Written.Length);
            AssertArticleWire(delivered, "<a@b>", prepared.ArtData);
            Assert.Equal(0, authority.RetainedCount);
        }

        /// <summary>Checks greeting, one 220 line, the retained image, and one terminator.</summary>
        /// <param name="written">Bytes accepted by the transport.</param>
        /// <param name="messageId">Message-ID expected in the status line.</param>
        /// <param name="artData">Retained article image expected after the status line.</param>
        private static void AssertArticleWire(byte[] written, string messageId, ReadOnlyMemory<byte> artData)
        {
            var follows = Status(messageId);
            Assert.True(written.AsSpan(0, Greeting.Length).SequenceEqual(Greeting));
            Assert.True(written.AsSpan(Greeting.Length, follows.Length).SequenceEqual(follows));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length, artData.Length).SequenceEqual(artData.Span));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + artData.Length).SequenceEqual(Terminator));
        }

        /// <summary>Runs the cache listener session against a scripted transport and returns wire bytes sent to the client.</summary>
        /// <param name="authority">Retention store backing article lookup and delivery claims.</param>
        /// <param name="commands">Inbound client command script (ASCII, CRLF-terminated).</param>
        /// <param name="maxRead">Maximum bytes copied per transport read to simulate partial socket reads.</param>
        /// <param name="time">Clock for DATE. Null uses <see cref="TimeProvider.System"/>.</param>
        private static async Task<byte[]> RunAsync(
            ArticleRetentionAuthority authority,
            string commands,
            int maxRead = int.MaxValue,
            TimeProvider? time = null)
        {
            var transport = new ScriptedTransport(Encoding.ASCII.GetBytes(commands), maxRead);
            await new NntpCacheListenerSession(transport, authority, time).RunAsync(CancellationToken.None);
            return transport.Written;
        }

        /// <summary>Counts non-overlapping occurrences of <paramref name="value"/> in <paramref name="source"/>.</summary>
        /// <param name="source">Buffer to scan.</param>
        /// <param name="value">Pattern to count.</param>
        private static int Count(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
        {
            var count = 0;
            for (var index = 0; index <= source.Length - value.Length; index++)
            {
                if (source.Slice(index, value.Length).SequenceEqual(value))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Rebuilds logical article bytes by destuffing the multiline body after the header/body separator.</summary>
        /// <param name="artData">Retained on-wire article including headers and dot-stuffed body.</param>
        private static byte[] LogicalFromRetained(byte[] artData)
        {
            var separator = artData.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            var bodyStart = separator + 4;
            var body = DestuffMultiline(artData.AsSpan(bodyStart));
            var logical = new byte[bodyStart + body.Length];
            artData.AsSpan(0, bodyStart).CopyTo(logical);
            body.CopyTo(logical.AsSpan(bodyStart));
            return logical;
        }

        /// <summary>Removes NNTP dot-stuffing from a CRLF-delimited multiline block line by line.</summary>
        /// <param name="block">Multiline body bytes as transmitted on the wire.</param>
        private static byte[] DestuffMultiline(ReadOnlySpan<byte> block)
        {
            var buffer = new byte[block.Length];
            var written = 0;
            var index = 0;
            while (index < block.Length)
            {
                var crlf = block[index..].IndexOf("\r\n"u8);
                Assert.True(crlf >= 0);
                var lineLength = crlf + 2;
                var line = block.Slice(index, lineLength);
                if (line[0] == (byte)'.' && !(line.Length == 3 && line[1] == (byte)'\r'))
                {
                    line[1..].CopyTo(buffer.AsSpan(written));
                    written += line.Length - 1;
                }
                else
                {
                    line.CopyTo(buffer.AsSpan(written));
                    written += line.Length;
                }

                index += lineLength;
            }

            return buffer.AsSpan(0, written).ToArray();
        }

        /// <summary>Reads a multiline article payload through <see cref="NntpStreamReader"/> with optional single-byte reads.</summary>
        /// <param name="multiline">Complete multiline payload including terminators.</param>
        /// <param name="chunk">When not <see cref="int.MaxValue"/>, wraps the stream to return one byte per read.</param>
        private static async Task<byte[]> ReadWithProductionReaderAsync(byte[] multiline, int chunk)
        {
            Stream stream = chunk == int.MaxValue
                ? new MemoryStream(multiline, writable: false)
                : new OneByteStream(multiline);
            var reader = new NntpStreamReader(stream, 1024);
            return await reader.ReadArticlePayloadAsync(1024 * 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        /// <summary>
        /// Stream that reads one byte at a time to stress incremental payload reading.
        /// </summary>

        private sealed class OneByteStream(byte[] data) : Stream
        {
            /// <summary>Next byte index in <c>data</c>; single-byte reads advance it until EOF.</summary>
            private int _position;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => data.Length;

            /// <inheritdoc />
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            /// <summary>Returns one byte per call when the destination span is non-empty.</summary>
            /// <param name="buffer">Destination span.</param>
            /// <returns>1 while data remains, otherwise 0.</returns>
            public override int Read(Span<byte> buffer)
            {
                if (_position >= data.Length || buffer.IsEmpty)
                {
                    return 0;
                }

                buffer[0] = data[_position++];
                return 1;
            }

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Builds the 220 article-follows status line for the given Message-ID.</summary>
        /// <param name="messageId">Message-ID appearing in the status line.</param>
        private static byte[] Status(string messageId) =>
            Encoding.ASCII.GetBytes("220 0 " + messageId + " article follows\r\n");

        /// <summary>Mutates a byte inside retained <see cref="ArticleRecord.ArtData"/> via the exposed array segment.</summary>
        /// <param name="record">Retained article whose backing array is mutated.</param>
        /// <param name="index">Zero-based offset within the retained article byte buffer.</param>
        /// <param name="value">Replacement byte value.</param>
        private static void WriteRetainedByte(ArticleRecord record, int index, byte value)
        {
            Assert.True(MemoryMarshal.TryGetArray(record.ArtData, out ArraySegment<byte> segment));
            segment.Array![segment.Offset + index] = value;
        }

        /// <summary>Divides <paramref name="length"/> by <paramref name="chunk"/>, rounding a remainder up.</summary>
        /// <param name="length">Byte length of one logical write.</param>
        /// <param name="chunk">Maximum bytes accepted from one transport write.</param>
        /// <returns>The number of transport writes required to accept <paramref name="length"/> bytes.</returns>
        private static int DivideRoundingUp(int length, int chunk) => (length + chunk - 1) / chunk;

        /// <summary>Returns the first index of <paramref name="pattern"/> in <paramref name="buffer"/>, or -1 if absent.</summary>
        /// <param name="buffer">Haystack buffer.</param>
        /// <param name="pattern">Subsequence to locate.</param>
        private static int IndexOf(byte[] buffer, byte[] pattern)
        {
            for (var i = 0; i <= buffer.Length - pattern.Length; i++)
            {
                if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Returns a different UTC instant from each <see cref="GetUtcNow"/> call, in order.</summary>
        private sealed class SteppingTimeProvider : TimeProvider
        {
            /// <summary>UTC instants returned in order; the last instant repeats when exhausted.</summary>
            private readonly DateTimeOffset[] _instants;

            /// <summary>Number of <see cref="GetUtcNow"/> calls completed.</summary>
            private int _index;

            /// <summary>Stores the instants returned by successive clock reads.</summary>
            /// <param name="instants">At least one instant. Later reads repeat the last instant.</param>
            internal SteppingTimeProvider(params DateTimeOffset[] instants)
            {
                _instants = instants;
            }

            /// <inheritdoc />
            public override DateTimeOffset GetUtcNow()
            {
                var instant = _instants[Math.Min(_index, _instants.Length - 1)];
                _index++;
                return instant;
            }
        }

        /// <summary>
        /// ICacheListenerTransport double with scripted read chunking and failing/blocking writes.
        /// </summary>

        private sealed class ScriptedTransport : ICacheListenerTransport
        {
            /// <summary>Bytes returned sequentially from <see cref="ReadAsync"/> until exhausted.</summary>
            private readonly byte[] _inbound;

            /// <summary>Upper bound on bytes copied per read to simulate partial socket reads.</summary>
            private readonly int _maxRead;

            /// <summary>Accumulates outbound wire bytes accepted by successful writes.</summary>
            private readonly List<byte> _written = [];

            /// <summary>Cursor into <see cref="_inbound"/> for the next read.</summary>
            private int _offset;

            /// <summary>Total payload bytes accepted across successful writes before scripted failure limits.</summary>
            private int _accepted;

            /// <summary>Number of <see cref="WriteAsync"/> calls; drives block and fail thresholds.</summary>
            private int _writeCalls;

            /// <summary>Accepted length of each successful or partial write, in call order.</summary>
            private readonly List<int> _writeLengths = [];

            /// <summary>Configures inbound script and optional read chunking for session protocol tests.</summary>
            /// <param name="inbound">Wire bytes the client would receive from the listener transport.</param>
            /// <param name="maxRead">Maximum bytes per read; defaults to the full inbound buffer.</param>
            internal ScriptedTransport(byte[] inbound, int maxRead = int.MaxValue)
            {
                _inbound = inbound;
                _maxRead = maxRead;
            }

            /// <summary>Snapshot of all bytes written to the simulated client.</summary>
            internal byte[] Written => _written.ToArray();

            /// <summary>How many times <see cref="WriteAsync"/> was entered.</summary>
            internal int WriteCallCount => _writeCalls;

            /// <summary>Accepted byte count of each write, including a partial accept before a later failure.</summary>
            internal IReadOnlyList<int> WriteLengths => _writeLengths;

            /// <summary>When the write call count equals this value, the next write throws <see cref="IOException"/>.</summary>
            internal int FailOnWriteNumber { get; init; }

            /// <summary>After this many payload bytes are accepted cumulatively, further writes fail or truncate.</summary>
            internal int FailAfterAcceptedBytes { get; init; } = int.MaxValue;

            /// <summary>Maximum bytes accepted from one write. A smaller value makes the session continue at the next byte.</summary>
            internal int MaxAcceptPerWrite { get; init; } = int.MaxValue;

            /// <summary>On this write invocation, accept nothing and return zero. Zero disables the behavior.</summary>
            internal int ZeroAcceptOnWriteNumber { get; init; }

            /// <summary>On this write invocation, report one more byte than the buffer contains. Zero disables the behavior.</summary>
            internal int OverAcceptOnWriteNumber { get; init; }

            /// <summary>On this write invocation, signals <see cref="Entered"/> and awaits <see cref="Release"/> before continuing.</summary>
            internal int BlockOnWriteNumber { get; init; }

            /// <summary>Completed when a write blocks at <see cref="BlockOnWriteNumber"/>.</summary>
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Unblocks a write waiting at <see cref="BlockOnWriteNumber"/>.</summary>
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Returns the next chunk of the inbound script, bounded by <see cref="_maxRead"/>; zero at EOF.</summary>
            /// <param name="buffer">Destination for copied inbound bytes.</param>
            /// <param name="cancellationToken">Unused; reads are synchronous.</param>
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_offset >= _inbound.Length || buffer.IsEmpty)
                {
                    return ValueTask.FromResult(0);
                }

                var count = Math.Min(buffer.Length, Math.Min(_maxRead, _inbound.Length - _offset));
                _inbound.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            /// <summary>Records accepted outbound bytes; may block, fail, or truncate per scripted thresholds.</summary>
            /// <param name="buffer">Outbound payload from the listener session.</param>
            /// <param name="cancellationToken">Honored while blocked on <see cref="Release"/>.</param>
            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                _writeCalls++;
                if (_writeCalls == BlockOnWriteNumber)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (FailOnWriteNumber == _writeCalls || _accepted >= FailAfterAcceptedBytes)
                {
                    throw new IOException("scripted write failure");
                }

                if (_writeCalls == ZeroAcceptOnWriteNumber)
                {
                    _writeLengths.Add(0);
                    return 0;
                }

                if (_writeCalls == OverAcceptOnWriteNumber)
                {
                    return buffer.Length + 1;
                }

                var count = Math.Min(buffer.Length, MaxAcceptPerWrite);
                count = Math.Min(count, FailAfterAcceptedBytes - _accepted);
                for (var i = 0; i < count; i++)
                {
                    _written.Add(buffer.Span[i]);
                }

                _accepted += count;
                _writeLengths.Add(count);
                return count;
            }

            /// <summary>No-op transport teardown.</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
