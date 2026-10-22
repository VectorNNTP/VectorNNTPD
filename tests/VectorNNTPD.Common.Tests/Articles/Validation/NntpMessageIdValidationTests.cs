using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using VectorNNTPD.Common.Articles.Validation;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Tests.Articles.Validation
{
    /// <summary>
    /// Asserts RFC 5536 acceptance and rejection, vector-window character classes, scan progress, and allocation.
    /// </summary>
    public sealed class NntpMessageIdValidationTests
    {
        /// <summary>
        /// Accepts a minimum id, dotted and plus local parts, IPv4 and IPv6 literals, an empty literal,
        /// an embedded <c>@</c>, mdtext specials, a 250-octet id, a 17-octet left run, and a 16-octet literal run.
        /// </summary>
        [Fact]
        public void Accepts_rfc5536_msg_ids()
        {
            byte[][] valid =
            [
                "<a@b>"u8.ToArray(),
                "<foo.bar@example.com>"u8.ToArray(),
                "<foo+bar@example.com>"u8.ToArray(),
                "<foo@bar.example>"u8.ToArray(),
                "<foo@[1.2.3.4]>"u8.ToArray(),
                "<foo@[IPv6:2001:db8::1]>"u8.ToArray(),
                "<a@[]>"u8.ToArray(),
                "<foo@[a@b]>"u8.ToArray(),
                "<f@[!\"(),:;<=?@^~]>"u8.ToArray(),
                Build(NntpMessageIdValidation.MaxMessageIdLength),
                RepeatLeft((byte)'a', 17),
                RepeatLiteral((byte)':', 16),
            ];

            foreach (var messageId in valid)
            {
                Assert.True(NntpMessageIdValidation.IsValidMessageId(messageId));
            }
        }

        /// <summary>
        /// Rejects empty and short ids, missing atoms, quoted locals, bad dots, whitespace, trailing junk,
        /// brackets and separators in atoms, broken literals, missing brackets, controls, DEL, non-ASCII, and length 251.
        /// </summary>
        [Fact]
        public void Rejects_tokens_outside_rfc5536()
        {
            byte[][] invalid =
            [
                [],
                "<>"u8.ToArray(),
                "<x>"u8.ToArray(),
                "<@x>"u8.ToArray(),
                "<x@>"u8.ToArray(),
                "<x@@y>"u8.ToArray(),
                "<\"quoted\"@example.com>"u8.ToArray(),
                "<double..dot@example.com>"u8.ToArray(),
                "<.leading@example.com>"u8.ToArray(),
                "<trailing.@example.com>"u8.ToArray(),
                "<foo@.example.com>"u8.ToArray(),
                "<foo@example..com>"u8.ToArray(),
                "<foo bar@example.com>"u8.ToArray(),
                "<foo@bar example.com>"u8.ToArray(),
                "<foo@example.com>extra"u8.ToArray(),
                "<foo[bar]@example.com>"u8.ToArray(),
                "<foo@bar,baz.com>"u8.ToArray(),
                "<foo@bar:baz.com>"u8.ToArray(),
                "<foo@bar\\baz.com>"u8.ToArray(),
                "<foo@[a\\b]>"u8.ToArray(),
                "<foo@[a]b]>"u8.ToArray(),
                "<foo@[abc[def]>"u8.ToArray(),
                "<foo@[a b]>"u8.ToArray(),
                "<foo@[a]b>"u8.ToArray(),
                "x@y"u8.ToArray(),
                "<x@y"u8.ToArray(),
                "x@y>"u8.ToArray(),
                "<a\tb@c>"u8.ToArray(),
                "<a\r\nb@c>"u8.ToArray(),
                [(byte)'<', 0x01, (byte)'@', (byte)'b', (byte)'>'],
                [(byte)'<', 0x7F, (byte)'@', (byte)'b', (byte)'>'],
                "<abc@ex\u00E1mple.com>"u8.ToArray(),
                Build(2),
                Build(NntpMessageIdValidation.MaxMessageIdLength + 1),
            ];

            foreach (var messageId in invalid)
            {
                Assert.False(NntpMessageIdValidation.IsValidMessageId(messageId));
            }
        }

        /// <summary>
        /// For every octet, a 16-byte left-side run is valid exactly when the octet is <c>atext</c>,
        /// and a 16-byte literal run is valid exactly when the octet is <c>mdtext</c>.
        /// </summary>
        [Fact]
        public void Sixteen_byte_runs_match_the_rfc_character_classes()
        {
            for (var value = 0; value < 256; value++)
            {
                var octet = (byte)value;
                Assert.Equal(IsAtext(octet), NntpMessageIdValidation.IsValidMessageId(RepeatLeft(octet, 16)));
                Assert.Equal(IsMdtext(octet), NntpMessageIdValidation.IsValidMessageId(RepeatLiteral(octet, 16)));
            }
        }

        /// <summary>
        /// A five-octet id and a 32-octet id are valid, and writing a second <c>@</c> into the long id rejects it.
        /// </summary>
        [Fact]
        public void Short_scalar_and_long_vector_tokens_follow_the_same_rules()
        {
            var scalar = "<a@b>"u8.ToArray();
            var vector = Build(32);
            Assert.True(NntpMessageIdValidation.IsValidMessageId(scalar));
            Assert.True(NntpMessageIdValidation.IsValidMessageId(vector));
            vector[4] = (byte)'@';
            Assert.False(NntpMessageIdValidation.IsValidMessageId(vector));
        }

        /// <summary>
        /// Ids with at least sixteen remaining bytes whose first window lane is not a uniform match finish within two seconds.
        /// The cases cover a dot, a leading dot, spaces, a space inside a literal, a space-filled literal, and a long colon literal.
        /// </summary>
        [Fact]
        public void Vector_scan_terminates_when_the_first_lane_does_not_match()
        {
            // Sixteen or more bytes remain, and lane 0 is not a match. A zero mask is the
            // all-invalid window. A non-zero mask whose first lane is clear is the dot/@ case.
            byte[][] tokens =
            [
                "<a.bbbbbbbbbbbbbbbb@c>"u8.ToArray(),
                "<.bbbbbbbbbbbbbbbb@c>"u8.ToArray(),
                "<                @b>"u8.ToArray(),
                "<a@[ bbbbbbbbbbbbbbb]>"u8.ToArray(),
                "<a@[                ]>"u8.ToArray(),
                RepeatLiteral((byte)':', 20),
            ];
            bool[] expected = [true, false, false, false, false, true];

            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < tokens.Length; i++)
                    {
                        Assert.Equal(expected[i], NntpMessageIdValidation.IsValidMessageId(tokens[i]));
                    }
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "message-id-vector-termination",
            };

            thread.Start();
            Assert.True(
                done.Wait(TimeSpan.FromSeconds(2)),
                "Message-ID vector scan did not finish; the lane-0 mask returned without advancing.");
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        /// <summary>
        /// After <see cref="Exercise"/> warms the validator, one more call allocates no bytes on this thread.
        /// </summary>
        [Fact]
        public void IsValidMessageId_allocates_nothing_after_warmup()
        {
            var scalar = "<a@b>"u8.ToArray();
            var vector = Build(NntpMessageIdValidation.MaxMessageIdLength);
            var literal = RepeatLiteral((byte)'1', 32);
            var invalid = "<double..dot@example.com>"u8.ToArray();
            // Exercise compiles the measured body, including the inlined validator, before the
            // counter starts. The window is one call of that body. GetAllocatedBytesForCurrentThread
            // counts only this thread, so the window must not contain the test method's own hot loop:
            // tiered compilation can charge a one-time JIT cost to that loop.
            Exercise(scalar, vector, literal, invalid);

            var before = GC.GetAllocatedBytesForCurrentThread();
            Exercise(scalar, vector, literal, invalid);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        /// <summary>
        /// Runs the four validator inputs. Optimized on the first call so the measured call is not
        /// a tier-0 body being replaced.
        /// </summary>
        /// <param name="scalar">Short valid Message-ID.</param>
        /// <param name="vector">Message-ID at the maximum accepted length.</param>
        /// <param name="literal">Valid domain-literal Message-ID.</param>
        /// <param name="invalid">Message-ID with a consecutive-dot local part.</param>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void Exercise(byte[] scalar, byte[] vector, byte[] literal, byte[] invalid)
        {
            for (var i = 0; i < 2_000; i++)
            {
                _ = NntpMessageIdValidation.IsValidMessageId(scalar);
                _ = NntpMessageIdValidation.IsValidMessageId(vector);
                _ = NntpMessageIdValidation.IsValidMessageId(literal);
                _ = NntpMessageIdValidation.IsValidMessageId(invalid);
            }
        }

        /// <summary>
        /// Builds <c>&lt;</c>, <paramref name="count"/> copies of <paramref name="value"/>, and <c>@b&gt;</c>.
        /// </summary>
        /// <param name="value">Octet repeated in the local part.</param>
        /// <param name="count">Number of repetitions.</param>
        /// <returns>The Message-ID bytes.</returns>
        private static byte[] RepeatLeft(byte value, int count)
        {
            var bytes = new byte[count + 4];
            bytes[0] = (byte)'<';
            bytes.AsSpan(1, count).Fill(value);
            bytes[^3] = (byte)'@';
            bytes[^2] = (byte)'b';
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Builds <c>&lt;a@[</c>, <paramref name="count"/> copies of <paramref name="value"/>, and <c>]&gt;</c>.
        /// </summary>
        /// <param name="value">Octet repeated inside the domain literal.</param>
        /// <param name="count">Number of repetitions.</param>
        /// <returns>The Message-ID bytes.</returns>
        private static byte[] RepeatLiteral(byte value, int count)
        {
            var bytes = new byte[count + 6];
            bytes[0] = (byte)'<';
            bytes[1] = (byte)'a';
            bytes[2] = (byte)'@';
            bytes[3] = (byte)'[';
            bytes.AsSpan(4, count).Fill(value);
            bytes[^2] = (byte)']';
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Builds a Message-ID of <paramref name="totalLength"/> octets. Lengths of at least 5 are <c>&lt;a@</c>, <c>b</c> fill, and <c>&gt;</c>.
        /// Shorter lengths set only the brackets that fit and leave the remaining octets zero.
        /// </summary>
        /// <param name="totalLength">Exact buffer length, including brackets when they fit.</param>
        /// <returns>The candidate bytes.</returns>
        private static byte[] Build(int totalLength)
        {
            var bytes = new byte[totalLength];
            if (totalLength < 5)
            {
                if (totalLength > 0)
                {
                    bytes[0] = (byte)'<';
                }

                if (totalLength > 1)
                {
                    bytes[^1] = (byte)'>';
                }

                return bytes;
            }

            bytes[0] = (byte)'<';
            bytes[1] = (byte)'a';
            bytes[2] = (byte)'@';
            bytes[^1] = (byte)'>';
            bytes.AsSpan(3, totalLength - 4).Fill((byte)'b');
            return bytes;
        }

        /// <summary>
        /// Returns whether <paramref name="value"/> is RFC 5322 <c>atext</c>, excluding dot and <c>@</c>.
        /// </summary>
        /// <param name="value">Octet to classify.</param>
        /// <returns><see langword="true"/> for letters, digits, and the <c>atext</c> punctuation set.</returns>
        private static bool IsAtext(byte value) =>
            value == 0x21
            || (uint)(value - 0x23) <= (uint)(0x27 - 0x23)
            || (uint)(value - 0x2A) <= (uint)(0x2B - 0x2A)
            || value == 0x2D
            || value == 0x2F
            || (uint)(value - 0x30) <= 9u
            || value == 0x3D
            || value == 0x3F
            || (uint)(value - 0x41) <= (uint)(0x5A - 0x41)
            || (uint)(value - 0x5E) <= (uint)(0x7E - 0x5E);

        /// <summary>
        /// Returns whether <paramref name="value"/> is RFC 5536 <c>mdtext</c>: 33-61, 63-90, or 94-126.
        /// </summary>
        /// <param name="value">Octet to classify.</param>
        /// <returns><see langword="true"/> when the octet is in one of those ranges.</returns>
        private static bool IsMdtext(byte value) =>
            (uint)(value - 33) <= (uint)(61 - 33)
            || (uint)(value - 63) <= (uint)(90 - 63)
            || (uint)(value - 94) <= (uint)(126 - 94);
    }
}
