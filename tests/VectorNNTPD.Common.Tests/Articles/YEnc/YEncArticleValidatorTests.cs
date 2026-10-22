using System.Text;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles.YEnc
{
    /// <summary>
    /// Verifies yEnc validation outcomes for corpus fixtures and targeted corruption scenarios.
    /// </summary>
    /// <remarks>
    /// <para>These tests exercise byte-oriented validation behavior only; they do not integrate with downloader/runtime orchestration.</para>
    /// <para>Coverage includes single-part and multipart parsing, CRC and size integrity checks, malformed metadata handling,
    /// NNTP dot-stuffing interaction, and control-line false-positive avoidance.</para>
    /// </remarks>
    public sealed class YEncArticleValidatorTests
    {
        /// <summary>
        /// Absolute directory path containing offline SABCTools yEnc fixtures for theory-driven tests.
        /// </summary>
        private static readonly string FixtureRoot = ResolveFixtureRoot();

        /// <summary>
        /// Fixture names whose payload bytes are preserved in NNTP wire form and therefore require one transport dot-unstuffing pass before direct validator use.
        /// </summary>
        private static readonly HashSet<string> WireFormDotStuffedFixtureNames = new(StringComparer.Ordinal)
        {
            "test_regular.yenc",
            "test_regular_2.yenc",
            "test_special_chars.yenc",
            "test_bad_crc_end.yenc",
        };

        /// <summary>
        /// Provides fixture cases expected to validate successfully.
        /// </summary>
        /// <returns>Fixture name and expected validation status name pairs.</returns>
        public static IEnumerable<object[]> ValidFixtureCases()
        {
            yield return ["test_regular.yenc", nameof(YEncArticleValidationStatus.ValidMultiPart)];
            yield return ["test_regular_2.yenc", nameof(YEncArticleValidationStatus.ValidMultiPart)];
            yield return ["test_special_chars.yenc", nameof(YEncArticleValidationStatus.ValidMultiPart)];
            yield return ["test_special_utf8_chars.yenc", nameof(YEncArticleValidationStatus.ValidMultiPart)];
            yield return ["test_partial.yenc", nameof(YEncArticleValidationStatus.DecodedSizeMismatch)];
            yield return ["test_bad_crc.yenc", nameof(YEncArticleValidationStatus.ValidMultiPart)];
        }

        /// <summary>
        /// Provides fixture cases expected to fail validation or classify as non-yEnc.
        /// </summary>
        /// <returns>Fixture name and expected validation status name pairs.</returns>
        public static IEnumerable<object[]> InvalidFixtureCases()
        {
            yield return ["test_bad_crc_end.yenc", nameof(YEncArticleValidationStatus.CrcMismatch)];
            yield return ["test_invalid_crc_chars.yenc", nameof(YEncArticleValidationStatus.InvalidMetadata)];
            yield return ["test_invalid_escape.yenc", nameof(YEncArticleValidationStatus.InvalidEscapeSequence)];
            yield return ["test_missing_yend.yenc", nameof(YEncArticleValidationStatus.Truncated)];
            yield return ["test_malformed_ybegin.yenc", nameof(YEncArticleValidationStatus.InvalidMetadata)];
            yield return ["test_ypart_without_ybegin.yenc", nameof(YEncArticleValidationStatus.ValidNonYEnc)];
            yield return ["test_empty_file.yenc", nameof(YEncArticleValidationStatus.ValidNonYEnc)];
        }

        /// <summary>
        /// Confirms that known-good SABCTools fixtures classify to the expected success or expected failure contract.
        /// </summary>
        /// <param name="fixtureName">Fixture file name to validate.</param>
        /// <param name="expectedStatusName">Expected terminal validation status name.</param>
        [Theory]
        [MemberData(nameof(ValidFixtureCases))]
        public void Validate_WhenFixtureIsValid_ReturnsExpectedSuccessStatus(string fixtureName, string expectedStatusName)
        {
            byte[] fixtureBytes = LoadFixtureBytes(fixtureName);
            byte[] body = NormalizeFixtureBodyForValidatorContract(fixtureName, fixtureBytes);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(body);
            YEncArticleValidationStatus expected = Enum.Parse<YEncArticleValidationStatus>(expectedStatusName);

            Assert.Equal(expected, result.Status);

            if (expected is YEncArticleValidationStatus.ValidSinglePart or YEncArticleValidationStatus.ValidMultiPart)
            {
                Assert.True(result.IsValid);
                Assert.True(result.SectionsValidated > 0);
                Assert.False(result.ShouldTreatAsYEncDecodingFailed);
            }
            else
            {
                Assert.False(result.IsValid);
                Assert.True(result.ShouldTreatAsYEncDecodingFailed);
            }
        }

        /// <summary>
        /// Confirms that malformed fixtures and non-yEnc fixtures map to the expected validation classification.
        /// </summary>
        /// <param name="fixtureName">Fixture file name to validate.</param>
        /// <param name="expectedStatusName">Expected terminal validation status name.</param>
        [Theory]
        [MemberData(nameof(InvalidFixtureCases))]
        public void Validate_WhenFixtureIsInvalidOrNonYEnc_ReturnsExpectedStatus(string fixtureName, string expectedStatusName)
        {
            byte[] fixtureBytes = LoadFixtureBytes(fixtureName);
            byte[] body = NormalizeFixtureBodyForValidatorContract(fixtureName, fixtureBytes);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(body);
            YEncArticleValidationStatus expected = Enum.Parse<YEncArticleValidationStatus>(expectedStatusName);

            Assert.Equal(expected, result.Status);

            if (expected is YEncArticleValidationStatus.ValidNonYEnc)
            {
                Assert.True(result.IsValid);
                Assert.False(result.ShouldTreatAsYEncDecodingFailed);
            }
            else
            {
                Assert.False(result.IsValid);
                Assert.True(result.ShouldTreatAsYEncDecodingFailed);
            }
        }

        /// <summary>
        /// Verifies that a synthetically generated single-part article validates successfully and reports one validated section.
        /// </summary>
        [Fact]
        public void Validate_WhenSinglePartArticleIsValid_ReturnsValidSinglePart()
        {
            byte[] payload = BuildPayload(4096, 17);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
            Assert.True(result.IsValid);
            Assert.Equal(1, result.SectionsValidated);
        }

        /// <summary>
        /// Verifies that mutating one encoded payload byte is detected by decoded CRC mismatch classification.
        /// </summary>
        [Fact]
        public void Validate_WhenEncodedByteIsChanged_ReturnsCrcMismatch()
        {
            byte[] payload = BuildPayload(2048, 1);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int payloadOffset = FindPayloadOffset(article);
            article[payloadOffset + 25] ^= 0x01;

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that mutating one escaped yEnc byte changes decoded output and is reported as CRC mismatch.
        /// </summary>
        [Fact]
        public void Validate_WhenEscapedByteIsChanged_ReturnsCrcMismatch()
        {
            byte[] payload = new byte[2048];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 256);
            }

            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);
            int payloadOffset = FindPayloadOffset(article);

            for (int i = payloadOffset; i < article.Length - 1; i++)
            {
                if (article[i] == (byte)'=' && article[i + 1] != (byte)'y')
                {
                    article[i + 1] ^= 0x01;
                    break;
                }
            }

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that tampering trailer CRC metadata causes mismatch against computed decoded CRC.
        /// </summary>
        [Fact]
        public void Validate_WhenCrcMetadataIsAltered_ReturnsCrcMismatch()
        {
            byte[] payload = BuildPayload(2048, 4);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            byte[] mutated = ReplaceAsciiInCopy(article, " crc32=", " crc32=ffffffff");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(mutated);

            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that tampering trailer size metadata is detected as decoded-size mismatch.
        /// </summary>
        [Fact]
        public void Validate_WhenDeclaredSizeIsAltered_ReturnsDecodedSizeMismatch()
        {
            byte[] payload = BuildPayload(1536, 7);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            byte[] mutated = ReplaceExactTokenInCopy(article, "\r\n=yend size=1536 ", "\r\n=yend size=1535 ");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(mutated);

            Assert.Equal(YEncArticleValidationStatus.DecodedSizeMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that removing trailing bytes from the article body yields truncated classification.
        /// </summary>
        [Fact]
        public void Validate_WhenPayloadIsTruncated_ReturnsTruncated()
        {
            byte[] payload = BuildPayload(4096, 5);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            byte[] truncated = article[..(article.Length - 40)];

            YEncArticleValidationResult result = YEncArticleValidator.Validate(truncated);

            Assert.Equal(YEncArticleValidationStatus.Truncated, result.Status);
        }

        /// <summary>
        /// Verifies that a cut-off <c>=yend</c> control line is treated as truncated yEnc data.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndLineIsTruncated_ReturnsTruncated()
        {
            byte[] payload = BuildPayload(1024, 8);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int endOffset = IndexOfAscii(article, "=yend ");
            byte[] truncated = article[..(endOffset + 8)];

            YEncArticleValidationResult result = YEncArticleValidator.Validate(truncated);

            Assert.Equal(YEncArticleValidationStatus.Truncated, result.Status);
        }

        /// <summary>
        /// Verifies that a trailing escape marker without a valid escaped byte is rejected as an invalid escape sequence.
        /// </summary>
        [Fact]
        public void Validate_WhenEscapeSequenceIsMalformed_ReturnsInvalidEscapeSequence()
        {
            byte[] payload = BuildPayload(1024, 11);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int payloadStart = FindPayloadOffset(article);
            int lineEnd = Array.IndexOf(article, (byte)'\n', payloadStart);
            article[lineEnd - 1] = (byte)'=';

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidEscapeSequence, result.Status);
        }

        /// <summary>
        /// Verifies that missing required <c>=ybegin</c> metadata fields are classified as invalid metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginIsMalformed_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that malformed multipart range metadata in <c>=ypart</c> is classified as invalid metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYPartIsMalformed_ReturnsInvalidMetadata()
        {
            byte[] payload = BuildPayload(100, 3);
            byte[] article = BuildMultiPartArticle(payload, begin: 1, end: 100, malformedYPart: true);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that a trailer missing CRC metadata is rejected as invalid metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndMissingCrc_ReturnsInvalidMetadata()
        {
            byte[] payload = BuildPayload(512, 12);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int endStart = IndexOfAscii(article, "=yend ");
            int endLineEnd = Array.IndexOf(article, (byte)'\n', endStart);
            byte[] prefix = article[..endStart];
            byte[] replacement = "=yend size=512\r\n"u8.ToArray();
            byte[] suffix = article[(endLineEnd + 1)..];
            byte[] malformed = new byte[prefix.Length + replacement.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, malformed, 0, prefix.Length);
            Buffer.BlockCopy(replacement, 0, malformed, prefix.Length, replacement.Length);
            Buffer.BlockCopy(suffix, 0, malformed, prefix.Length + replacement.Length, suffix.Length);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(malformed);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies a logical encoded payload line beginning with <c>.</c> validates successfully when provided as transport-normalized bytes.
        /// </summary>
        [Fact]
        public void Validate_WhenLogicalPayloadHasLeadingDot_ValidatesSuccessfully()
        {
            byte[] decodedPayload = [4];
            uint crc = Crc32(decodedPayload);
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <message-id>\r\n\r\n=ybegin line=128 size=1 name=test.bin\r\n.\r\n=yend size=1 crc32={crc:x8}\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
        }

        /// <summary>
        /// Verifies that wire-level dot-stuffed input is rejected when passed directly to the validator.
        /// </summary>
        [Fact]
        public void Validate_WhenWireDotStuffedPayloadProvided_ReturnsDecodedSizeMismatch()
        {
            byte[] decodedPayload = [4];
            uint crc = Crc32(decodedPayload);
            byte[] wireStuffedArticle = System.Text.Encoding.ASCII.GetBytes($"220 0 <message-id>\r\n\r\n=ybegin line=128 size=1 name=test.bin\r\n..\r\n=yend size=1 crc32={crc:x8}\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(wireStuffedArticle);

            Assert.Equal(YEncArticleValidationStatus.DecodedSizeMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that <c>=yend</c>-like bytes embedded within payload data do not terminate section parsing.
        /// </summary>
        [Fact]
        public void Validate_WhenPayloadContainsYEndLikeBytesInData_DoesNotFalseTerminate()
        {
            byte[] payload = "prefix =yend size=9999 crc32=ffffffff suffix"u8.ToArray();
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
        }

        /// <summary>
        /// Verifies that a payload line starting with escaped bytes that textually resemble <c>=yend</c> does not terminate parsing.
        /// </summary>
        [Fact]
        public void Validate_WhenPayloadLineStartsWithEscapedYEndText_DoesNotFalseTerminate()
        {
            byte[] payload = [119, 59, 68, 67, 58, 246, 59, 72, 57, 9, 57, 56];
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
        }

        /// <summary>
        /// Verifies that repeated validation of a large valid payload does not allocate on the current thread after warmup.
        /// </summary>
        [Fact]
        public void Validate_WhenLargeSinglePartPayloadIsValid_DoesNotAllocateOnHotPath()
        {
            byte[] payload = BuildPayload(4 * 1024 * 1024, 41);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            YEncArticleValidationResult warmup = YEncArticleValidator.Validate(article);
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, warmup.Status);

            const int Iterations = 16;
            long before = GC.GetAllocatedBytesForCurrentThread();
            YEncArticleValidationStatus lastStatus = YEncArticleValidationStatus.ValidNonYEnc;

            for (int i = 0; i < Iterations; i++)
            {
                lastStatus = YEncArticleValidator.Validate(article).Status;
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long allocatedBytes = after - before;

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, lastStatus);
            Assert.Equal(0, allocatedBytes);
        }

        /// <summary>
        /// Verifies that single-byte corruption in a large payload is detected as CRC mismatch.
        /// </summary>
        [Fact]
        public void Validate_WhenLargeSinglePartPayloadCorrupted_ReturnsCrcMismatch()
        {
            byte[] payload = BuildPayload(4 * 1024 * 1024, 57);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int payloadOffset = FindPayloadOffset(article);
            article[payloadOffset + 1_000_000] ^= 0x20;

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that validation throughput on a large payload exceeds a minimal regression guard threshold.
        /// </summary>
        [Fact]
        public void Validate_WhenThroughputMeasuredForLargePayload_CompletesAtReasonableRate()
        {
            byte[] payload = BuildPayload(8 * 1024 * 1024, 21);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);
            sw.Stop();

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);

            double mb = article.Length / (1024d * 1024d);
            double mbPerSecond = mb / Math.Max(sw.Elapsed.TotalSeconds, 0.000001d);
            Assert.True(mbPerSecond > 1d, $"Expected throughput > 1 MB/s but got {mbPerSecond:F2} MB/s");
        }

        /// <summary>
        /// Verifies that an overflowing decimal <c>size=</c> value in <c>=ybegin</c> is rejected as invalid metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginSizeOverflowsLong_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=9223372036854775808 name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that trailer CRC values longer than eight hexadecimal digits are rejected as invalid metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndCrcHasMoreThanEightHexDigits_ReturnsInvalidMetadata()
        {
            byte[] payload = BuildPayload(512, 33);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);
            byte[] mutated = ReplaceAsciiInCopy(article, " crc32=", " crc32=123456789");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(mutated);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that an overflowing decimal <c>size=</c> value in <c>=yend</c> is rejected during strict decimal parsing.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndSizeOverflowsLong_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=9223372036854775808 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that decimal metadata fields reject numeric prefixes with trailing garbage in <c>=ybegin</c>.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginSizeContainsGarbageSuffix_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=123abc name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that decimal metadata fields reject numeric prefixes with trailing garbage in <c>=ypart begin=</c>.
        /// </summary>
        [Fact]
        public void Validate_WhenYPartBeginContainsGarbageSuffix_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin part=1 line=128 size=3 name=test.bin\r\n=ypart begin=1foo end=3\r\nabc\r\n=yend size=3 pcrc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that decimal metadata fields reject numeric prefixes with trailing garbage in <c>=ypart end=</c>.
        /// </summary>
        [Fact]
        public void Validate_WhenYPartEndContainsGarbageSuffix_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin part=1 line=128 size=3 name=test.bin\r\n=ypart begin=1 end=3bar\r\nabc\r\n=yend size=3 pcrc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that CRC metadata rejects valid hexadecimal prefixes followed by trailing garbage.
        /// </summary>
        /// <param name="crcField">Trailer text whose hex prefix is followed by non-hex characters.</param>
        [Theory]
        [InlineData(" crc32=ABCDEF12G")]
        [InlineData(" crc32=12345678XYZ")]
        public void Validate_WhenYEndCrcContainsGarbageSuffix_ReturnsInvalidMetadata(string crcField)
        {
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3{crcField}\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that duplicate <c>size=</c> tokens are rejected in <c>=yend</c> metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsDuplicateSizeFields_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3 size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that duplicate <c>crc32=</c> tokens are rejected in <c>=yend</c> metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsDuplicateCrcFields_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2 crc32=00000000\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that duplicate <c>pcrc32=</c> tokens are rejected in multipart <c>=yend</c> metadata.
        /// </summary>
        [Fact]
        public void Validate_WhenMultipartYEndContainsDuplicatePartCrcFields_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin part=1 line=128 size=3 name=test.bin\r\n=ypart begin=1 end=3\r\nabc\r\n=yend size=3 pcrc32=352441c2 pcrc32=00000000\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that single-part trailers reject <c>pcrc32=</c> when <c>crc32=</c> is missing.
        /// </summary>
        [Fact]
        public void Validate_WhenSinglePartYEndContainsOnlyPartCrc_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3 pcrc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that single-part trailers use <c>crc32=</c> for validation when <c>pcrc32=</c> is also present and fields conflict.
        /// </summary>
        /// <param name="fieldTemplate">Format string whose <c>{0}</c> is the computed CRC. Both field orders keep <c>crc32</c> equal to that CRC.</param>
        [Theory]
        [InlineData(" pcrc32=00000000 crc32={0:x8}")]
        [InlineData(" crc32={0:x8} pcrc32=00000000")]
        public void Validate_WhenSinglePartYEndContainsConflictingCrcAndPartCrc_UsesCrc32Field(string fieldTemplate)
        {
            byte[] payload = BuildPayload(512, 67);
            byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);
            uint crc = Crc32(payload);
            string replacement = string.Format(System.Globalization.CultureInfo.InvariantCulture, fieldTemplate, crc);
            byte[] mutated = ReplaceAsciiInCopy(article, " crc32=", replacement);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(mutated);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
        }

        /// <summary>
        /// Verifies that multipart trailers prefer <c>pcrc32=</c> over conflicting <c>crc32=</c> in either field order.
        /// </summary>
        /// <param name="fieldTemplate">Format string whose <c>{0}</c> is the part CRC. Both field orders keep <c>pcrc32</c> equal to that CRC.</param>
        [Theory]
        [InlineData(" pcrc32={0:x8} crc32=00000000")]
        [InlineData(" crc32=00000000 pcrc32={0:x8}")]
        public void Validate_WhenMultipartYEndContainsConflictingCrcAndPartCrc_UsesPartCrcField(string fieldTemplate)
        {
            byte[] payload = BuildPayload(512, 68);
            byte[] article = BuildMultiPartArticle(payload, begin: 1, end: payload.Length, malformedYPart: false);
            uint crc = Crc32(payload);
            string replacement = string.Format(System.Globalization.CultureInfo.InvariantCulture, fieldTemplate, crc);
            byte[] mutated = ReplaceAsciiInCopy(article, " pcrc32=", replacement);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(mutated);

            Assert.Equal(YEncArticleValidationStatus.ValidMultiPart, result.Status);
        }

        /// <summary>
        /// Verifies that unknown metadata keys in <c>=yend</c> are rejected.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsUnknownMetadataKey_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2 unknown=1\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that empty metadata values in <c>=yend</c> are rejected.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsEmptyMetadataValue_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size= crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that malformed <c>=ybegin</c> stem delimiter usage is rejected.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginStemDelimiterIsMissing_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybeginfoo\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that malformed <c>=ypart</c> and <c>=yend</c> delimiter usage in payload is not treated as control boundaries.
        /// </summary>
        /// <param name="malformedControlLine">Payload line that starts with <c>=ypart</c> or <c>=yend</c> without the required space.</param>
        [Theory]
        [InlineData("=ypartfoo")]
        [InlineData("=yendfoo")]
        public void Validate_WhenPayloadControlLineDelimiterIsMissing_DoesNotBecomeBoundary(string malformedControlLine)
        {
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\n{malformedControlLine}\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.DecodedSizeMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that yend-like payload lines without parseable metadata do not terminate decoding and lead to integrity mismatch.
        /// </summary>
        [Fact]
        public void Validate_WhenPayloadContainsYEndLikeNoiseLine_RejectsWithDecodedSizeMismatch()
        {
            byte[] payload = BuildPayload(64, 66);
            byte[] baseArticle = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int yendIndex = IndexOfAscii(baseArticle, "=yend ");
            byte[] prefix = baseArticle[..yendIndex];
            byte[] noise = "=yend not metadata\r\n"u8.ToArray();
            byte[] suffix = baseArticle[yendIndex..];
            byte[] article = new byte[prefix.Length + noise.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, article, 0, prefix.Length);
            Buffer.BlockCopy(noise, 0, article, prefix.Length, noise.Length);
            Buffer.BlockCopy(suffix, 0, article, prefix.Length + noise.Length, suffix.Length);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.DecodedSizeMismatch, result.Status);
        }

        /// <summary>
        /// Verifies multipart status semantics by validating independent overlapping sections without file reconstruction.
        /// </summary>
        [Fact]
        public void Validate_WhenMultipartSectionsOverlap_ReportsSectionValidationContract()
        {
            byte[] firstPayload = BuildPayload(64, 61);
            byte[] secondPayload = BuildPayload(64, 62);

            byte[] first = BuildMultiPartArticle(firstPayload, begin: 1, end: 64, malformedYPart: false);
            byte[] second = BuildMultiPartArticle(secondPayload, begin: 32, end: 95, malformedYPart: false);
            byte[] combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(combined);

            Assert.Equal(YEncArticleValidationStatus.ValidMultiPart, result.Status);
            Assert.Equal(2, result.SectionsValidated);
            Assert.True(result.IsValid);
        }

        /// <summary>
        /// Verifies that payload lines starting with control-like stems are treated as payload bytes and not rejected by stem heuristics.
        /// </summary>
        /// <param name="payloadLine">Payload line inserted before the real trailer.</param>
        [Theory]
        [InlineData("=ybegin")]
        [InlineData("=ypart")]
        [InlineData("=yend")]
        [InlineData("=ybeginfoo")]
        [InlineData("=ypartfoo")]
        [InlineData("=yendfoo")]
        public void Validate_WhenPayloadLineStartsWithControlStem_DoesNotFailWithInvalidMetadata(string payloadLine)
        {
            byte[] payload = BuildPayload(48, 71);
            byte[] baseArticle = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);

            int yendIndex = IndexOfAscii(baseArticle, "=yend ");
            byte[] prefix = baseArticle[..yendIndex];
            byte[] inserted = System.Text.Encoding.ASCII.GetBytes(payloadLine + "\r\n");
            byte[] suffix = baseArticle[yendIndex..];
            byte[] article = new byte[prefix.Length + inserted.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, article, 0, prefix.Length);
            Buffer.BlockCopy(inserted, 0, article, prefix.Length, inserted.Length);
            Buffer.BlockCopy(suffix, 0, article, prefix.Length + inserted.Length, suffix.Length);

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.NotEqual(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that malformed key/value pairs with tab separators are rejected when metadata key hints exist.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsTabSeparatorWithMetadataHint_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3\tcrc32=352441c2 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that repeated spaces in metadata are rejected.
        /// </summary>
        [Fact]
        public void Validate_WhenYEndContainsRepeatedSpaces_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n=yend size=3  crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that malformed key/value forms are rejected.
        /// </summary>
        /// <param name="yendLine">Complete <c>=yend</c> line with a missing key, missing value, or doubled equals.</param>
        [Theory]
        [InlineData("=yend size==3 crc32=352441c2")]
        [InlineData("=yend =3 crc32=352441c2")]
        [InlineData("=yend size=3 =352441c2")]
        [InlineData("=yend size=3 key==value crc32=352441c2")]
        public void Validate_WhenYEndContainsMalformedKeyValue_ReturnsInvalidMetadata(string yendLine)
        {
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <id>\r\n\r\n=ybegin line=128 size=3 name=test.bin\r\nabc\r\n{yendLine}\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies decimal parser boundaries for non-negative and malformed forms.
        /// </summary>
        /// <param name="sizeValue"><c>size</c> token that is signed, blank-padded, empty, or has a trailing non-digit.</param>
        [Theory]
        [InlineData("+3")]
        [InlineData("-3")]
        [InlineData("3x")]
        [InlineData(" 3")]
        [InlineData("")]
        public void Validate_WhenYBeginSizeHasMalformedDecimal_ReturnsInvalidMetadata(string sizeValue)
        {
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <id>\r\n\r\n=ybegin line=128 size={sizeValue} name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies that an Int64 maximum value parses but cannot match decoded size for tiny payloads.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginSizeIsInt64Max_ParsesAndFailsBySizeMismatch()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=9223372036854775807 name=test.bin\r\nabc\r\n=yend size=9223372036854775807 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.DecodedSizeMismatch, result.Status);
        }

        /// <summary>
        /// Verifies that a non-ASCII digit in size metadata is rejected.
        /// </summary>
        [Fact]
        public void Validate_WhenYBeginSizeContainsNonAsciiDigit_ReturnsInvalidMetadata()
        {
            byte[] article = "220 0 <id>\r\n\r\n=ybegin line=128 size=１２3 name=test.bin\r\nabc\r\n=yend size=3 crc32=352441c2\r\n.\r\n"u8.ToArray();

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.InvalidMetadata, result.Status);
        }

        /// <summary>
        /// Verifies full-byte-range yEnc escape/decode correctness for single-byte payloads.
        /// </summary>
        [Fact]
        public void Validate_WhenAllByteValuesAreRoundTripped_ValidatesEachValue()
        {
            for (int value = 0; value <= 255; value++)
            {
                byte[] payload = [(byte)value];
                byte[] article = BuildSinglePartArticle(payload, includeDotStuffedLeadingDotLine: false);
                YEncArticleValidationResult result = YEncArticleValidator.Validate(article);
                Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
            }
        }

        /// <summary>
        /// Verifies leading-dot line patterns validate using logical encoded bytes without transport dot normalization.
        /// </summary>
        /// <param name="payloadLine">Logical encoded payload line, including leading dots, validated without transport unstuffing.</param>
        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("...")]
        [InlineData("....")]
        [InlineData("..=...")]
        [InlineData("...=...")]
        [InlineData("..=y")]
        public void Validate_WhenLogicalLeadingDotPatternIsUsed_ValidatesLogicalDecodedContent(string payloadLine)
        {
            byte[] decoded = DecodeLogicalPayloadLine(payloadLine);
            uint crc = Crc32(decoded);
            byte[] article = System.Text.Encoding.ASCII.GetBytes($"220 0 <id>\r\n\r\n=ybegin line=128 size={decoded.Length} name=test.bin\r\n{payloadLine}\r\n=yend size={decoded.Length} crc32={crc:x8}\r\n.\r\n");

            YEncArticleValidationResult result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
        }

        /// <summary>
        /// Verifies section sequencing semantics across valid/corrupt/overlapping/repeated combinations.
        /// </summary>
        [Fact]
        public void Validate_WhenMultipleSectionsAreProvided_StopsOnFirstFailureAndCountsValidSections()
        {
            byte[] aPayload = BuildPayload(32, 81);
            byte[] bPayload = BuildPayload(32, 82);
            byte[] cPayload = BuildPayload(32, 83);

            byte[] a = BuildMultiPartArticle(aPayload, begin: 1, end: 32, malformedYPart: false);
            byte[] b = BuildMultiPartArticle(bPayload, begin: 33, end: 64, malformedYPart: false);
            byte[] c = BuildMultiPartArticle(cPayload, begin: 17, end: 48, malformedYPart: false);

            byte[] combinedValid = Concatenate(a, b, c);
            YEncArticleValidationResult validResult = YEncArticleValidator.Validate(combinedValid);
            Assert.Equal(YEncArticleValidationStatus.ValidMultiPart, validResult.Status);
            Assert.Equal(3, validResult.SectionsValidated);

            byte[] corrupt = (byte[])b.Clone();
            int payloadOffset = FindPayloadOffset(corrupt);
            corrupt[payloadOffset + 8] ^= 0x04;
            byte[] combinedCorruptSecond = Concatenate(a, corrupt, c);
            YEncArticleValidationResult corruptResult = YEncArticleValidator.Validate(combinedCorruptSecond);
            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, corruptResult.Status);
            Assert.Equal(1, corruptResult.SectionsValidated);
        }

        /// <summary>
        /// Verifies article-boundary behavior for empty and malformed framing variants.
        /// </summary>
        [Fact]
        public void Validate_WhenArticleBoundaryVariantsAreProvided_ReturnsExpectedStatuses()
        {
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, YEncArticleValidator.Validate([]).Status);

            byte[] beginOnly = "=ybegin line=128 size=1 name=test.bin\r\n"u8.ToArray();
            Assert.Equal(YEncArticleValidationStatus.Truncated, YEncArticleValidator.Validate(beginOnly).Status);

            byte[] beginPayloadNoEnd = "=ybegin line=128 size=1 name=test.bin\r\n.\r\n"u8.ToArray();
            Assert.Equal(YEncArticleValidationStatus.Truncated, YEncArticleValidator.Validate(beginPayloadNoEnd).Status);

            byte[] beginEndNoPayload = "=ybegin line=128 size=0 name=test.bin\r\n=yend size=0 crc32=00000000\r\n"u8.ToArray();
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, YEncArticleValidator.Validate(beginEndNoPayload).Status);

            byte[] lfDecoded = [4];
            uint lfCrc = Crc32(lfDecoded);
            byte[] lfOnly = System.Text.Encoding.ASCII.GetBytes($"=ybegin line=128 size=1 name=test.bin\n.\n=yend size=1 crc32={lfCrc:x8}\n");
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, YEncArticleValidator.Validate(lfOnly).Status);

            byte[] crOnly = "=ybegin line=128 size=1 name=test.bin\r.\r=yend size=1 crc32=1d3d839a\r"u8.ToArray();
            Assert.Equal(YEncArticleValidationStatus.Truncated, YEncArticleValidator.Validate(crOnly).Status);

            byte[] trailingDecoded = [4];
            uint trailingCrc = Crc32(trailingDecoded);
            byte[] trailingBytes = System.Text.Encoding.ASCII.GetBytes($"=ybegin line=128 size=1 name=test.bin\r\n.\r\n=yend size=1 crc32={trailingCrc:x8}\r\nGARBAGE\r\n");
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, YEncArticleValidator.Validate(trailingBytes).Status);
        }

        /// <summary>
        /// Loads one yEnc fixture as raw bytes for validation.
        /// </summary>
        /// <param name="fixtureName">Fixture file name under the SABCTools fixture directory.</param>
        /// <returns>Raw fixture bytes.</returns>
        private static byte[] LoadFixtureBytes(string fixtureName)
        {
            string path = Path.Join(FixtureRoot, fixtureName);
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// Normalizes fixture bytes to the validator input contract for fixture-driven tests that bypass acquisition.
        /// </summary>
        /// <param name="fixtureName">Fixture file name under the SABCTools fixture directory.</param>
        /// <param name="fixtureBytes">Raw fixture bytes.</param>
        /// <returns>Logical body bytes suitable for direct transport-agnostic validator input.</returns>
        private static byte[] NormalizeFixtureBodyForValidatorContract(string fixtureName, byte[] fixtureBytes)
        {
            if (!WireFormDotStuffedFixtureNames.Contains(fixtureName))
            {
                return fixtureBytes;
            }

            int payloadStart = FindPayloadStartOffset(fixtureBytes);
            int yEndLineStart = FindYEndLineStartOffset(fixtureBytes, payloadStart);
            if (payloadStart < 0 || yEndLineStart < 0 || yEndLineStart < payloadStart)
            {
                return fixtureBytes;
            }

            byte[] normalizedPayload = UnstuffNntpWireDotPrefixedLines(fixtureBytes.AsSpan(payloadStart, yEndLineStart - payloadStart));
            byte[] normalized = new byte[payloadStart + normalizedPayload.Length + (fixtureBytes.Length - yEndLineStart)];
            Buffer.BlockCopy(fixtureBytes, 0, normalized, 0, payloadStart);
            Buffer.BlockCopy(normalizedPayload, 0, normalized, payloadStart, normalizedPayload.Length);
            Buffer.BlockCopy(fixtureBytes, yEndLineStart, normalized, payloadStart + normalizedPayload.Length, fixtureBytes.Length - yEndLineStart);
            return normalized;
        }

        /// <summary>
        /// Finds payload start offset immediately after the multipart <c>=ypart</c> line when present, otherwise after <c>=ybegin</c>.
        /// </summary>
        /// <param name="fixtureBytes">Fixture bytes.</param>
        /// <returns>Zero-based payload start offset, or -1 when markers are missing.</returns>
        private static int FindPayloadStartOffset(byte[] fixtureBytes)
        {
            string text = Encoding.ASCII.GetString(fixtureBytes);
            int partIndex = text.IndexOf("=ypart ", StringComparison.Ordinal);
            int beginIndex = text.IndexOf("=ybegin ", StringComparison.Ordinal);
            int anchor = partIndex >= 0 ? partIndex : beginIndex;
            if (anchor < 0)
            {
                return -1;
            }

            int lineFeed = text.IndexOf('\n', anchor);
            return lineFeed < 0 ? -1 : lineFeed + 1;
        }

        /// <summary>
        /// Finds the byte offset of the <c>=yend</c> control line start that terminates payload bytes.
        /// </summary>
        /// <param name="fixtureBytes">Fixture bytes.</param>
        /// <param name="searchStart">Offset where payload begins.</param>
        /// <returns>Zero-based offset of the <c>=yend</c> line start, or -1 when not found.</returns>
        private static int FindYEndLineStartOffset(byte[] fixtureBytes, int searchStart)
        {
            string text = Encoding.ASCII.GetString(fixtureBytes);
            int crlfCandidate = text.IndexOf("\r\n=yend ", searchStart, StringComparison.Ordinal);
            if (crlfCandidate >= 0)
            {
                return crlfCandidate + 2;
            }

            int lfCandidate = text.IndexOf("\n=yend ", searchStart, StringComparison.Ordinal);
            return lfCandidate >= 0 ? lfCandidate + 1 : -1;
        }

        /// <summary>
        /// Removes exactly one NNTP transport dot from line-start <c>..</c> sequences while preserving all other payload bytes.
        /// </summary>
        /// <param name="payload">Payload bytes between data start and <c>=yend</c>.</param>
        /// <returns>Payload bytes after one line-start transport dot-unstuffing pass.</returns>
        private static byte[] UnstuffNntpWireDotPrefixedLines(ReadOnlySpan<byte> payload)
        {
            List<byte> output = new(payload.Length);
            bool atLineStart = true;

            for (int i = 0; i < payload.Length; i++)
            {
                byte current = payload[i];
                if (atLineStart && current == (byte)'.' && i + 1 < payload.Length && payload[i + 1] == (byte)'.')
                {
                    output.Add((byte)'.');
                    i++;
                    atLineStart = false;
                    continue;
                }

                output.Add(current);
                atLineStart = current is (byte)'\r' or (byte)'\n';
            }

            return [.. output];
        }

        /// <summary>
        /// Resolves the fixture directory by walking upward from the test base directory to <c>VectorNNTPD.slnx</c>.
        /// </summary>
        /// <returns>Absolute fixture directory path.</returns>
        /// <exception cref="DirectoryNotFoundException">Thrown when neither the output fixture directory nor the solution-relative directory exists.</exception>
        private static string ResolveFixtureRoot()
        {
            string outputCandidate = Path.Join(AppContext.BaseDirectory, "Fixtures", "SabctoolsYEnc");
            if (Directory.Exists(outputCandidate))
            {
                return outputCandidate;
            }

            string? current = AppContext.BaseDirectory;

            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Join(current, "VectorNNTPD.slnx")))
                {
                    string root = Path.Join(current, "tests", "VectorNNTPD.Common.Tests", "Fixtures", "SabctoolsYEnc");
                    if (Directory.Exists(root))
                    {
                        return root;
                    }

                    break;
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate yEnc fixture directory.");
        }

        /// <summary>
        /// Builds a synthetic single-part article envelope around a decoded payload.
        /// </summary>
        /// <param name="decodedPayload">Decoded bytes to encode and place between <c>=ybegin</c> and <c>=yend</c>.</param>
        /// <param name="includeDotStuffedLeadingDotLine">Whether to apply NNTP dot-stuffing to encoded line starts.</param>
        /// <returns>Raw article bytes with transport framing and yEnc metadata.</returns>
        private static byte[] BuildSinglePartArticle(byte[] decodedPayload, bool includeDotStuffedLeadingDotLine)
        {
            byte[] encodedPayload = EncodeYEnc(decodedPayload);

            if (includeDotStuffedLeadingDotLine)
            {
                encodedPayload = DotStuffLineStarts(encodedPayload);
            }

            uint crc = Crc32(decodedPayload);
            byte[] prefix = System.Text.Encoding.ASCII.GetBytes($"220 0 <message-id>\r\n\r\n=ybegin line=128 size={decodedPayload.Length} name=test.bin\r\n");
            byte[] suffix = System.Text.Encoding.ASCII.GetBytes($"\r\n=yend size={decodedPayload.Length} crc32={crc:x8}\r\n.\r\n");

            byte[] article = new byte[prefix.Length + encodedPayload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, article, 0, prefix.Length);
            Buffer.BlockCopy(encodedPayload, 0, article, prefix.Length, encodedPayload.Length);
            Buffer.BlockCopy(suffix, 0, article, prefix.Length + encodedPayload.Length, suffix.Length);
            return article;
        }

        /// <summary>
        /// Builds a synthetic multipart article containing <c>=ybegin</c>, <c>=ypart</c>, and <c>=yend</c> metadata.
        /// </summary>
        /// <param name="decodedPayload">Decoded bytes for this multipart section.</param>
        /// <param name="begin">Declared part begin offset (1-based).</param>
        /// <param name="end">Declared part end offset (inclusive).</param>
        /// <param name="malformedYPart">Whether to intentionally emit malformed <c>=ypart</c> metadata for negative tests.</param>
        /// <returns>Raw multipart article bytes.</returns>
        private static byte[] BuildMultiPartArticle(byte[] decodedPayload, int begin, int end, bool malformedYPart)
        {
            byte[] encodedPayload = EncodeYEnc(decodedPayload);
            uint crc = Crc32(decodedPayload);

            string yPartLine = malformedYPart
                ? "=ypart begin=x end=y"
                : $"=ypart begin={begin} end={end}";

            byte[] prefix = System.Text.Encoding.ASCII.GetBytes($"222 0 <message-id>\r\n\r\n=ybegin part=1 line=128 size={end} name=test.bin\r\n{yPartLine}\r\n");
            byte[] suffix = System.Text.Encoding.ASCII.GetBytes($"\r\n=yend size={decodedPayload.Length} part=1 pcrc32={crc:x8}\r\n.\r\n");

            byte[] article = new byte[prefix.Length + encodedPayload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, article, 0, prefix.Length);
            Buffer.BlockCopy(encodedPayload, 0, article, prefix.Length, encodedPayload.Length);
            Buffer.BlockCopy(suffix, 0, article, prefix.Length + encodedPayload.Length, suffix.Length);
            return article;
        }

        /// <summary>
        /// Encodes decoded bytes into yEnc payload bytes using the same escape set expected by validator tests.
        /// </summary>
        /// <param name="decoded">Decoded input bytes to encode.</param>
        /// <returns>Encoded payload bytes with CRLF line wrapping.</returns>
        private static byte[] EncodeYEnc(byte[] decoded)
        {
            List<byte> output = new(decoded.Length + (decoded.Length / 32));
            int lineCount = 0;

            for (int i = 0; i < decoded.Length; i++)
            {
                byte encoded = unchecked((byte)(decoded[i] + 42));
                bool mustEscape = encoded is 0 or 9 or 10 or 13 or 32 or 46 or 61;

                if (mustEscape)
                {
                    output.Add((byte)'=');
                    output.Add(unchecked((byte)(encoded + 64)));
                    lineCount += 2;
                }
                else
                {
                    output.Add(encoded);
                    lineCount++;
                }

                if (lineCount >= 128)
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    lineCount = 0;
                }
            }

            if (output.Count == 0 || output[^1] != (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            return [.. output];
        }

        /// <summary>
        /// Applies NNTP dot-stuffing to encoded payload line starts for transport-level fixture synthesis.
        /// </summary>
        /// <param name="payload">Encoded payload bytes before dot-stuffing.</param>
        /// <returns>Dot-stuffed payload bytes.</returns>
        private static byte[] DotStuffLineStarts(byte[] payload)
        {
            List<byte> output = new(payload.Length + 32);
            bool atLineStart = true;

            for (int i = 0; i < payload.Length; i++)
            {
                byte b = payload[i];
                if (atLineStart && b == (byte)'.')
                {
                    output.Add((byte)'.');
                }

                output.Add(b);
                atLineStart = b == (byte)'\n';
            }

            return [.. output];
        }

        /// <summary>
        /// Computes CRC-32 for decoded payload bytes to populate synthetic yEnc trailer metadata.
        /// </summary>
        /// <param name="payload">Decoded payload bytes.</param>
        /// <returns>CRC-32 value using polynomial <c>0xEDB88320</c>.</returns>
        private static uint Crc32(byte[] payload)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < payload.Length; i++)
            {
                crc = (crc >> 8) ^ CrcTable[(int)((crc ^ payload[i]) & 0xFF)];
            }

            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// Creates deterministic pseudo-random payload bytes for repeatable tests.
        /// </summary>
        /// <param name="size">Requested payload size in bytes.</param>
        /// <param name="seed">Random seed controlling generated sequence.</param>
        /// <returns>Generated payload bytes.</returns>
        private static byte[] BuildPayload(int size, int seed)
        {
            byte[] payload = new byte[size];
            Random random = new(seed);
            random.NextBytes(payload);
            return payload;
        }

        /// <summary>
        /// Locates the encoded payload start within a synthetic yEnc article.
        /// </summary>
        /// <param name="article">Synthetic article bytes that include NNTP framing and yEnc control lines.</param>
        /// <returns>Zero-based offset to first encoded payload byte.</returns>
        private static int FindPayloadOffset(byte[] article)
        {
            int beginIndex = IndexOfAscii(article, "=ybegin ");
            int beginLineEnd = Array.IndexOf(article, (byte)'\n', beginIndex);
            int offset = beginLineEnd + 1;

            if (offset < article.Length && article.AsSpan(offset).StartsWith("=ypart "u8))
            {
                int partLineEnd = Array.IndexOf(article, (byte)'\n', offset);
                offset = partLineEnd + 1;
            }

            return offset;
        }

        /// <summary>
        /// Replaces an ASCII token with replacement text up to end-of-line in a copied buffer for corruption tests.
        /// </summary>
        /// <param name="buffer">Source buffer to copy and mutate.</param>
        /// <param name="search">ASCII token to find.</param>
        /// <param name="replacement">Replacement ASCII text inserted at token location.</param>
        /// <returns>Mutated buffer copy.</returns>
        private static byte[] ReplaceAsciiInCopy(byte[] buffer, string search, string replacement)
        {
            byte[] searchBytes = System.Text.Encoding.ASCII.GetBytes(search);
            byte[] replacementBytes = System.Text.Encoding.ASCII.GetBytes(replacement);
            int idx = buffer.AsSpan().IndexOf(searchBytes);
            Assert.True(idx >= 0, $"Expected to find '{search}'");

            int lineEnd = Array.IndexOf(buffer, (byte)'\n', idx);
            Assert.True(lineEnd > idx, "Expected target line ending after replacement token.");

            int prefixLength = idx;
            int suffixStart = lineEnd;

            byte[] newBuffer = new byte[prefixLength + replacementBytes.Length + (buffer.Length - suffixStart)];
            Buffer.BlockCopy(buffer, 0, newBuffer, 0, prefixLength);
            Buffer.BlockCopy(replacementBytes, 0, newBuffer, prefixLength, replacementBytes.Length);
            Buffer.BlockCopy(buffer, suffixStart, newBuffer, prefixLength + replacementBytes.Length, buffer.Length - suffixStart);

            return newBuffer;
        }

        /// <summary>
        /// Replaces a same-length ASCII token in a copied buffer for metadata tampering tests.
        /// </summary>
        /// <param name="buffer">Source buffer to copy and mutate.</param>
        /// <param name="search">Exact ASCII token to replace.</param>
        /// <param name="replacement">Replacement token with matching length.</param>
        /// <returns>Mutated buffer copy.</returns>
        private static byte[] ReplaceExactTokenInCopy(byte[] buffer, string search, string replacement)
        {
            byte[] source = (byte[])buffer.Clone();
            byte[] searchBytes = System.Text.Encoding.ASCII.GetBytes(search);
            byte[] replacementBytes = System.Text.Encoding.ASCII.GetBytes(replacement);
            Assert.Equal(searchBytes.Length, replacementBytes.Length);

            int idx = source.AsSpan().IndexOf(searchBytes);
            Assert.True(idx >= 0, $"Expected to find exact token '{search}'");

            replacementBytes.CopyTo(source.AsSpan(idx, replacementBytes.Length));
            return source;
        }

        /// <summary>
        /// Decodes one logical encoded payload line as the validator should decode transport-normalized input.
        /// </summary>
        /// <param name="payloadLine">Logical encoded payload line without CRLF terminator.</param>
        /// <returns>Decoded bytes for CRC generation in logical leading-dot tests.</returns>
        private static byte[] DecodeLogicalPayloadLine(string payloadLine)
        {
            ReadOnlySpan<byte> line = System.Text.Encoding.ASCII.GetBytes(payloadLine);

            List<byte> decoded = new(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                byte current = line[i];
                if (current == (byte)'=')
                {
                    Assert.True(i + 1 < line.Length, "Expected escaped payload byte after '='.");
                    decoded.Add(unchecked((byte)(line[i + 1] - 42 - 64)));
                    i++;
                }
                else
                {
                    decoded.Add(unchecked((byte)(current - 42)));
                }
            }

            return [.. decoded];
        }

        /// <summary>
        /// Concatenates article byte blocks in order.
        /// </summary>
        /// <param name="segments">Segments to concatenate.</param>
        /// <returns>Single concatenated buffer.</returns>
        private static byte[] Concatenate(params byte[][] segments)
        {
            int total = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                total += segments[i].Length;
            }

            byte[] result = new byte[total];
            int offset = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                Buffer.BlockCopy(segments[i], 0, result, offset, segments[i].Length);
                offset += segments[i].Length;
            }

            return result;
        }

        /// <summary>
        /// Finds an ASCII token within a byte buffer.
        /// </summary>
        /// <param name="buffer">Buffer to search.</param>
        /// <param name="token">ASCII token text.</param>
        /// <returns>Zero-based index of first occurrence, or -1 when not found.</returns>
        private static int IndexOfAscii(byte[] buffer, string token)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(token);
            return buffer.AsSpan().IndexOf(bytes);
        }

        /// <summary>
        /// Lookup table used by <see cref="Crc32(byte[])"/> for deterministic trailer metadata generation.
        /// </summary>
        private static readonly uint[] CrcTable = CreateCrcTable();

        /// <summary>
        /// Builds the CRC-32 lookup table used by test helper checksum generation.
        /// </summary>
        /// <returns>Initialized 256-entry lookup table.</returns>
        private static uint[] CreateCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xEDB88320u;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
