using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.BackFillerService.Tests.Acme
{
    /// <summary>
    /// AcmeProblemDiagnostics redacts PEM and long tokens in problem detail formatting.
    /// </summary>

    public sealed class AcmeProblemDiagnosticsTests
    {
        /// <summary>
        /// Contract: FormatProblemFields redacts pem blocks and bounded long tokens.
        /// </summary>

        [Fact]
        public void FormatProblemFields_redacts_pem_blocks_and_bounded_long_tokens()
        {
            var token = new string('a', 80);
            var shorter = new string('b', 79);
            var detail =
                $"prefix {token} {shorter} -----BEGIN CERTIFICATE-----\nsecret\n-----END CERTIFICATE----- tail";

            var text = AcmeProblemDiagnostics.FormatProblemFields(
                preface: null,
                type: null,
                status: null,
                identifier: null,
                detail,
                subproblems: null);

            Assert.Equal($"detail=prefix [redacted-token] {shorter} [redacted-pem] tail", text);
        }

        /// <summary>
        /// Contract: FormatProblemFields shrinks a greedy token until the trailing word boundary holds.
        /// </summary>

        [Fact]
        public void FormatProblemFields_shrinks_a_greedy_token_until_the_trailing_word_boundary_holds()
        {
            var letters = new string('a', 80);

            var text = Detail(letters + "-");

            Assert.Equal("detail=[redacted-token]-", text);
        }

        /// <summary>
        /// Contract: FormatProblemFields includes a trailing hyphen when a word character follows.
        /// </summary>

        [Fact]
        public void FormatProblemFields_includes_a_trailing_hyphen_when_a_word_character_follows()
        {
            var body = new string('a', 79) + "-\u00E9";

            var text = Detail(body);

            Assert.Equal("detail=[redacted-token]\u00E9", text);
        }

        /// <summary>
        /// Contract: FormatProblemFields does not redact a run that lacks a word boundary.
        /// </summary>

        [Fact]
        public void FormatProblemFields_does_not_redact_a_run_that_lacks_a_word_boundary()
        {
            var token = new string('a', 80);

            var text = Detail("\u00E9" + token);

            Assert.Equal("detail=\u00E9" + token, text);
        }

        /// <summary>
        /// Contract: FormatProblemFields does not redact seventy nine token characters.
        /// </summary>

        [Fact]
        public void FormatProblemFields_does_not_redact_seventy_nine_token_characters()
        {
            var text = Detail(new string('a', 79));

            Assert.Equal("detail=" + new string('a', 79), text);
        }

        /// <summary>Formats only the detail field so redaction tests do not depend on the other problem fields.</summary>
        /// <param name="detail">Detail text passed to <see cref="AcmeProblemDiagnostics.FormatProblemFields"/>.</param>
        private static string Detail(string detail) =>
            AcmeProblemDiagnostics.FormatProblemFields(
                preface: null,
                type: null,
                status: null,
                identifier: null,
                detail,
                subproblems: null);
    }
}
