namespace VectorNNTPD.Common.Articles.Parsing
{
    /// <summary>
    /// Classifies how a Path header would be rewritten without mutating the article buffer.
    /// </summary>
    internal enum ArticlePathKind
    {
        /// <summary>No Path header was present.</summary>
        Missing = 0,

        /// <summary>Path was present but contained only separators or whitespace.</summary>
        Empty = 1,

        /// <summary>The leftmost validated component already equals the local identity (case-insensitive).</summary>
        AlreadyContainsLocalIdentity = 2,

        /// <summary>
        /// The local identity is not the leftmost hop. Writes use <see cref="ArticlePathMode"/> rather than this classification.
        /// </summary>
        NeedsLocalIdentityPrepend = 3,
    }

    /// <summary>
    /// Selects whether a Path rewrite records a receiving-system traversal.
    /// </summary>
    internal enum ArticlePathMode
    {
        /// <summary>
        /// Validate and normalize Path without adding an application hop.
        /// <c>news.usenet.ninja</c> is inserted when absent and reduced to one occurrence.
        /// </summary>
        Normalize = 0,

        /// <summary>
        /// Normalize Path and always prepend the receiving system's application FQDN.
        /// </summary>
        Traverse = 1,
    }

    /// <summary>
    /// Byte-oriented Path validation and canonical-write primitives.
    /// </summary>
    /// <remarks>
    /// These helpers determine validity and the rewrite kind. They do not rewrite the article buffer.
    /// Common owns the organizational tracker hostname <c>news.usenet.ninja</c> and application-hop
    /// prepend rules. Callers supply only their own application FQDN.
    /// </remarks>
    internal static class ArticlePathCanonicalizer
    {
        /// <summary>Maximum accepted Path value length.</summary>
        public const int MaxPathLength = 8192;

        /// <summary>
        /// Organizational Vector tracker hostname. Present exactly once after any Vector application has seen the article.
        /// </summary>
        internal static ReadOnlySpan<byte> OrganizationalTrackerHost => "news.usenet.ninja"u8;

        /// <summary>
        /// Returns whether <paramref name="siteName"/> can be the Path tracker component.
        /// </summary>
        /// <param name="siteName"><c>nntpsharedconfig.sitename</c>.</param>
        /// <returns>
        /// <see langword="true"/> when the value is non-empty, at most 50 characters, and a printable ASCII Path component without <c>!</c>.
        /// </returns>
        internal static bool IsValidSiteName(string? siteName)
        {
            if (string.IsNullOrEmpty(siteName) || siteName.Length > 50)
            {
                return false;
            }

            foreach (var ch in siteName)
            {
                if (ch is <= ' ' or '!' or > '\u007E')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Validates a raw Path value and classifies the rewrite that a later materializer would apply.
        /// </summary>
        /// <param name="rawPath">Raw Path header value bytes, or empty when the header is absent.</param>
        /// <param name="localIdentity">Local application FQDN. Analysis reports whether it is leftmost; writes use <see cref="ArticlePathMode"/>.</param>
        /// <param name="pathPresent">Whether a Path header was present.</param>
        /// <param name="kind">Rewrite classification when validation succeeds.</param>
        /// <param name="containsOrganizationalTracker">
        /// Whether a Path token equals the tracker host using existing case-insensitive token rules.
        /// </param>
        /// <param name="failureCode">Failure code when the Path value is invalid.</param>
        /// <returns><see langword="true"/> when the Path is usable (including a missing header).</returns>
        internal static bool TryAnalyze(
            ReadOnlySpan<byte> rawPath,
            ReadOnlySpan<byte> localIdentity,
            bool pathPresent,
            out ArticlePathKind kind,
            out bool containsOrganizationalTracker,
            out NntpArticleParseFailureCode failureCode)
            => TryAnalyze(
                rawPath,
                localIdentity,
                pathPresent,
                OrganizationalTrackerHost,
                out kind,
                out containsOrganizationalTracker,
                out failureCode);

        /// <summary>
        /// Validates <paramref name="rawPath"/> and classifies the rewrite against an explicit tracker host.
        /// </summary>
        /// <param name="rawPath">Raw Path header value bytes, or empty when the header is absent.</param>
        /// <param name="localIdentity">Local application FQDN. Analysis reports whether it is leftmost.</param>
        /// <param name="pathPresent">Whether a Path header was present. When <see langword="false"/>, the result is <see cref="ArticlePathKind.Missing"/>.</param>
        /// <param name="organizationalTracker">Tracker host compared with Path tokens using the existing case-insensitive rules.</param>
        /// <param name="kind">Rewrite classification when validation succeeds.</param>
        /// <param name="containsOrganizationalTracker">
        /// Whether a Path token equals <paramref name="organizationalTracker"/>.
        /// </param>
        /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.InvalidPath"/> when the value is too long, is not printable ASCII, or contains a component that is not a valid Path token. <see cref="NntpArticleParseFailureCode.None"/> on success.</param>
        /// <returns><see langword="true"/> when the Path is usable, including a missing or empty header.</returns>
        internal static bool TryAnalyze(
            ReadOnlySpan<byte> rawPath,
            ReadOnlySpan<byte> localIdentity,
            bool pathPresent,
            ReadOnlySpan<byte> organizationalTracker,
            out ArticlePathKind kind,
            out bool containsOrganizationalTracker,
            out NntpArticleParseFailureCode failureCode)
        {
            kind = ArticlePathKind.Missing;
            containsOrganizationalTracker = false;
            failureCode = NntpArticleParseFailureCode.None;

            if (!pathPresent)
            {
                return true;
            }

            if (rawPath.Length > MaxPathLength)
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }

            if (!ContainsLineBreak(rawPath))
            {
                return TryAnalyzePrepared(
                    rawPath,
                    localIdentity,
                    organizationalTracker,
                    out kind,
                    out containsOrganizationalTracker,
                    out failureCode);
            }

            if (!IsLegalPathFold(rawPath))
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }

            Span<byte> unfoldBuffer = stackalloc byte[rawPath.Length];
            if (!TryUnfoldPath(rawPath, unfoldBuffer, out var prepared))
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }

