using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VectorNNTPD.Common.Articles.Validation;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Streams news-log files and extracts Message-IDs without loading the full corpus into memory.
    /// </summary>
    /// <remarks>
    /// Expected line shape (syslog-style prefix, then status, provider, Message-ID, size):
    /// <c>Oct 17 00:00:00.026 + giganews &lt;id@host&gt; 740121 ?</c>.
    /// Status is one of <c>+</c>, <c>-</c>, or <c>j</c>. Provider labels are preserved as logged.
    /// Extracted Message-IDs must also pass <see cref="NntpMessageIdValidation.IsValidMessageId"/>.
    /// This type never fabricates Message-IDs.
    /// </remarks>
    public static class NewsLogMessageIdDiscovery
    {
        /// <summary>Matches one news-log article line and captures status, provider, Message-ID, and size.</summary>
        private static readonly Regex LinePattern = new(
            @"^[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+([+\-j])\s+(\S+)\s+(<[^>\s]+>)\s+(\d+)\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Ensures <paramref name="rootDirectory"/> exists and contains at least one <c>*.log</c> file.
        /// </summary>
        /// <exception cref="InvalidOperationException">Missing directory, empty corpus, or unreadable root.</exception>
        public static void RequireUsableCorpusDirectory(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            if (!Directory.Exists(rootDirectory))
            {
                throw new InvalidOperationException(
                    "News-log corpus directory is missing or unreadable: " + rootDirectory
                    + ". Required default is " + StressCorpusPaths.RequiredNewsLogDirectory
                    + ". Stress will not invent Message-IDs or fall back to another dataset.");
            }

            if (DiscoverLogFiles(rootDirectory).Count == 0)
            {
                throw new InvalidOperationException(
                    "News-log corpus contains no *.log files: " + rootDirectory
                    + ". Stress will not invent Message-IDs or fall back to another dataset.");
            }
        }

        /// <summary>Discovers log files under <paramref name="rootDirectory"/> recursively.</summary>
        /// <param name="rootDirectory">Directory that may contain nested <c>*.log</c> files.</param>
        /// <returns>Ordinal-sorted absolute paths.</returns>
        public static IReadOnlyList<string> DiscoverLogFiles(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            if (!Directory.Exists(rootDirectory))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(rootDirectory, "*.log", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="messageId"/> passes canonical RFC 5536 validation.
        /// </summary>
        public static bool IsCanonicalMessageId(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            var byteCount = Encoding.ASCII.GetByteCount(messageId);
            Span<byte> buffer = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
            Encoding.ASCII.GetBytes(messageId, buffer);
            return NntpMessageIdValidation.IsValidMessageId(buffer);
        }

        /// <summary>
        /// Parses one news-log line.
        /// </summary>
        /// <param name="line">Raw log line.</param>
        /// <param name="entry">Parsed fields when the line matches.</param>
        /// <returns><see langword="true"/> when the line is a valid article log entry.</returns>
        public static bool TryParseLine(ReadOnlySpan<char> line, out NewsLogEntry entry)
        {
            entry = default;
            // Regex has no Span API on all targets; convert only for match.
            var text = line.ToString();
            var match = LinePattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            var status = match.Groups[1].Value[0];
            var provider = match.Groups[2].Value;
            var messageId = match.Groups[3].Value;
            if (!long.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                return false;
            }

            entry = new NewsLogEntry(status, provider, messageId, size);
            return true;
        }

        /// <summary>
        /// Streams every discovered log file, counts entries, and optionally records unique Message-IDs.
        /// </summary>
        /// <param name="rootDirectory">News-log root.</param>
        /// <param name="uniqueMessageIds">
        /// When non-null, unique Message-IDs are added here. Callers that must bound memory may pass
        /// <see langword="null"/> and rely on counts only, or pass a set and clear it after writing a workload file.
        /// </param>
        /// <param name="cancellationToken">Cancels mid-file.</param>
        /// <returns>Aggregate discovery statistics.</returns>
        public static async Task<NewsLogDiscoveryStats> DiscoverAsync(
            string rootDirectory,
            ISet<string>? uniqueMessageIds,
            CancellationToken cancellationToken = default)
        {
            RequireUsableCorpusDirectory(rootDirectory);
            var files = DiscoverLogFiles(rootDirectory);
            long totalLines = 0;
            long valid = 0;
            long rejectedInvalid = 0;
            long malformed = 0;
            long statusPlus = 0;
            long statusMinus = 0;
            long statusJ = 0;
            long giganews = 0;
            long eweka = 0;
            long otherProvider = 0;
            var perFile = new List<NewsLogFileStats>(files.Count);

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long fileLines = 0;
                long fileValid = 0;
                long fileGig = 0;
                long fileEwe = 0;
                await foreach (var line in ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    fileLines++;
                    totalLines++;
                    if (!TryParseLine(line.AsSpan(), out var entry))
                    {
                        malformed++;
                        continue;
                    }

                    if (!IsCanonicalMessageId(entry.MessageId))
                    {
                        rejectedInvalid++;
                        continue;
                    }

                    valid++;
                    fileValid++;
                    uniqueMessageIds?.Add(entry.MessageId);
                    switch (entry.Status)
                    {
                        case '+':
                            statusPlus++;
                            break;
                        case '-':
                            statusMinus++;
                            break;
                        case 'j':
                            statusJ++;
                            break;
                    }

                    if (entry.Provider.Equals("giganews", StringComparison.OrdinalIgnoreCase))
                    {
                        giganews++;
                        fileGig++;
                    }
                    else if (entry.Provider.Equals("eweka", StringComparison.OrdinalIgnoreCase))
                    {
                        eweka++;
                        fileEwe++;
                    }
                    else
                    {
                        otherProvider++;
                    }
                }

                perFile.Add(new NewsLogFileStats(path, fileLines, fileValid, fileGig, fileEwe));
            }

            var uniqueCount = uniqueMessageIds?.Count ?? 0L;
            if (valid == 0 || uniqueCount == 0)
            {
                throw new InvalidOperationException(
                    "News-log corpus produced no usable Message-IDs after parse and canonical validation: "
                    + rootDirectory
                    + $". examined={totalLines} malformed={malformed} rejectedInvalid={rejectedInvalid}. "
                    + "Stress will not invent replacement Message-IDs.");
            }

            var duplicateRate = valid > 0
                ? 100.0 * (valid - uniqueCount) / valid
                : 0.0;
            return new NewsLogDiscoveryStats(
                files.Count,
                totalLines,
                valid,
                uniqueCount,
                malformed,
                rejectedInvalid,
                statusPlus,
                statusMinus,
                statusJ,
                giganews,
                eweka,
                otherProvider,
                duplicateRate,
                perFile);
        }

        /// <summary>
        /// Writes a deterministic workload partition from unique Message-IDs.
        /// </summary>
        /// <param name="uniqueMessageIds">Unique IDs (order is not assumed).</param>
        /// <param name="outputDirectory">Destination for workload text files.</param>
        /// <param name="seed">Shuffle seed for reproducibility.</param>
        /// <param name="warmupCount">Warm-up set size.</param>
        /// <param name="mainCount">Main stress set size.</param>
        /// <param name="repeatableCount">Repeatable comparison set size.</param>
        /// <returns>Counts actually written for each partition.</returns>
        public static WorkloadPartitionCounts WriteWorkloadSets(
            IEnumerable<string> uniqueMessageIds,
            string outputDirectory,
            int seed,
            int warmupCount,
            int mainCount,
            int repeatableCount)
        {
            ArgumentNullException.ThrowIfNull(uniqueMessageIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            // Sort before shuffle so HashSet enumeration order cannot affect reproducibility.
            var ids = uniqueMessageIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
            {
                throw new InvalidOperationException(
                    "Cannot write workload sets: no Message-IDs were supplied. "
                    + "Stress will not invent replacement Message-IDs.");
            }

            Shuffle(ids, seed);

            var warmup = TakeSlice(ids, 0, warmupCount);
            var mainStart = warmup.Length;
            var main = TakeSlice(ids, mainStart, mainCount);
            var repeatStart = mainStart + main.Length;
            var repeatable = TakeSlice(ids, repeatStart, repeatableCount);
            if (repeatable.Length == 0 && ids.Length > 0)
            {
                // Prefer a non-empty repeatable set when the corpus is smaller than the requested slices.
                repeatable = TakeSlice(ids, 0, Math.Min(repeatableCount, ids.Length));
            }

            File.WriteAllLines(Path.Join(outputDirectory, "warmup-message-ids.txt"), warmup);
            File.WriteAllLines(Path.Join(outputDirectory, "main-message-ids.txt"), main);
            File.WriteAllLines(Path.Join(outputDirectory, "repeatable-message-ids.txt"), repeatable);
            return new WorkloadPartitionCounts(warmup.Length, main.Length, repeatable.Length, seed);
        }

        private static string[] TakeSlice(string[] ids, int start, int count)
        {
            if (start >= ids.Length || count <= 0)
            {
                return Array.Empty<string>();
            }

            var length = Math.Min(count, ids.Length - start);
            var slice = new string[length];
            Array.Copy(ids, start, slice, 0, length);
            return slice;
        }

        private static void Shuffle(string[] ids, int seed)
        {
            var rng = new Random(seed);
            for (var i = ids.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (ids[i], ids[j]) = (ids[j], ids[i]);
            }
        }

        private static async IAsyncEnumerable<string> ReadLinesAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    yield break;
                }

                yield return line;
            }
        }
    }

    /// <summary>One parsed news-log article line.</summary>
    /// <param name="Status">Log status character (<c>+</c>, <c>-</c>, or <c>j</c>).</param>
    /// <param name="Provider">Provider label as logged (for example <c>giganews</c>).</param>
    /// <param name="MessageId">Message-ID including angle brackets.</param>
    /// <param name="LoggedSize">Size field from the log line.</param>
    public readonly record struct NewsLogEntry(char Status, string Provider, string MessageId, long LoggedSize);

    /// <summary>Per-file discovery counters.</summary>
    public sealed record NewsLogFileStats(
        string Path,
        long LineCount,
        long ValidCount,
        long GiganewsCount,
        long EwekaCount);

    /// <summary>Aggregate news-log discovery statistics.</summary>
    /// <param name="RejectedInvalidMessageIds">
    /// Lines that matched the log shape but failed <see cref="NntpMessageIdValidation"/>.
    /// </param>
    public sealed record NewsLogDiscoveryStats(
        int FileCount,
        long TotalLogEntries,
        long ValidIds,
        long UniqueIds,
        long MalformedEntries,
        long RejectedInvalidMessageIds,
        long StatusPlus,
        long StatusMinus,
        long StatusJ,
        long ProviderGiganews,
        long ProviderEweka,
        long ProviderOther,
        double DuplicateRatePercent,
        IReadOnlyList<NewsLogFileStats> PerFile);

    /// <summary>Sizes of written workload partitions.</summary>
    public sealed record WorkloadPartitionCounts(int Warmup, int Main, int Repeatable, int Seed);
}
