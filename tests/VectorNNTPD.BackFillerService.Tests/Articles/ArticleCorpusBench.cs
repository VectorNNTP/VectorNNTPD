using System.Diagnostics;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Articles.Parsing;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Processing;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// One-pass measurement of the local <c>.artifacts/articles</c> corpus.
    /// The corpus is raw stored articles. This bench does not frame them as NNTP and does not
    /// claim provider or RabbitMQ throughput. Timings are not assertions.
    /// </summary>
    public sealed class ArticleCorpusBench
    {
        /// <summary>
        /// Walks <c>.artifacts/articles</c> once, writes a report under <c>.artifacts</c>, and asserts every file is classified.
        /// Skips quietly when the corpus directory is absent.
        /// </summary>
        [Fact]
        public void Measure_local_article_corpus()
        {
            var corpus = RepositoryFiles.Under(".artifacts", "articles");
            if (!Directory.Exists(corpus))
            {
                Console.WriteLine("corpus bench not run: .artifacts/articles is absent from this workspace");
                return;
            }

            var files = Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var parser = new NntpArticleParser("backfiller.test");
            const int warmupFiles = 32;
            for (var i = 0; i < Math.Min(warmupFiles, files.Length); i++)
            {
                Process(parser, File.ReadAllBytes(files[i]), out _, out _, out _, out _, out _, out _);
            }

            // No forced collection. The 2026-10-15 corpus report was measured after GC.Collect and is not
            // comparable to a later run. That file stays under .artifacts and is not rewritten here.
            var sizes = new List<long>(files.Length);
            var samples = new List<long>(files.Length);
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var rejectedPaths = new List<string>();
            long inputBytes = 0;
            long outputBytes = 0;
            long validExtraDots = 0;
            long dotLines = 0;
            var dotArticles = 0;
            var valid = 0;
            var invalid = 0;
            var unsupported = 0;
            long parseTicks = 0;
            long materializeTicks = 0;
            long hashTicks = 0;
            var contentHashes = new HashSet<ulong>();
            var duplicateContent = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var total = Stopwatch.StartNew();

            for (var i = 0; i < files.Length; i++)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(files[i]);
                }
                catch (Exception ex) when (TryClassifyReadFailure(ex, out var readReason))
                {
                    unsupported++;
                    Add(reasons, readReason);
                    rejectedPaths.Add(readReason + " " + Path.GetRelativePath(corpus, files[i]));
                    continue;
                }

                sizes.Add(bytes.Length);
                inputBytes += bytes.Length;
                if (!names.Add(Path.GetFileName(files[i])))
                {
                    Add(reasons, "duplicate filename");
                }

                if (!contentHashes.Add(XxHash3.HashToUInt64(bytes)))
                {
                    duplicateContent++;
                }

                if (bytes.Length == 0)
                {
                    unsupported++;
                    Add(reasons, "empty file");
                    rejectedPaths.Add("empty file " + Path.GetRelativePath(corpus, files[i]));
                    continue;
                }

                var outcome = Process(
                    parser,
                    bytes,
                    out var parseElapsed,
                    out var materializeElapsed,
                    out var hashElapsed,
                    out var canonicalBytes,
                    out var stuffedDots,
                    out var reason);
                samples.Add(parseElapsed + materializeElapsed + hashElapsed);
                parseTicks += parseElapsed;
                materializeTicks += materializeElapsed;
                hashTicks += hashElapsed;
                if (outcome == CorpusOutcome.Valid)
                {
                    valid++;
                    outputBytes += canonicalBytes;
                    validExtraDots += stuffedDots;
                }
                else if (outcome == CorpusOutcome.Unsupported)
                {
                    unsupported++;
                    Add(reasons, reason);
                    rejectedPaths.Add(reason + " " + Path.GetRelativePath(corpus, files[i]));
                }
                else
                {
                    invalid++;
                    Add(reasons, reason);
                    rejectedPaths.Add(reason + " " + Path.GetRelativePath(corpus, files[i]));
                }

                if (stuffedDots > 0)
                {
                    dotArticles++;
                    dotLines += stuffedDots;
                }
            }

            total.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var measured = samples.ToArray();
            var sizeArray = sizes.ToArray();
            Array.Sort(sizeArray);
            Array.Sort(measured);
            var processingTicks = parseTicks + materializeTicks + hashTicks;
            var processingSeconds = TicksToSeconds(processingTicks);
            var seconds = total.Elapsed.TotalSeconds;
            var report = new StringBuilder();
            report.AppendLine("article corpus bench");
            report.AppendLine("corpus=" + corpus);
            report.AppendLine("representation=raw stored articles (headers and body). Not NNTP-framed, so this pass does not destuff. Materialize writes the retained wire image, including body dot-stuffing.");
            report.AppendLine("pipeline=NntpArticleParser.Parse + NntpArticleCanonicalMaterializer.Materialize(Traverse) + ArticleFieldTable.Locate + ArticleId + XXH3-64. yEnc validation runs inside Parse when a section is detected.");
            report.AppendLine("not measured=live provider, RabbitMQ, retention admission");
            report.AppendLine($"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} cpus={Environment.ProcessorCount} processor={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
            report.AppendLine($"optimizerDisabled={typeof(ArticleRecordFactory).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled} config={typeof(ArticleRecordFactory).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration}");
            report.AppendLine($"warmupFiles={Math.Min(warmupFiles, files.Length)} measuredPasses=1 gcCollectBeforeMeasuredPass=false");
            report.AppendLine($"files={files.Length} uniqueNames={names.Count} duplicateContentXxh3={duplicateContent}");
            report.AppendLine($"valid={valid} invalid={invalid} unsupported={unsupported}");
            report.AppendLine($"inputBytes={inputBytes} outputBytes={outputBytes} validStuffingExtraDots={validExtraDots}");
            report.AppendLine($"dotArticles={dotArticles} dotArticleProportion={(files.Length == 0 ? 0 : (double)dotArticles / files.Length):R} dotLeadingBodyLines={dotLines}");
            report.AppendLine($"sizeBytes min={At(sizeArray, 0)} median={At(sizeArray, 0.50)} p90={At(sizeArray, 0.90)} p95={At(sizeArray, 0.95)} p99={At(sizeArray, 0.99)} max={At(sizeArray, 1)}");
            report.AppendLine($"wallSeconds={seconds:R} processingSeconds={processingSeconds:R} articlesPerSecond={(processingSeconds <= 0 ? 0 : samples.Count / processingSeconds):R} inputMiBPerSecond={(processingSeconds <= 0 ? 0 : inputBytes / 1024d / 1024d / processingSeconds):R}");
            report.AppendLine($"perArticleMs min={Ms(At(measured, 0)):R} median={Ms(At(measured, 0.50)):R} p90={Ms(At(measured, 0.90)):R} p95={Ms(At(measured, 0.95)):R} p99={Ms(At(measured, 0.99)):R} max={Ms(At(measured, 1)):R} samples={measured.Length}");
            report.AppendLine($"stageSeconds parse={TicksToSeconds(parseTicks):R} materialize={TicksToSeconds(materializeTicks):R} hashAndId={TicksToSeconds(hashTicks):R}");
            report.AppendLine($"allocatedBytes={allocated} allocatedBytesPerArticle={(files.Length == 0 ? 0 : allocated / (double)files.Length):R} allocatedBytesPerInputMiB={(inputBytes == 0 ? 0 : allocated / (inputBytes / 1024d / 1024d)):R}");
            report.AppendLine("allocatedBytes includes each file's input array plus canonical output and parser scratch. One file is resident at a time.");
            report.AppendLine("p99 is an order statistic of the per-article samples in this single pass, not a confidence interval across runs.");
            foreach (var pair in reasons.OrderByDescending(pair => pair.Value))
            {
                report.AppendLine($"reason {pair.Value} {pair.Key}");
            }

            foreach (var rejected in rejectedPaths)
            {
                report.AppendLine("rejected " + rejected);
            }

            var text = report.ToString();
            var reportPath = RepositoryFiles.Under(".artifacts", "article-corpus-bench-no-forced-gc.txt");
            File.WriteAllText(reportPath, text);
            Console.WriteLine(text);
            Assert.Equal(files.Length, valid + invalid + unsupported);
        }

        /// <summary>
        /// Parses, materializes, and hashes one article while recording stage timings and stuffed-dot counts for bench output.
        /// </summary>
        /// <param name="parser">Shared parser instance for the corpus pass.</param>
        /// <param name="bytes">Raw stored article bytes (not NNTP-framed).</param>
        /// <param name="parseElapsed">Stopwatch ticks spent in <see cref="NntpArticleParser.Parse"/>.</param>
        /// <param name="materializeElapsed">Stopwatch ticks spent in canonical materialization.</param>
        /// <param name="hashElapsed">Stopwatch ticks spent locating fields and hashing canonical bytes.</param>
        /// <param name="canonicalBytes">Accepted canonical length when the outcome is valid.</param>
        /// <param name="stuffedDots">Extra body dots counted on the parsed body span.</param>
        /// <param name="reason">Parse or materialize failure label when the outcome is invalid.</param>
        /// <returns>Valid when parse and materialize succeed; invalid otherwise.</returns>
        private static CorpusOutcome Process(
            NntpArticleParser parser,
            byte[] bytes,
            out long parseElapsed,
            out long materializeElapsed,
            out long hashElapsed,
            out int canonicalBytes,
            out int stuffedDots,
            out string reason)
        {
            parseElapsed = 0;
            materializeElapsed = 0;
            hashElapsed = 0;
            canonicalBytes = 0;
            stuffedDots = 0;
            reason = "";
            var parseStart = Stopwatch.GetTimestamp();
            var parsed = parser.Parse(bytes);
            parseElapsed = Stopwatch.GetTimestamp() - parseStart;
            if (parsed.BodyBytes.Length > 0)
            {
                stuffedDots = ArticleBodyWireStuffer.CountExtraDots(parsed.BodyBytes.Span);
            }

            if (!parsed.IsAccepted)
            {
                reason = "parse " + parsed.FailureCode;
                return CorpusOutcome.Invalid;
            }

            var materializeStart = Stopwatch.GetTimestamp();
            var materialized = NntpArticleCanonicalMaterializer.Materialize(
                in parsed,
                ArticlePathMode.Traverse);
            materializeElapsed = Stopwatch.GetTimestamp() - materializeStart;
            if (!materialized.IsAccepted || materialized.ArticleBytes is not { } artData)
            {
                reason = "materialize " + materialized.FailureCode;
                return CorpusOutcome.Invalid;
            }

            var hashStart = Stopwatch.GetTimestamp();
            var fields = ArticleFieldTable.Locate(artData, parsed.SelectedDateHeaderName);
            _ = ArticleId.FromMessageId(fields.MessageId.Slice(artData));
            _ = XxHash3.HashToUInt64(artData.AsSpan(0, materialized.CanonicalLength));
            hashElapsed = Stopwatch.GetTimestamp() - hashStart;
            canonicalBytes = materialized.CanonicalLength;
            return CorpusOutcome.Valid;
        }

        /// <summary>
        /// Classifies one article the way <see cref="Process"/> does, without recording timings.
        /// </summary>
        /// <param name="parser">Parser used for the corpus pass.</param>
        /// <param name="bytes">Article bytes.</param>
        /// <param name="reason">Parse or materialize failure text when the result is invalid.</param>
        /// <returns>Valid when canonical bytes were produced. Invalid when parse or materialize rejected the bytes.</returns>
        /// <remarks>
        /// Malformed articles return failure codes. This method does not catch exceptions.
        /// An unexpected failure fails the caller instead of being counted as a bad article.
        /// </remarks>
        internal static CorpusOutcome ClassifyArticle(NntpArticleParser parser, byte[] bytes, out string reason)
        {
            var outcome = Process(parser, bytes, out _, out _, out _, out _, out _, out reason);
            return outcome;
        }

        /// <summary>
        /// Classifies a file-read failure that should be counted as an unreadable corpus file.
        /// </summary>
        /// <param name="exception">Exception from <see cref="File.ReadAllBytes(string)"/>.</param>
        /// <param name="reason">Read-failure text when this method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>.
        /// Any other exception returns <see langword="false"/> and must be rethrown by the caller.
        /// </returns>
        internal static bool TryClassifyReadFailure(Exception exception, out string reason)
        {
            if (exception is IOException or UnauthorizedAccessException)
            {
                reason = "read " + exception.GetType().Name;
                return true;
            }

            reason = "";
            return false;
        }

        /// <summary>Increments a rejection-reason counter for the corpus report.</summary>
        /// <param name="reasons">Mutable reason histogram.</param>
        /// <param name="reason">Reason key to increment.</param>
        private static void Add(Dictionary<string, int> reasons, string reason)
        {
            reasons.TryGetValue(reason, out var count);
            reasons[reason] = count + 1;
        }

        /// <summary>Returns an order statistic from a sorted sample (0 = minimum, 1 = maximum).</summary>
        /// <param name="sorted">Ascending sorted values.</param>
        /// <param name="fraction">Quantile in [0, 1].</param>
        /// <returns>Selected element or zero when the sample is empty.</returns>
        private static long At(long[] sorted, double fraction)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            if (fraction >= 1)
            {
                return sorted[^1];
            }

            var index = (int)Math.Ceiling(sorted.Length * fraction) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return sorted[index];
        }

        /// <summary>Converts stopwatch ticks to milliseconds for report lines.</summary>
        /// <param name="ticks">Elapsed ticks.</param>
        /// <returns>Milliseconds at the current stopwatch frequency.</returns>
        private static double Ms(long ticks)
            => ticks * 1000d / Stopwatch.Frequency;

        /// <summary>Converts stopwatch ticks to seconds for aggregate stage totals.</summary>
        /// <param name="ticks">Elapsed ticks.</param>
        /// <returns>Seconds at the current stopwatch frequency.</returns>
        private static double TicksToSeconds(long ticks)
            => ticks / (double)Stopwatch.Frequency;

        /// <summary>Per-file classification bucket used by the corpus bench and <see cref="ClassifyArticle"/>.</summary>
        internal enum CorpusOutcome
        {
            /// <summary>Parse and materialize succeeded.</summary>
            Valid,

            /// <summary>Article bytes were read but rejected by parse or materialize.</summary>
            Invalid,

            /// <summary>File could not be read or is empty; not treated as a malformed article.</summary>
            Unsupported,
        }
    }
}