            return TryAnalyzePrepared(
                prepared,
                localIdentity,
                organizationalTracker,
                out kind,
                out containsOrganizationalTracker,
                out failureCode);
        }

        /// <summary>
        /// Classifies a Path value that has already been unfolded.
        /// </summary>
        /// <param name="prepared">Unfolded Path bytes.</param>
        /// <param name="localIdentity">Local application FQDN.</param>
        /// <param name="organizationalTracker">Tracker host compared with Path tokens.</param>
        /// <param name="kind">Rewrite classification when validation succeeds.</param>
        /// <param name="containsOrganizationalTracker">Whether a token equals <paramref name="organizationalTracker"/>.</param>
        /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.InvalidPath"/> when the value is rejected.</param>
        /// <returns><see langword="true"/> when the Path is usable.</returns>
        private static bool TryAnalyzePrepared(
            ReadOnlySpan<byte> prepared,
            ReadOnlySpan<byte> localIdentity,
            ReadOnlySpan<byte> organizationalTracker,
            out ArticlePathKind kind,
            out bool containsOrganizationalTracker,
            out NntpArticleParseFailureCode failureCode)
        {
            kind = ArticlePathKind.Missing;
            containsOrganizationalTracker = false;
            failureCode = NntpArticleParseFailureCode.None;

            if (!IsPrintablePathText(prepared))
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }

            var remaining = TrimAscii(prepared);
            if (remaining.IsEmpty)
            {
                kind = ArticlePathKind.Empty;
                return true;
            }

            var sawComponent = false;
            var sawLeadingEmpty = false;
            var pendingEmpty = false;
            var leftmostMatchesLocal = false;
            while (TryConsumePathComponent(ref remaining, out var component))
            {
                if (component.IsEmpty)
                {
                    if (!sawComponent)
                    {
                        sawLeadingEmpty = true;
                        continue;
                    }

                    if (pendingEmpty)
                    {
                        failureCode = NntpArticleParseFailureCode.InvalidPath;
                        return false;
                    }

                    pendingEmpty = true;
                    continue;
                }

                if (sawLeadingEmpty || !IsValidPathComponent(component))
                {
                    failureCode = NntpArticleParseFailureCode.InvalidPath;
                    return false;
                }

                pendingEmpty = false;
                if (!sawComponent)
                {
                    leftmostMatchesLocal = AsciiEqualsIgnoreCase(component, localIdentity);
                }

                sawComponent = true;
                if (AsciiEqualsIgnoreCase(component, organizationalTracker))
                {
                    containsOrganizationalTracker = true;
                }
            }

