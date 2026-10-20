using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Dns;
using VectorNNTPD.Common.Tests.TestDoubles;

namespace VectorNNTPD.Common.Tests.Dns
{
    /// <summary>
    /// Deterministic wire-format fixtures for the owned ACME DNS stack (no public DNS required).
    /// </summary>
    public sealed class DnsWireFormatTests
    {
        /// <summary>
        /// Asserts that a TXT query is longer than the DNS header, echoes the generated query id, leaves recursion desired unset, and asks one question.
        /// </summary>
        [Fact]
        public void QueryBuilder_BuildsSingleQuestionTxtQuery()
        {
            byte[] query = DnsQueryBuilder.Build("_acme-challenge.example.com", DnsRecordType.Txt, out ushort queryId);
            Assert.True(query.Length > DnsWireFormat.HeaderSize);
            Assert.Equal(queryId, BinaryPrimitives.ReadUInt16BigEndian(query));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2))); // RD=0 default
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4))); // QDCOUNT
        }

        /// <summary>
        /// Asserts that requesting recursion sets the RD bit and leaves the other header flag bits clear.
        /// </summary>
        [Fact]
        public void QueryBuilder_RecursiveDesired_SetsRdBit()
        {
            byte[] query = DnsQueryBuilder.Build(
                "example.com",
                DnsRecordType.Ns,
                out _,
                recursionDesired: true);
            Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2)));
        }

        /// <summary>
        /// Asserts that a valid ACME name has no validation error, encodes to the computed wire length, and decodes back to the same name.
        /// </summary>
        [Fact]
        public void TryValidateDnsName_ValidName_EncodesAndRoundTrips()
        {
            const string name = "_acme-challenge.example.com";
            Assert.True(DnsWireFormat.TryValidateDnsName(name, out string? error));
            Assert.Null(error);

            int wireLength = DnsWireFormat.ComputeWireNameLength(name);
            Span<byte> buffer = stackalloc byte[wireLength];
            int written = DnsWireFormat.EncodeDnsName(name, buffer);
            Assert.Equal(wireLength, written);

            int offset = 0;
            Assert.True(DnsNameCodec.TryReadDomainName(buffer, ref offset, out string decoded));
            Assert.Equal(name, decoded);
            Assert.Equal(written, offset);
        }

        /// <summary>
        /// Asserts that an empty label, a non-ASCII label, and an empty name fail DNS name validation.
        /// </summary>
        [Fact]
        public void TryValidateDnsName_RejectsEmptyLabelAndNonAscii()
        {
            Assert.False(DnsWireFormat.TryValidateDnsName("example..com", out _));
            Assert.False(DnsWireFormat.TryValidateDnsName("café.example.com", out _));
            Assert.False(DnsWireFormat.TryValidateDnsName(string.Empty, out _));
        }

        /// <summary>
        /// Asserts that a golden TXT response yields the challenge bytes and string, and that a different token does not match.
        /// </summary>
        [Fact]
        public void TxtParser_GoldenAcmeTxtResponse_ParsesAndMatches()
        {
            const string recordName = "_acme-challenge.example.com";
            const string challenge = "abc123-challenge-token";
            byte[] response = BuildGoldenTxtResponse(0x1234, recordName, challenge);
            byte[] expectedBytes = Encoding.ASCII.GetBytes(challenge);

            List<byte[]> parsed = [];
            Assert.True(DnsTxtParser.TryParseTxtRecords(response, 0x1234, recordName, parsed));
            Assert.Single(parsed);
            Assert.Equal(expectedBytes, parsed[0]);
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x1234, recordName, expectedBytes));
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x1234, recordName, "wrong"u8));

            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x1234, recordName);
            Assert.Equal([challenge], strings);
        }

        /// <summary>
        /// Asserts that two TXT character-strings in one RDATA are concatenated into a single parsed string.
        /// </summary>
        [Fact]
        public void TxtParser_MultiStringRdata_ConcatenatesSegments()
        {
            const string recordName = "txt.example.com";
            byte[] response = BuildMultiStringTxtResponse(0x42, recordName, "hello", "world");
            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x42, recordName);
            Assert.Equal(["helloworld"], strings);
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x42, recordName, "helloworld"u8));
        }

        /// <summary>
        /// Asserts that a response with a different transaction id parses no TXT strings and does not match the token.
        /// </summary>
        [Fact]
        public void TxtParser_TransactionIdMismatch_ReturnsEmpty()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x1111, recordName, "token");
            Assert.Empty(DnsTxtParser.ParseTxtStrings(response, 0x2222, recordName));
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x2222, recordName, "token"u8));
        }

        /// <summary>
        /// Asserts that a response forced to NXDOMAIN, with QR still set, parses no TXT strings.
        /// </summary>
        [Fact]
        public void TxtParser_RcodeFailure_ReturnsEmpty()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x99, recordName, "token");
            // Force RCODE=3 (NXDOMAIN) while keeping QR+AA.
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8403);
            Assert.Empty(DnsTxtParser.ParseTxtStrings(response, 0x99, recordName));
        }

        /// <summary>
        /// Asserts that a truncated header parses no TXT records and that the try-parse API returns <see langword="false"/> instead of throwing.
        /// </summary>
        [Fact]
        public void TxtParser_MalformedPacket_DoesNotThrow()
        {
            byte[] truncated = [0x00, 0x01, 0x84, 0x00];
            Assert.Empty(DnsTxtParser.ParseTxtStrings(truncated, 1, "example.com"));
            Assert.False(DnsTxtParser.TryParseTxtRecords(truncated, 1, "example.com", []));
        }

        /// <summary>
        /// Asserts that a TXT digest under a different answer owner name is not accepted for the queried name.
        /// </summary>
        [Fact]
        public void TxtParser_WrongAnswerOwnerName_RejectsDigest()
        {
            const string queried = "_acme-challenge.example.com";
            const string other = "evil.example.com";
            const string digest = "challenge-digest";
            byte[] response = BuildTxtResponse(
                0x55,
                questionName: queried,
                answerOwnerName: other,
                txtValue: digest,
                flags: 0x8400);

            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x55, queried, Encoding.ASCII.GetBytes(digest)));
            List<byte[]> parsed = [];
            Assert.True(DnsTxtParser.TryParseTxtRecords(response, 0x55, queried, parsed));
            Assert.Empty(parsed);
        }

        /// <summary>
        /// Asserts that a question name that differs from the requested name fails the parse.
        /// </summary>
        [Fact]
        public void TxtParser_QuestionNameMismatch_Rejects()
        {
            const string queried = "_acme-challenge.example.com";
            byte[] response = BuildTxtResponse(
                0x56,
                questionName: "other.example.com",
                answerOwnerName: queried,
                txtValue: "digest",
                flags: 0x8400);

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x56, queried, []));
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x56, queried, "digest"u8));
        }

        /// <summary>
        /// Asserts that a question type other than TXT fails the parse.
        /// </summary>
        [Fact]
        public void TxtParser_QuestionTypeMismatch_Rejects()
        {
            const string name = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x57, name, "digest");
            // Overwrite QTYPE to A after the QNAME.
            int qnameLength = DnsWireFormat.ComputeWireNameLength(name);
            BinaryPrimitives.WriteUInt16BigEndian(
                response.AsSpan(DnsWireFormat.HeaderSize + qnameLength),
                DnsRecordType.A);

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x57, name, []));
        }

        /// <summary>
        /// Asserts that a question class other than IN fails the parse.
        /// </summary>
        [Fact]
        public void TxtParser_QuestionClassMismatch_Rejects()
        {
            const string name = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x58, name, "digest");
            int qnameLength = DnsWireFormat.ComputeWireNameLength(name);
            BinaryPrimitives.WriteUInt16BigEndian(
                response.AsSpan(DnsWireFormat.HeaderSize + qnameLength + 2),
                3); // CH

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x58, name, []));
        }

        /// <summary>
        /// Asserts that DNS name comparison is case-insensitive for question and answer owners.
        /// </summary>
        [Fact]
        public void TxtParser_NameCaseDifferences_Accepted()
        {
            const string queried = "_acme-challenge.example.com";
            const string digest = "case-ok";
            byte[] response = BuildTxtResponse(
                0x59,
                questionName: "_ACME-Challenge.EXAMPLE.COM",
                answerOwnerName: "_Acme-Challenge.Example.Com",
                txtValue: digest,
                flags: 0x8400);

            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x59, queried, Encoding.ASCII.GetBytes(digest)));
            Assert.True(DnsNameCodec.NamesEqual(queried, "_ACME-Challenge.EXAMPLE.COM"));
        }

        /// <summary>
        /// Asserts that a different name that only shares a suffix is rejected.
        /// </summary>
        [Fact]
        public void TxtParser_SuffixOnlyName_Rejected()
        {
            const string queried = "example.com";
            const string digest = "suffix-digest";
            byte[] response = BuildTxtResponse(
                0x5A,
                questionName: queried,
                answerOwnerName: "evil.example.com",
                txtValue: digest,
                flags: 0x8400);

            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x5A, queried, Encoding.ASCII.GetBytes(digest)));
            Assert.False(DnsNameCodec.NamesEqual(queried, "evil.example.com"));
        }

        /// <summary>
        /// Asserts that compressed question and answer names decompress and match the queried owner.
        /// </summary>
        [Fact]
        public void TxtParser_CompressedNames_Accepted()
        {
            const string recordName = "_acme-challenge.example.com";
            const string digest = "compressed-ok";
            byte[] response = BuildCompressedTxtResponse(0x5B, recordName, digest);

            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x5B, recordName, Encoding.ASCII.GetBytes(digest)));
            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x5B, recordName);
            Assert.Equal([digest], strings);
        }

        /// <summary>
        /// Asserts that a self-referential compression pointer in the answer owner fails the parse.
        /// </summary>
        [Fact]
        public void TxtParser_InvalidCompressionPointer_Rejects()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x5C, recordName, "token");
            // Replace answer owner with a pointer to itself (offset of the pointer bytes).
            int qnameLength = DnsWireFormat.ComputeWireNameLength(recordName);
            int answerOwnerOffset = DnsWireFormat.HeaderSize + qnameLength + DnsWireFormat.QuestionSuffixSize;
            // Shrink is awkward; overwrite first two bytes of answer name with self pointer.
            response[answerOwnerOffset] = 0xC0;
            response[answerOwnerOffset + 1] = (byte)answerOwnerOffset;

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x5C, recordName, []));
        }

        /// <summary>
        /// Asserts that a response with the TC bit set is rejected as incomplete proof.
        /// </summary>
        [Fact]
        public void TxtParser_TruncatedFlag_Rejects()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x5D, recordName, "token");
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8600); // QR+AA+TC

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x5D, recordName, []));
            Assert.True(DnsResponseHeader.IsTruncated(response));
        }

        /// <summary>
        /// Asserts that a packet with a valid TXT and a malformed TXT RDATA does not return partial success.
        /// </summary>
        [Fact]
        public void TxtParser_ValidPlusMalformedTxt_RejectsEntireParse()
        {
            const string recordName = "example.com";
            const string good = "good-digest";
            byte[] response = BuildTwoTxtAnswersWithSecondMalformed(0x5E, recordName, good);

            List<byte[]> parsed = [];
            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x5E, recordName, parsed));
            Assert.Empty(parsed);
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x5E, recordName, Encoding.ASCII.GetBytes(good)));
        }

        /// <summary>
        /// Asserts that a non-TXT answer and a TXT of another class do not corrupt offsets for a following matching TXT.
        /// </summary>
        [Fact]
        public void TxtParser_NonTxtAndWrongClass_DoNotCorruptOffsets()
        {
            const string recordName = "example.com";
            const string digest = "after-skip";
            byte[] response = BuildMixedRrTxtResponse(0x5F, recordName, digest);

            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x5F, recordName, Encoding.ASCII.GetBytes(digest)));
            Assert.Equal([digest], DnsTxtParser.ParseTxtStrings(response, 0x5F, recordName));
        }

        /// <summary>
        /// Asserts that authoritative TXT proof requires AA=1 and rejects a non-authoritative reply with the digest.
        /// </summary>
        [Fact]
        public void TxtParser_NonAuthoritative_RejectedWhenAaRequired()
        {
            const string recordName = "example.com";
            const string digest = "digest";
            byte[] response = BuildTxtResponse(
                0x60,
                questionName: recordName,
                answerOwnerName: recordName,
                txtValue: digest,
                flags: 0x8000); // QR only, AA clear

            Assert.False(DnsTxtParser.TryParseTxtRecords(response, 0x60, recordName, [], requireAuthoritativeAnswer: true));
            Assert.False(DnsTxtParser.ResponseContainsTxt(
                response,
                0x60,
                recordName,
                Encoding.ASCII.GetBytes(digest),
                requireAuthoritativeAnswer: true));

            // Explicit non-authoritative parse path may still accept the packet when AA is not required.
            Assert.True(DnsTxtParser.ResponseContainsTxt(
                response,
                0x60,
                recordName,
                Encoding.ASCII.GetBytes(digest),
                requireAuthoritativeAnswer: false));
        }

        /// <summary>
        /// Asserts that leading or trailing whitespace in raw TXT data does not satisfy an unpadded digest.
        /// </summary>
        [Fact]
        public void TxtParser_WhitespacePadding_DoesNotMatch()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x61, recordName, " digest ");
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x61, recordName, "digest"u8));
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x61, recordName, " digest "u8));
        }

        /// <summary>
        /// Asserts that surrounding quote characters in raw TXT data do not satisfy an unquoted digest.
        /// </summary>
        [Fact]
        public void TxtParser_QuotedRawTxt_DoesNotMatchUnquotedDigest()
        {
            const string recordName = "example.com";
            byte[] response = BuildGoldenTxtResponse(0x62, recordName, "\"digest\"");
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x62, recordName, "digest"u8));
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x62, recordName, "\"digest\""u8));
        }

        /// <summary>
        /// Asserts that multiple TXT records under the queried owner are all returned.
        /// </summary>
        [Fact]
        public void TxtParser_MultipleTxtRecords_AllReturned()
        {
            const string recordName = "example.com";
            byte[] response = BuildTwoTxtAnswers(0x63, recordName, "one", "two");
            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x63, recordName);
            Assert.Equal(["one", "two"], strings);
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x63, recordName, "two"u8));
        }

        /// <summary>
        /// Asserts that empty TXT RDATA cannot satisfy a non-empty challenge digest.
        /// </summary>
        [Fact]
        public void TxtParser_EmptyTxtRdata_DoesNotSatisfyChallenge()
        {
            const string recordName = "example.com";
            byte[] response = BuildEmptyTxtResponse(0x64, recordName);
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x64, recordName, "digest"u8));
            List<byte[]> parsed = [];
            Assert.True(DnsTxtParser.TryParseTxtRecords(response, 0x64, recordName, parsed));
            Assert.Single(parsed);
            Assert.Empty(parsed[0]);
        }

        /// <summary>
        /// Asserts that skipping the question name of an <c>example.com</c> TXT query advances 13 bytes past the header.
        /// </summary>
        [Fact]
        public void NameCodec_SkipsQuestionName()
        {
            byte[] query = DnsQueryBuilder.Build("example.com", DnsRecordType.Txt, out _);
            int offset = DnsWireFormat.HeaderSize;
            Assert.True(DnsNameCodec.TrySkipName(query, ref offset));
            Assert.Equal(DnsWireFormat.HeaderSize + 13, offset);
        }

        /// <summary>
        /// Asserts that skipping a compression pointer advances two bytes and does not follow the pointer.
        /// </summary>
        [Fact]
        public void NameCodec_CompressionPointer_AdvancesTwoBytesWithoutFollowing()
        {
            byte[] packet = new byte[DnsWireFormat.HeaderSize + 2];
            packet[12] = 0xC0;
            packet[13] = 0;
            int offset = DnsWireFormat.HeaderSize;
            Assert.True(DnsNameCodec.TrySkipName(packet, ref offset));
            Assert.Equal(DnsWireFormat.HeaderSize + 2, offset);
        }

        /// <summary>
        /// Asserts that reading a compression pointer that targets itself fails instead of looping.
        /// </summary>
        [Fact]
        public void NameCodec_SelfReferentialCompressionPointer_FailsBounded()
        {
            byte[] packet = new byte[DnsWireFormat.HeaderSize + 2];
            packet[12] = 0xC0;
            packet[13] = 12; // pointer to self
            int offset = DnsWireFormat.HeaderSize;
            Assert.False(DnsNameCodec.TryReadDomainName(packet, ref offset, out _));
        }

        /// <summary>
        /// Asserts that TXT lookup keeps only values present in every successful authoritative answer.
        /// </summary>
        /// <returns>A task that completes when the intersection assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_IntersectionSemantics_RequiresAllSuccessfulAnswers()
        {
            var a = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 53);
            var b = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 53);
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([a, b]),
                (address, _, _) =>
                {
                    IReadOnlyList<string> values = address.Equals(a.Address)
                        ? ["shared", "only-a"]
                        : ["shared", "only-b"];
                    return Task.FromResult(values);
                });

            IReadOnlyList<string> values = await resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None);
            Assert.Equal(["shared"], values.OrderBy(static s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// Asserts that lookup returns no TXT strings when every injectable authoritative query throws <see cref="IOException"/>.
        /// </summary>
        /// <returns>A task that completes when the empty-result assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_InjectableEndpoints_EmptyWhenAllFail()
        {
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>(
                    [new IPEndPoint(IPAddress.Parse("203.0.113.1"), 53)]),
                (_, _, _) => throw new IOException("simulated authoritative query failure"));

            IReadOnlyList<string> values = await resolver.LookupTxtAsync(
                "_acme-challenge.example.com",
                CancellationToken.None);
            Assert.Empty(values);
        }

        /// <summary>
        /// Asserts that an empty nameserver list throws <see cref="AcmeChallengeException"/> with category <c>ns_discovery_failed</c>.
        /// </summary>
        /// <returns>A task that completes when the exception category is asserted.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_NoNameservers_ThrowsNsDiscoveryFailed()
        {
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([]));

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None));
            Assert.Equal("ns_discovery_failed", ex.Category);
        }

        /// <summary>
        /// Asserts that a failed nameserver does not vote into the intersection of successful answers.
        /// </summary>
        /// <returns>A task that completes when the assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_FailedServer_DoesNotCountAsSuccess()
        {
            var a = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 53);
            var b = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 53);
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([a, b]),
                (address, _, _) =>
                {
                    if (address.Equals(a.Address))
                    {
                        throw new IOException("ns-a failed");
                    }

                    return Task.FromResult<IReadOnlyList<string>>(["digest"]);
                });

            IReadOnlyList<string> values = await resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None);
            Assert.Equal(["digest"], values);
        }

        /// <summary>
        /// Asserts that whitespace in returned TXT values is preserved and does not become a trim-normalized match.
        /// </summary>
        /// <returns>A task that completes when the assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_DoesNotTrimTxtValues()
        {
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>(
                    [new IPEndPoint(IPAddress.Parse("198.51.100.1"), 53)]),
                (_, _, _) => Task.FromResult<IReadOnlyList<string>>([" digest "]));

            IReadOnlyList<string> values = await resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None);
            Assert.Equal([" digest "], values);
        }

        /// <summary>
        /// Asserts that a matching digest returned for a wrong owner name never satisfies propagation.
        /// </summary>
        /// <returns>A task that completes when the timeout category is asserted.</returns>
        [Fact]
        public async Task Dns01_WaitPropagated_WrongOwnerName_DoesNotSucceed()
        {
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ScriptedTxtResolver();
            var solver = new Dns01Solver(
                client,
                "zone",
                resolver,
                "example.com",
                stateDir: null,
                propagationTimeout: TimeSpan.FromMilliseconds(80),
                propagationInterval: TimeSpan.FromMilliseconds(10));

            var spec = new Dns01ChallengeSpec("example.com", "digest");
            // Seed only under a different name than the challenge RecordName.
            resolver.Set("evil.example.com", "digest");

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(
                () => solver.WaitPropagatedAsync([spec], CancellationToken.None));
            Assert.Equal("propagation_timeout", ex.Category);
        }

        /// <summary>
        /// Asserts that resolver exceptions are treated as non-evidence and do not satisfy propagation.
        /// </summary>
        /// <returns>A task that completes when the timeout category is asserted.</returns>
        [Fact]
        public async Task Dns01_WaitPropagated_ResolverException_DoesNotCountAsSuccess()
        {
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ThrowingTxtResolver();
            var solver = new Dns01Solver(
                client,
                "zone",
                resolver,
                "example.com",
                stateDir: null,
                propagationTimeout: TimeSpan.FromMilliseconds(80),
                propagationInterval: TimeSpan.FromMilliseconds(10));

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(
                () => solver.WaitPropagatedAsync(
                    [new Dns01ChallengeSpec("example.com", "digest")],
                    CancellationToken.None));
            Assert.Equal("propagation_timeout", ex.Category);
        }

        /// <summary>
        /// Asserts that whitespace-padded resolver answers do not satisfy an exact challenge digest.
        /// </summary>
        /// <returns>A task that completes when the timeout category is asserted.</returns>
        [Fact]
        public async Task Dns01_WaitPropagated_WhitespacePadding_DoesNotMatch()
        {
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ScriptedTxtResolver();
            var solver = new Dns01Solver(
                client,
                "zone",
                resolver,
                "example.com",
                stateDir: null,
                propagationTimeout: TimeSpan.FromMilliseconds(80),
                propagationInterval: TimeSpan.FromMilliseconds(10));

            var spec = new Dns01ChallengeSpec("example.com", "digest");
            resolver.Set(spec.RecordName, " digest ");

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(
                () => solver.WaitPropagatedAsync([spec], CancellationToken.None));
            Assert.Equal("propagation_timeout", ex.Category);
        }

        /// <summary>
        /// Asserts that an exact authoritative digest match satisfies propagation.
        /// </summary>
        /// <returns>A task that completes when propagation succeeds.</returns>
        [Fact]
        public async Task Dns01_WaitPropagated_ExactMatch_Succeeds()
        {
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ScriptedTxtResolver();
            var solver = new Dns01Solver(
                client,
                "zone",
                resolver,
                "example.com",
                stateDir: null,
                propagationTimeout: TimeSpan.FromSeconds(2),
                propagationInterval: TimeSpan.FromMilliseconds(10));

            var spec = new Dns01ChallengeSpec("example.com", "exact-digest");
            resolver.Set(spec.RecordName, "exact-digest");
            await solver.WaitPropagatedAsync([spec], CancellationToken.None);
        }

        /// <summary>
        /// Asserts that caller cancellation stops propagation waiting.
        /// </summary>
        /// <returns>A task that completes when cancellation is observed.</returns>
        [Fact]
        public async Task Dns01_WaitPropagated_Cancellation_Propagates()
        {
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ScriptedTxtResolver();
            var solver = new Dns01Solver(
                client,
                "zone",
                resolver,
                "example.com",
                stateDir: null,
                propagationTimeout: TimeSpan.FromSeconds(30),
                propagationInterval: TimeSpan.FromMilliseconds(50));

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => solver.WaitPropagatedAsync(
                    [new Dns01ChallengeSpec("example.com", "never")],
                    cts.Token));
        }

        /// <summary>
        /// Builds a QR+AA response with one TXT question and one TXT answer whose RDATA is a single character-string.
        /// </summary>
        private static byte[] BuildGoldenTxtResponse(ushort queryId, string recordName, string txtValue)
            => BuildTxtResponse(queryId, recordName, recordName, txtValue, 0x8400);

        /// <summary>
        /// Builds a QR+AA TXT response whose answer RDATA contains two character-strings.
        /// </summary>
        private static byte[] BuildMultiStringTxtResponse(ushort queryId, string recordName, string part1, string part2)
        {
            byte[] query = DnsQueryBuilder.Build(recordName, DnsRecordType.Txt, out _);
            int qnameLength = query.Length - DnsWireFormat.HeaderSize - DnsWireFormat.QuestionSuffixSize;
            ReadOnlySpan<byte> qname = query.AsSpan(DnsWireFormat.HeaderSize, qnameLength);

            byte[] rdata = new byte[2 + part1.Length + part2.Length];
            rdata[0] = (byte)part1.Length;
            Encoding.ASCII.GetBytes(part1, rdata.AsSpan(1));
            rdata[1 + part1.Length] = (byte)part2.Length;
            Encoding.ASCII.GetBytes(part2, rdata.AsSpan(2 + part1.Length));

            return BuildSingleAnswerResponse(queryId, 0x8400, qname, qname, DnsRecordType.Txt, DnsRecordType.ClassIn, rdata);
        }

        /// <summary>
        /// Builds a response with independent question and answer owner names.
        /// </summary>
        private static byte[] BuildTxtResponse(
            ushort queryId,
            string questionName,
            string answerOwnerName,
            string txtValue,
            ushort flags)
        {
            byte[] questionWire = EncodeName(questionName);
            byte[] answerWire = EncodeName(answerOwnerName);
            byte[] rdata = EncodeSingleStringRdata(txtValue);
            return BuildSingleAnswerResponse(
                queryId,
                flags,
                questionWire,
                answerWire,
                DnsRecordType.Txt,
                DnsRecordType.ClassIn,
                rdata);
        }

        /// <summary>
        /// Builds a response whose answer owner is a compression pointer back to the question name.
        /// </summary>
        private static byte[] BuildCompressedTxtResponse(ushort queryId, string recordName, string txtValue)
        {
            byte[] qname = EncodeName(recordName);
            byte[] rdata = EncodeSingleStringRdata(txtValue);
            byte[] pointer = [0xC0, DnsWireFormat.HeaderSize]; // pointer to QNAME at offset 12

            int responseLength = DnsWireFormat.HeaderSize
                + qname.Length
                + DnsWireFormat.QuestionSuffixSize
                + pointer.Length
                + DnsWireFormat.ResourceRecordFixedFieldsSize
                + rdata.Length;

            byte[] response = new byte[responseLength];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), queryId);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8400);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);

            int offset = DnsWireFormat.HeaderSize;
            qname.CopyTo(response.AsSpan(offset));
            offset += qname.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            offset += DnsWireFormat.QuestionSuffixSize;

            pointer.CopyTo(response.AsSpan(offset));
            offset += pointer.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8), (ushort)rdata.Length);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            rdata.CopyTo(response.AsSpan(offset));
            return response;
        }

        /// <summary>
        /// Builds two TXT answers under the same owner; the second RDATA has an overlong character-string length.
        /// </summary>
        private static byte[] BuildTwoTxtAnswersWithSecondMalformed(ushort queryId, string recordName, string goodValue)
        {
            byte[] qname = EncodeName(recordName);
            byte[] goodRdata = EncodeSingleStringRdata(goodValue);
            // Character-string length claims 5 following bytes, but RDLENGTH is only 1 (the length byte itself).
            byte[] badRdata = [5];

            int responseLength = DnsWireFormat.HeaderSize
                + qname.Length
                + DnsWireFormat.QuestionSuffixSize
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + goodRdata.Length)
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + badRdata.Length);

            byte[] response = new byte[responseLength];
            WriteHeader(response, queryId, 0x8400, qdCount: 1, anCount: 2);
            int offset = DnsWireFormat.HeaderSize;
            offset = WriteQuestion(response, offset, qname);
            offset = WriteTxtAnswer(response, offset, qname, goodRdata);
            WriteTxtAnswer(response, offset, qname, badRdata);
            return response;
        }

        /// <summary>
        /// Builds two well-formed TXT answers under the same owner.
        /// </summary>
        private static byte[] BuildTwoTxtAnswers(ushort queryId, string recordName, string first, string second)
        {
            byte[] qname = EncodeName(recordName);
            byte[] r1 = EncodeSingleStringRdata(first);
            byte[] r2 = EncodeSingleStringRdata(second);
            int responseLength = DnsWireFormat.HeaderSize
                + qname.Length
                + DnsWireFormat.QuestionSuffixSize
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + r1.Length)
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + r2.Length);

            byte[] response = new byte[responseLength];
            WriteHeader(response, queryId, 0x8400, qdCount: 1, anCount: 2);
            int offset = DnsWireFormat.HeaderSize;
            offset = WriteQuestion(response, offset, qname);
            offset = WriteTxtAnswer(response, offset, qname, r1);
            WriteTxtAnswer(response, offset, qname, r2);
            return response;
        }

        /// <summary>
        /// Builds A (IN), TXT (CH), then TXT (IN) answers so skipped RRs must advance offsets correctly.
        /// </summary>
        private static byte[] BuildMixedRrTxtResponse(ushort queryId, string recordName, string digest)
        {
            byte[] qname = EncodeName(recordName);
            byte[] aRdata = [198, 51, 100, 1];
            byte[] chTxt = EncodeSingleStringRdata("ignore-ch");
            byte[] inTxt = EncodeSingleStringRdata(digest);

            int responseLength = DnsWireFormat.HeaderSize
                + qname.Length
                + DnsWireFormat.QuestionSuffixSize
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + aRdata.Length)
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + chTxt.Length)
                + (qname.Length + DnsWireFormat.ResourceRecordFixedFieldsSize + inTxt.Length);

            byte[] response = new byte[responseLength];
            WriteHeader(response, queryId, 0x8400, qdCount: 1, anCount: 3);
            int offset = DnsWireFormat.HeaderSize;
            offset = WriteQuestion(response, offset, qname);
            offset = WriteAnswer(response, offset, qname, DnsRecordType.A, DnsRecordType.ClassIn, aRdata);
            offset = WriteAnswer(response, offset, qname, DnsRecordType.Txt, 3, chTxt);
            WriteTxtAnswer(response, offset, qname, inTxt);
            return response;
        }

        /// <summary>
        /// Builds a TXT answer with RDLENGTH 0.
        /// </summary>
        private static byte[] BuildEmptyTxtResponse(ushort queryId, string recordName)
        {
            byte[] qname = EncodeName(recordName);
            return BuildSingleAnswerResponse(
                queryId,
                0x8400,
                qname,
                qname,
                DnsRecordType.Txt,
                DnsRecordType.ClassIn,
                []);
        }

        private static byte[] BuildSingleAnswerResponse(
            ushort queryId,
            ushort flags,
            ReadOnlySpan<byte> questionName,
            ReadOnlySpan<byte> answerOwner,
            ushort rrType,
            ushort rrClass,
            ReadOnlySpan<byte> rdata)
        {
            int responseLength = DnsWireFormat.HeaderSize
                + questionName.Length
                + DnsWireFormat.QuestionSuffixSize
                + answerOwner.Length
                + DnsWireFormat.ResourceRecordFixedFieldsSize
                + rdata.Length;

            byte[] response = new byte[responseLength];
            WriteHeader(response, queryId, flags, qdCount: 1, anCount: 1);
            int offset = DnsWireFormat.HeaderSize;
            offset = WriteQuestion(response, offset, questionName);
            WriteAnswer(response, offset, answerOwner, rrType, rrClass, rdata);
            return response;
        }

        private static void WriteHeader(byte[] response, ushort queryId, ushort flags, ushort qdCount, ushort anCount)
        {
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), queryId);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), flags);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), qdCount);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), anCount);
        }

        private static int WriteQuestion(byte[] response, int offset, ReadOnlySpan<byte> qname)
        {
            qname.CopyTo(response.AsSpan(offset));
            offset += qname.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            return offset + DnsWireFormat.QuestionSuffixSize;
        }

        private static int WriteTxtAnswer(byte[] response, int offset, ReadOnlySpan<byte> owner, ReadOnlySpan<byte> rdata)
            => WriteAnswer(response, offset, owner, DnsRecordType.Txt, DnsRecordType.ClassIn, rdata);

        private static int WriteAnswer(
            byte[] response,
            int offset,
            ReadOnlySpan<byte> owner,
            ushort rrType,
            ushort rrClass,
            ReadOnlySpan<byte> rdata)
        {
            owner.CopyTo(response.AsSpan(offset));
            offset += owner.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), rrType);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), rrClass);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8), (ushort)rdata.Length);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            rdata.CopyTo(response.AsSpan(offset));
            return offset + rdata.Length;
        }

        private static byte[] EncodeName(string name)
        {
            byte[] buffer = new byte[DnsWireFormat.ComputeWireNameLength(name)];
            DnsWireFormat.EncodeDnsName(name, buffer);
            return buffer;
        }

        private static byte[] EncodeSingleStringRdata(string txtValue)
        {
            byte[] rdata = new byte[1 + txtValue.Length];
            rdata[0] = (byte)txtValue.Length;
            Encoding.ASCII.GetBytes(txtValue, rdata.AsSpan(1));
            return rdata;
        }

        /// <summary>Injectable resolver that returns scripted TXT values by owner name.</summary>
        private sealed class ScriptedTxtResolver : IAuthoritativeTxtResolver
        {
            private readonly Dictionary<string, IReadOnlyList<string>> _byName =
                new(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, params string[] values) => _byName[name] = values;

            public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_byName.TryGetValue(name, out IReadOnlyList<string>? values))
                {
                    return Task.FromResult(values);
                }

                return Task.FromResult<IReadOnlyList<string>>([]);
            }
        }

        /// <summary>Injectable resolver that always throws a non-cancellation exception.</summary>
        private sealed class ThrowingTxtResolver : IAuthoritativeTxtResolver
        {
            public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("simulated resolver failure");
            }
        }
    }
}
