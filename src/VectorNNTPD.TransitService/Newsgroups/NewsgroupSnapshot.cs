using System.Buffers.Text;
using System.Text;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Newsgroups
{
    /// <summary>
    /// Immutable, fully constructed newsgroup catalogue snapshot.
    /// </summary>
    /// <remarks>
    /// Readers observe either the previous complete snapshot or this complete snapshot.
    /// No lock is required. Command handlers must capture <see cref="INewsgroupCatalogue.Current"/>
    /// once and use only that instance.
    /// </remarks>
    public sealed class NewsgroupSnapshot
    {
        private static readonly NewsgroupNameComparer NameComparer = new();

        /// <summary>Empty published snapshot (zero groups; valid LIST body is empty).</summary>
        public static NewsgroupSnapshot Empty { get; } = Create(Array.Empty<NewsgroupDefinition>());

        private readonly NewsgroupRecord[] _groups;

        private NewsgroupSnapshot(
            NewsgroupRecord[] groups,
            byte[] listActiveBody,
            byte[] listActiveComplete,
            byte[] listCountsBody,
            byte[] listCountsComplete,
            byte[] listNewsgroupsBody,
            byte[] listNewsgroupsComplete)
        {
            _groups = groups;
            Groups = groups;
            ListActiveBody = listActiveBody;
            ListActiveComplete = listActiveComplete;
            ListCountsBody = listCountsBody;
            ListCountsComplete = listCountsComplete;
            ListNewsgroupsBody = listNewsgroupsBody;
            ListNewsgroupsComplete = listNewsgroupsComplete;
        }

        /// <summary>Gets groups in deterministic ordinal-ignore-case name order.</summary>
        public IReadOnlyList<NewsgroupRecord> Groups { get; }

        /// <summary>Gets concatenated precomputed LIST ACTIVE lines (no status or terminator).</summary>
        public ReadOnlyMemory<byte> ListActiveBody { get; }

        /// <summary>Gets the complete LIST / LIST ACTIVE multiline wire (status + body + terminator).</summary>
        public ReadOnlyMemory<byte> ListActiveComplete { get; }

        /// <summary>Gets concatenated precomputed LIST COUNTS lines (no status or terminator).</summary>
        public ReadOnlyMemory<byte> ListCountsBody { get; }

        /// <summary>Gets the complete LIST COUNTS multiline wire (status + body + terminator).</summary>
        public ReadOnlyMemory<byte> ListCountsComplete { get; }

        /// <summary>Gets concatenated precomputed LIST NEWSGROUPS lines (no status or terminator).</summary>
        public ReadOnlyMemory<byte> ListNewsgroupsBody { get; }

        /// <summary>Gets the complete LIST NEWSGROUPS multiline wire (status + body + terminator).</summary>
        public ReadOnlyMemory<byte> ListNewsgroupsComplete { get; }

        /// <summary>Builds a snapshot from raw database rows. Fails the entire build on any malformed row.</summary>
        public static NewsgroupSnapshot Create(IReadOnlyList<NntpGroupRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var records = new NewsgroupRecord[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                records[i] = CreateRecord(rows[i], i);
            }

            return Publish(records);
        }

        /// <summary>Builds a snapshot from already-typed definitions (tests and in-process construction).</summary>
        public static NewsgroupSnapshot Create(IReadOnlyList<NewsgroupDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);
            var records = new NewsgroupRecord[definitions.Count];
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                records[i] = CreateRecord(
                    new NntpGroupRow(
                        definition.GroupName,
                        definition.GroupDescription,
                        definition.CountHigh,
                        definition.CountLow,
                        (byte)definition.PostingStatus),
                    i);
            }

            return Publish(records);
        }

        /// <summary>
        /// Looks up a group by name using ASCII ordinal case-insensitive comparison.
        /// The original stored name is retained on the returned record.
        /// </summary>
        public bool TryGet(ReadOnlySpan<byte> groupName, out NewsgroupRecord group)
        {
            var lo = 0;
            var hi = _groups.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var cmp = CompareOrdinalIgnoreCase(_groups[mid].GroupNameBytes.Span, groupName);
                if (cmp == 0)
                {
                    group = _groups[mid];
                    return true;
                }

                if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            group = null!;
            return false;
        }

        private static NewsgroupSnapshot Publish(NewsgroupRecord[] records)
        {
            Array.Sort(records, NameComparer);
            for (var i = 1; i < records.Length; i++)
            {
                if (CompareOrdinalIgnoreCase(records[i - 1].GroupNameBytes.Span, records[i].GroupNameBytes.Span) == 0)
                {
                    throw new NewsgroupCatalogueException(
                        $"Duplicate newsgroup name '{records[i].GroupName}' (case-insensitive).");
                }
            }

            var activeBody = ConcatenateLines(records, static r => r.ListActiveLine);
            var countsBody = ConcatenateLines(records, static r => r.ListCountsLine);
            var newsgroupsBody = ConcatenateLines(records, static r => r.ListNewsgroupsLine);
            return new NewsgroupSnapshot(
                records,
                activeBody,
                ConcatenateComplete(NntpResponses.ListOfNewsgroupsFollows, activeBody),
                countsBody,
                ConcatenateComplete(NntpResponses.ListOfNewsgroupsFollows, countsBody),
                newsgroupsBody,
                ConcatenateComplete(NntpResponses.ListOfNewsgroupsFollows, newsgroupsBody));
        }

        private static NewsgroupRecord CreateRecord(NntpGroupRow row, int index)
        {
            if (string.IsNullOrEmpty(row.GroupName))
            {
                throw new NewsgroupCatalogueException($"nntpgroups row {index} has an empty group_name.");
            }

            if (!NewsgroupPostingStatusOctets.IsSupported(row.PostingStatus))
            {
                throw new NewsgroupCatalogueException(
                    $"nntpgroups row '{row.GroupName}' has invalid posting_status 0x{row.PostingStatus:X2}.");
            }

            var nameBytes = Encoding.UTF8.GetBytes(row.GroupName);
            var description = row.GroupDescription ?? string.Empty;
            var descriptionBytes = Encoding.UTF8.GetBytes(description);
            var status = (NewsgroupPostingStatus)row.PostingStatus;
            var estimate = EstimateCount(row.CountLow, row.CountHigh);
            return new NewsgroupRecord(
                row.GroupName,
                nameBytes,
                description,
                row.CountHigh,
                row.CountLow,
                status,
                estimate,
                EncodeActiveLine(nameBytes, row.CountHigh, row.CountLow, row.PostingStatus),
                EncodeCountsLine(nameBytes, row.CountHigh, row.CountLow, estimate, row.PostingStatus),
                EncodeNewsgroupsLine(nameBytes, descriptionBytes),
                EncodeGroupSelectedLine(nameBytes, estimate, row.CountLow, row.CountHigh));
        }

        /// <summary>RFC 3977 §6.1.1 estimate: 0 when empty; otherwise high − low + 1 without wrapping.</summary>
        internal static ulong EstimateCount(ulong low, ulong high)
        {
            if (high < low)
            {
                return 0;
            }

            if (low == 0 && high == 0)
            {
                return 0;
            }

            var span = high - low;
            return span == ulong.MaxValue ? ulong.MaxValue : span + 1;
        }

        private static byte[] EncodeActiveLine(
            ReadOnlySpan<byte> name,
            ulong high,
            ulong low,
            byte status)
        {
            Span<byte> highDigits = stackalloc byte[20];
            Span<byte> lowDigits = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(high, highDigits, out var highLen)
                || !Utf8Formatter.TryFormat(low, lowDigits, out var lowLen))
            {
                throw new NewsgroupCatalogueException("Failed to format unsigned water marks.");
            }

            var length = name.Length + 1 + highLen + 1 + lowLen + 1 + 1 + 2;
            var line = new byte[length];
            var offset = 0;
            name.CopyTo(line.AsSpan(offset));
            offset += name.Length;
            line[offset++] = (byte)' ';
            highDigits[..highLen].CopyTo(line.AsSpan(offset));
            offset += highLen;
            line[offset++] = (byte)' ';
            lowDigits[..lowLen].CopyTo(line.AsSpan(offset));
            offset += lowLen;
            line[offset++] = (byte)' ';
            line[offset++] = status;
            line[offset++] = (byte)'\r';
            line[offset] = (byte)'\n';
            return StuffIfNeeded(line);
        }

        private static byte[] EncodeCountsLine(
            ReadOnlySpan<byte> name,
            ulong high,
            ulong low,
            ulong estimate,
            byte status)
        {
            Span<byte> highDigits = stackalloc byte[20];
            Span<byte> lowDigits = stackalloc byte[20];
            Span<byte> estimateDigits = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(high, highDigits, out var highLen)
                || !Utf8Formatter.TryFormat(low, lowDigits, out var lowLen)
                || !Utf8Formatter.TryFormat(estimate, estimateDigits, out var estimateLen))
            {
                throw new NewsgroupCatalogueException("Failed to format LIST COUNTS water marks.");
            }

            var length = name.Length + 1 + highLen + 1 + lowLen + 1 + estimateLen + 1 + 1 + 2;
            var line = new byte[length];
            var offset = 0;
            name.CopyTo(line.AsSpan(offset));
            offset += name.Length;
            line[offset++] = (byte)' ';
            highDigits[..highLen].CopyTo(line.AsSpan(offset));
            offset += highLen;
            line[offset++] = (byte)' ';
            lowDigits[..lowLen].CopyTo(line.AsSpan(offset));
            offset += lowLen;
            line[offset++] = (byte)' ';
            estimateDigits[..estimateLen].CopyTo(line.AsSpan(offset));
            offset += estimateLen;
            line[offset++] = (byte)' ';
            line[offset++] = status;
            line[offset++] = (byte)'\r';
            line[offset] = (byte)'\n';
            return StuffIfNeeded(line);
        }

        private static byte[] EncodeNewsgroupsLine(ReadOnlySpan<byte> name, ReadOnlySpan<byte> description)
        {
            var length = name.Length + 1 + description.Length + 2;
            var line = new byte[length];
            var offset = 0;
            name.CopyTo(line.AsSpan(offset));
            offset += name.Length;
            line[offset++] = (byte)' ';
            description.CopyTo(line.AsSpan(offset));
            offset += description.Length;
            line[offset++] = (byte)'\r';
            line[offset] = (byte)'\n';
            return StuffIfNeeded(line);
        }

        private static byte[] EncodeGroupSelectedLine(
            ReadOnlySpan<byte> name,
            ulong estimate,
            ulong low,
            ulong high)
        {
            Span<byte> estimateDigits = stackalloc byte[20];
            Span<byte> lowDigits = stackalloc byte[20];
            Span<byte> highDigits = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(estimate, estimateDigits, out var estimateLen)
                || !Utf8Formatter.TryFormat(low, lowDigits, out var lowLen)
                || !Utf8Formatter.TryFormat(high, highDigits, out var highLen))
            {
                throw new NewsgroupCatalogueException("Failed to format GROUP water marks.");
            }

            // "211 " + estimate + " " + low + " " + high + " " + name + CRLF
            var length = 4 + estimateLen + 1 + lowLen + 1 + highLen + 1 + name.Length + 2;
            var line = new byte[length];
            line[0] = (byte)'2';
            line[1] = (byte)'1';
            line[2] = (byte)'1';
            line[3] = (byte)' ';
            var offset = 4;
            estimateDigits[..estimateLen].CopyTo(line.AsSpan(offset));
            offset += estimateLen;
            line[offset++] = (byte)' ';
            lowDigits[..lowLen].CopyTo(line.AsSpan(offset));
            offset += lowLen;
            line[offset++] = (byte)' ';
            highDigits[..highLen].CopyTo(line.AsSpan(offset));
            offset += highLen;
            line[offset++] = (byte)' ';
            name.CopyTo(line.AsSpan(offset));
            offset += name.Length;
            line[offset++] = (byte)'\r';
            line[offset] = (byte)'\n';
            return line;
        }

        private static byte[] StuffIfNeeded(byte[] line)
        {
            if (line.Length == 0 || line[0] != (byte)'.')
            {
                return line;
            }

            var stuffed = new byte[line.Length + 1];
            stuffed[0] = (byte)'.';
            line.CopyTo(stuffed, 1);
            return stuffed;
        }

        private static byte[] ConcatenateLines(
            NewsgroupRecord[] records,
            Func<NewsgroupRecord, ReadOnlyMemory<byte>> selector)
        {
            var total = 0;
            for (var i = 0; i < records.Length; i++)
            {
                total = checked(total + selector(records[i]).Length);
            }

            var body = new byte[total];
            var offset = 0;
            for (var i = 0; i < records.Length; i++)
            {
                var line = selector(records[i]).Span;
                line.CopyTo(body.AsSpan(offset));
                offset += line.Length;
            }

            return body;
        }

        private static byte[] ConcatenateComplete(ReadOnlyMemory<byte> status, ReadOnlySpan<byte> body)
        {
            var total = checked(status.Length + body.Length + NntpResponses.MultilineTerminator.Length);
            var complete = new byte[total];
            status.Span.CopyTo(complete);
            body.CopyTo(complete.AsSpan(status.Length));
            NntpResponses.MultilineTerminator.Span.CopyTo(complete.AsSpan(status.Length + body.Length));
            return complete;
        }

        internal static int CompareOrdinalIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var n = Math.Min(left.Length, right.Length);
            for (var i = 0; i < n; i++)
            {
                var a = NntpAscii.FoldUpper(left[i]);
                var b = NntpAscii.FoldUpper(right[i]);
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private sealed class NewsgroupNameComparer : IComparer<NewsgroupRecord>
        {
            public int Compare(NewsgroupRecord? x, NewsgroupRecord? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x is null)
                {
                    return -1;
                }

                if (y is null)
                {
                    return 1;
                }

                var ignoreCase = CompareOrdinalIgnoreCase(x.GroupNameBytes.Span, y.GroupNameBytes.Span);
                return ignoreCase != 0
                    ? ignoreCase
                    : x.GroupNameBytes.Span.SequenceCompareTo(y.GroupNameBytes.Span);
            }
        }
    }

    /// <summary>Typed newsgroup fields used to construct a snapshot without a database row.</summary>
    /// <param name="GroupName">Original group name retained for wire output.</param>
    /// <param name="GroupDescription">Group description (UTF-8 on the wire).</param>
    /// <param name="CountHigh">Unsigned high water mark.</param>
    /// <param name="CountLow">Unsigned low water mark.</param>
    /// <param name="PostingStatus">Exactly <c>y</c>, <c>n</c>, <c>m</c>, <c>x</c>, or <c>j</c>. The RFC 6048 <c>=&lt;newsgroup&gt;</c> form is not supported.</param>
    public readonly record struct NewsgroupDefinition(
        string GroupName,
        string GroupDescription,
        ulong CountHigh,
        ulong CountLow,
        NewsgroupPostingStatus PostingStatus);
}