            if (!sawComponent)
            {
                kind = ArticlePathKind.Empty;
                return true;
            }

            kind = leftmostMatchesLocal
                ? ArticlePathKind.AlreadyContainsLocalIdentity
                : ArticlePathKind.NeedsLocalIdentityPrepend;
            return true;
        }

        /// <summary>
        /// Writes the canonical Path bytes that a later materializer would produce.
        /// </summary>
        /// <param name="rawPath">Original Path value bytes when present; ignored when <paramref name="kind"/> is <see cref="ArticlePathKind.Missing"/> or <see cref="ArticlePathKind.Empty"/>.</param>
        /// <param name="localIdentity">Local application FQDN written as an application hop when needed.</param>
        /// <param name="kind">Rewrite classification from path analysis.</param>
        /// <param name="containsOrganizationalTracker">Whether the organizational tracker token is already present.</param>
        /// <param name="mode">Normalize leaves application hops unchanged. Traverse always prepends <paramref name="localIdentity"/>.</param>
        /// <param name="destination">Destination receiving ASCII Path bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
        /// <remarks>
        /// <see cref="ArticlePathMode.Traverse"/> with the tracker absent writes
        /// <c>news.usenet.ninja!{application-fqdn}!existing</c>.
        /// A later traverse writes <c>{application-fqdn}!existing</c> even when that FQDN is already present.
        /// <see cref="ArticlePathMode.Normalize"/> does not write an application FQDN.
        /// <c>news.usenet.ninja</c> is inserted when absent and reduced to the first occurrence.
        /// One empty token between two hops is a <c>diag-match</c> and is written as <c>!!</c>.
        /// If tracker deduplication removes that marker's right-hand hop, the marker is dropped.
        /// Leading <c>!</c> is invalid. A trailing <c>!</c> is omitted. A run of only separators stays empty.
        /// A CRLF fold is accepted only in the whitespace immediately before a <c>!</c> delimiter.
        /// </remarks>
        internal static bool TryWriteCanonicalPath(
            ReadOnlySpan<byte> rawPath,
            ReadOnlySpan<byte> localIdentity,
            ArticlePathKind kind,
            bool containsOrganizationalTracker,
            ArticlePathMode mode,
            Span<byte> destination,
            out int bytesWritten)
            => TryWriteCanonicalPath(
                rawPath,
                localIdentity,
                kind,
                containsOrganizationalTracker,
                mode,
                OrganizationalTrackerHost,
                destination,
                out bytesWritten);

        /// <summary>Writes the canonical Path bytes for <paramref name="mode"/> into <paramref name="destination"/>.</summary>
        /// <param name="rawPath">Original Path value.</param>
        /// <param name="localIdentity">Identity prepended when the mode traverses.</param>
        /// <param name="kind">Classification produced by path analysis.</param>
        /// <param name="containsOrganizationalTracker">Whether the tracker host is already in <paramref name="rawPath"/>.</param>
        /// <param name="mode">Traverse or normalize.</param>
        /// <param name="organizationalTracker">Tracker hop inserted when it is absent and omitted after its first occurrence.</param>
        /// <param name="destination">Buffer that receives the canonical bytes.</param>
        /// <param name="bytesWritten">Bytes written when the path fits, including zero for an empty path. Zero when the destination is too small.</param>
        /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
        internal static bool TryWriteCanonicalPath(
            ReadOnlySpan<byte> rawPath,
            ReadOnlySpan<byte> localIdentity,
            ArticlePathKind kind,
            bool containsOrganizationalTracker,
            ArticlePathMode mode,
            ReadOnlySpan<byte> organizationalTracker,
            Span<byte> destination,
            out int bytesWritten)
        {
            var traverse = mode == ArticlePathMode.Traverse;
            var localIsTracker = traverse
                && !localIdentity.IsEmpty
                && AsciiEqualsIgnoreCase(localIdentity, organizationalTracker);
            var prependTracker = !containsOrganizationalTracker && !localIsTracker;
            var applicationHop = traverse ? localIdentity : default;

            if (kind is ArticlePathKind.Missing or ArticlePathKind.Empty)
            {
                return TryWriteJoinedComponents(
                    [],
                    prependTracker ? organizationalTracker : default,
                    applicationHop,
                    organizationalTracker,
                    trackerAlreadyEmitted: prependTracker || localIsTracker,
                    destination,
                    out bytesWritten);
            }

            if (!ContainsLineBreak(rawPath))
            {
                return TryWriteJoinedComponents(
                    TrimAscii(rawPath),
                    prependTracker ? organizationalTracker : default,
                    applicationHop,
                    organizationalTracker,
                    trackerAlreadyEmitted: prependTracker || localIsTracker,
                    destination,
                    out bytesWritten);
            }

            if (!IsLegalPathFold(rawPath))
            {
                bytesWritten = 0;
                return false;
            }

            Span<byte> unfoldBuffer = stackalloc byte[rawPath.Length];
            if (!TryUnfoldPath(rawPath, unfoldBuffer, out var prepared))
            {
                bytesWritten = 0;
                return false;
            }

            return TryWriteJoinedComponents(
                TrimAscii(prepared),
                prependTracker ? organizationalTracker : default,
                applicationHop,
                organizationalTracker,
                trackerAlreadyEmitted: prependTracker || localIsTracker,
                destination,
                out bytesWritten);
        }

        /// <summary>
        /// Unfolds a Path value whose only line breaks are CRLF folds.
        /// </summary>
        /// <param name="rawPath">Raw Path bytes, including continuation terminators.</param>
        /// <param name="destination">Buffer at least as long as <paramref name="rawPath"/>.</param>
        /// <param name="prepared">Unfolded bytes inside <paramref name="destination"/>.</param>
        /// <returns>
        /// <see langword="false"/> for a bare CR, a bare LF, a CRLF that is not followed by SP or HTAB,
        /// or a fold that is not in the whitespace immediately before a <c>!</c> delimiter.
        /// </returns>
        private static bool TryUnfoldPath(ReadOnlySpan<byte> rawPath, Span<byte> destination, out ReadOnlySpan<byte> prepared)
        {
            prepared = default;
            if (!IsLegalPathFold(rawPath)
                || !NntpArticleHeaderValueUnfolder.TryUnfold(rawPath, destination, out var written))
            {
                return false;
            }

            prepared = destination[..written];
            return true;
        }

        /// <summary>Returns whether <paramref name="value"/> contains CR or LF.</summary>
        /// <param name="value">Path bytes.</param>
        /// <returns><see langword="true"/> when a line break is present.</returns>
        private static bool ContainsLineBreak(ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is (byte)'\r' or (byte)'\n')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns whether every line break is a CRLF fold placed immediately before a <c>!</c> delimiter.
        /// </summary>
        /// <param name="value">Raw Path bytes.</param>
        /// <returns>
        /// <see langword="false"/> for a bare CR, a bare LF, a fold after <c>!</c>, a fold inside a hop,
        /// or a fold that is not followed by a delimiter.
        /// </returns>
        private static bool IsLegalPathFold(ReadOnlySpan<byte> value)
            => HasOnlyCrlfFolds(value) && FoldsSitBeforeDelimiters(value);

        /// <summary>
        /// Returns whether each fold is trailing whitespace of a hop that is followed by <c>!</c>.
        /// </summary>
        /// <param name="value">Raw Path bytes.</param>
        /// <returns>
        /// <see langword="false"/> when a line break occurs before a hop, inside a hop, after a delimiter,
        /// or after the final hop.
        /// </returns>
        private static bool FoldsSitBeforeDelimiters(ReadOnlySpan<byte> value)
        {
            var segmentStart = 0;
            for (var i = 0; i <= value.Length; i++)
            {
                var atEnd = i == value.Length;
                if (!atEnd && value[i] != (byte)'!')
                {
                    continue;
                }

                if (!SegmentFoldIsBeforeDelimiter(value, segmentStart, i, followedByDelimiter: !atEnd))
                {
                    return false;
                }

                segmentStart = i + 1;
            }

            return true;
        }

        /// <summary>
        /// Returns whether line breaks in one <c>!</c>-separated segment are only trailing whitespace.
        /// </summary>
        /// <param name="value">Raw Path bytes containing <paramref name="start"/> and <paramref name="end"/>.</param>
        /// <param name="start">Inclusive segment start.</param>
        /// <param name="end">Exclusive segment end. The delimiter itself is not included.</param>
        /// <param name="followedByDelimiter">Whether a <c>!</c> follows this segment.</param>
        /// <returns><see langword="false"/> when a fold is not the trailing whitespace before that delimiter.</returns>
        private static bool SegmentFoldIsBeforeDelimiter(
            ReadOnlySpan<byte> value,
            int start,
            int end,
            bool followedByDelimiter)
        {
            var sawContent = false;
            var inTrailingBreak = false;
            var contentAfterBreak = false;
            for (var i = start; i < end; i++)
            {
                var b = value[i];
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (!sawContent)
                    {
                        return false;
                    }

                    inTrailingBreak = true;
                    continue;
                }

                if (b is (byte)' ' or (byte)'\t')
                {
                    continue;
                }

                if (inTrailingBreak)
                {
                    contentAfterBreak = true;
                }

                sawContent = true;
                inTrailingBreak = false;
            }

            return !contentAfterBreak && (!inTrailingBreak || followedByDelimiter);
        }

        /// <summary>
        /// Returns whether every line break is a CRLF. The following SP or HTAB is checked by the unfolder.
        /// </summary>
        /// <param name="value">Raw Path bytes.</param>
        /// <returns><see langword="false"/> for a bare CR or a bare LF.</returns>
        private static bool HasOnlyCrlfFolds(ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var b = value[i];
                if (b == (byte)'\n')
                {
                    return false;
                }

                if (b != (byte)'\r')
                {
                    continue;
                }

                if ((uint)(i + 1) >= (uint)value.Length || value[i + 1] != (byte)'\n')
                {
                    return false;
                }

                i++;
            }

            return true;
        }

        /// <summary>Returns whether every byte is printable ASCII, including SP.</summary>
        /// <param name="value">Unfolded Path bytes.</param>
        /// <returns><see langword="false"/> for a byte below <c>0x20</c> or above <c>0x7E</c>.</returns>
        private static bool IsPrintablePathText(ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is < 0x20 or > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether one Path component is syntactically acceptable.
        /// </summary>
        /// <param name="component">Path component bytes after trimming.</param>
        /// <returns><see langword="true"/> when the component is valid.</returns>
        private static bool IsValidPathComponent(ReadOnlySpan<byte> component)
        {
            for (var i = 0; i < component.Length; i++)
            {
                var b = component[i];
                if (b is <= 0x20 or (byte)'!' or > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes optional prepended hops, then <c>!</c>-separated components from <paramref name="remaining"/>.
        /// </summary>
        /// <param name="remaining">Path value still to split. A single empty component between hops is written as an extra <c>!</c> unless the right-hand hop is a duplicate tracker that this method omits.</param>
        /// <param name="prepend1">First hop, or empty. Used for <see cref="OrganizationalTrackerHost"/> when it must be inserted.</param>
        /// <param name="prepend2">Second hop, or empty. Used for the application FQDN in traverse mode.</param>
        /// <param name="organizationalTracker">Tracker component suppressed after its first occurrence.</param>
        /// <param name="trackerAlreadyEmitted">
        /// When <see langword="true"/>, later components equal to <see cref="OrganizationalTrackerHost"/> are omitted.
        /// The first such component is written and then this suppression applies.
        /// </param>
        /// <param name="destination">Output buffer. Hop bytes are copied unchanged.</param>
        /// <param name="bytesWritten">Bytes written on success. Set to 0 when a hop does not fit.</param>
        /// <returns><see langword="false"/> when a non-empty hop does not fit in <paramref name="destination"/>.</returns>
        private static bool TryWriteJoinedComponents(
            ReadOnlySpan<byte> remaining,
            ReadOnlySpan<byte> prepend1,
            ReadOnlySpan<byte> prepend2,
            ReadOnlySpan<byte> organizationalTracker,
            bool trackerAlreadyEmitted,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;
            var first = true;
            if (!TryWriteHop(prepend1, ref first, destination, ref bytesWritten)
                || !TryWriteHop(prepend2, ref first, destination, ref bytesWritten))
            {
                return false;
            }

            var sawComponent = false;
            var pendingDiagnostic = false;
            while (TryConsumePathComponent(ref remaining, out var component))
            {
                if (component.IsEmpty)
                {
                    if (sawComponent)
                    {
                        if (pendingDiagnostic)
                        {
                            bytesWritten = 0;
                            return false;
                        }

                        pendingDiagnostic = true;
                    }

                    continue;
                }

                sawComponent = true;
                if (AsciiEqualsIgnoreCase(component, organizationalTracker))
                {
                    if (trackerAlreadyEmitted)
                    {
                        // A suppressed hop cannot stay the right-hand side of a pending marker.
                        pendingDiagnostic = false;
                        continue;
                    }

                    trackerAlreadyEmitted = true;
                }

                if (pendingDiagnostic)
                {
                    if (first || bytesWritten >= destination.Length)
                    {
                        bytesWritten = 0;
                        return false;
                    }

                    destination[bytesWritten++] = (byte)'!';
                    pendingDiagnostic = false;
                }

                if (!TryWriteHop(component, ref first, destination, ref bytesWritten))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Appends one hop, prefixed by <c>!</c> when it is not the first hop written.
        /// </summary>
        /// <param name="hop">Hop bytes. Empty is a no-op success.</param>
        /// <param name="first"><see langword="true"/> until the first non-empty hop is written, then set to <see langword="false"/>.</param>
        /// <param name="destination">Output buffer.</param>
        /// <param name="bytesWritten">Running length. Set to 0 when the hop does not fit.</param>
        /// <returns><see langword="false"/> when <paramref name="bytesWritten"/> plus the hop and optional separator exceeds <paramref name="destination"/>.</returns>
        private static bool TryWriteHop(
            ReadOnlySpan<byte> hop,
            ref bool first,
            Span<byte> destination,
            ref int bytesWritten)
        {
            if (hop.IsEmpty)
            {
                return true;
            }

            var needed = hop.Length + (first ? 0 : 1);
            if (bytesWritten + needed > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            if (!first)
            {
                destination[bytesWritten++] = (byte)'!';
            }

            hop.CopyTo(destination[bytesWritten..]);
            bytesWritten += hop.Length;
            first = false;
            return true;
        }

        /// <summary>
        /// Takes the next <c>!</c>-separated component and trims SP and HTAB from it.
        /// </summary>
        /// <param name="remaining">Unconsumed path bytes. Advanced past the component and its separator.</param>
        /// <param name="component">Trimmed component, which may be empty between consecutive <c>!</c> bytes. The last component is the remainder.</param>
        /// <returns><see langword="false"/> only when <paramref name="remaining"/> is empty on entry.</returns>
        private static bool TryConsumePathComponent(ref ReadOnlySpan<byte> remaining, out ReadOnlySpan<byte> component)
        {
            if (remaining.IsEmpty)
            {
                component = default;
                return false;
            }

            var separator = remaining.IndexOf((byte)'!');
            if (separator < 0)
            {
                component = TrimAscii(remaining);
                remaining = default;
                return true;
            }

            component = TrimAscii(remaining[..separator]);
            remaining = remaining[(separator + 1)..];
            return true;
        }

        /// <summary>Drops leading and trailing SP and HTAB.</summary>
        /// <param name="value">One path component.</param>
        /// <returns>A slice of <paramref name="value"/>.</returns>
        private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && value[start] is (byte)' ' or (byte)'\t')
            {
                start++;
            }

            while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
            {
                end--;
            }

            return value[start..end];
        }

        /// <summary>
        /// Compares hops after folding ASCII <c>A-Z</c> to <c>a-z</c>. Other bytes are compared unchanged.
        /// </summary>
        /// <param name="left">Left hop.</param>
        /// <param name="right">Right hop.</param>
        /// <returns><see langword="false"/> when the lengths differ or any folded byte differs.</returns>
        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Folds ASCII <c>A-Z</c> to <c>a-z</c>. Every other byte is returned unchanged.</summary>
        /// <param name="value">Byte to fold.</param>
        /// <returns>The folded byte.</returns>
        private static byte ToLowerAscii(byte value)
            => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;
    }
}
