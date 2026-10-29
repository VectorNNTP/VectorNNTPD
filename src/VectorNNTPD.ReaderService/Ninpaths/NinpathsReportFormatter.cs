using System.Globalization;
using System.Text;

namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>
    /// Formats compact INN 3.1.x <c>!!NINP</c> dump output from aggregated statistics.
    /// </summary>
    /// <remarks>
    /// Does not reread the Path-survey file. Site numbers are assigned in INN
    /// hash-table walk order. Returns an empty buffer when <see cref="NinpathsStatistics.TotalArticles"/>
    /// is 0 (INN <c>writedump</c> returns -1).
    /// </remarks>
    public static class NinpathsReportFormatter
    {
        /// <summary>
        /// Writes the compact dump for <paramref name="stats"/>. Empty when there is no traffic.
        /// </summary>
        public static byte[] Format(
            NinpathsStatistics stats,
            long startUnix,
            long endUnix,
            long averageUnix)
        {
            ArgumentNullException.ThrowIfNull(stats);
            if (stats.TotalArticles == 0)
            {
                return [];
            }

            using var buffer = new MemoryStream(4096);
            using (var writer = new StreamWriter(buffer, Encoding.Latin1, 1024, leaveOpen: true))
            {
                writer.NewLine = "\n";
                writer.Write("!!NINP ");
                writer.Write(NinpathsConstants.Version);
                writer.Write(' ');
                writer.Write(startUnix.ToString(CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(endUnix.ToString(CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(stats.SiteCount.ToString(CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(stats.TotalArticles.ToString(CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(averageUnix.ToString(CultureInfo.InvariantCulture));
                writer.Write('\n');

                long numbered = 0;
                var lineLength = 0;
                var buckets = stats.Buckets;
                for (var i = 0; i < buckets.Length; i++)
                {
                    for (var site = buckets[i]; site is not null; site = site.BucketNext)
                    {
                        site.Number = numbered++;
                        var written = WriteSiteRecord(writer, site);
                        lineLength += written;
                        if (lineLength > NinpathsConstants.RecordLineSoftLimit)
                        {
                            writer.Write('\n');
                            lineLength = 0;
                        }
                        else
                        {
                            writer.Write(' ');
                        }
                    }
                }

                writer.Write('\n');
                writer.Write("!!NLREC\n");

                long relations = 0;
                lineLength = 0;
                for (var i = 0; i < buckets.Length; i++)
                {
                    for (var site = buckets[i]; site is not null; site = site.BucketNext)
                    {
                        if (site.Relations is null)
                        {
                            continue;
                        }

                        lineLength += WriteColonNumber(writer, site.Number);
                        for (var rel = site.Relations; rel is not null; rel = rel.Next)
                        {
                            lineLength += WriteBangNumber(writer, rel.Target.Number);
                            if (rel.Tally > 1)
                            {
                                lineLength += WriteCommaNumber(writer, rel.Tally);
                            }

                            relations++;
                        }

                        if (lineLength > NinpathsConstants.RecordLineSoftLimit)
                        {
                            writer.Write('\n');
                            lineLength = 0;
                        }
                    }
                }

                writer.Write('\n');
                writer.Write("!!NLEND ");
                writer.Write(relations.ToString(CultureInfo.InvariantCulture));
                writer.Write('\n');
            }

            return buffer.ToArray();
        }

        private static int WriteSiteRecord(StreamWriter writer, NinpathsSite site)
        {
            var id = Encoding.Latin1.GetString(site.Id);
            var count = site.SentTo.ToString(CultureInfo.InvariantCulture);
            writer.Write(id);
            writer.Write(' ');
            writer.Write(count);
            return id.Length + 1 + count.Length;
        }

        private static int WriteColonNumber(StreamWriter writer, long value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            writer.Write(':');
            writer.Write(text);
            return 1 + text.Length;
        }

        private static int WriteBangNumber(StreamWriter writer, long value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            writer.Write('!');
            writer.Write(text);
            return 1 + text.Length;
        }

        private static int WriteCommaNumber(StreamWriter writer, long value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            writer.Write(',');
            writer.Write(text);
            return 1 + text.Length;
        }
    }
}
