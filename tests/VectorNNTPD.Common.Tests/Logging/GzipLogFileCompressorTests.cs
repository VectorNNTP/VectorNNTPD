using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.Common.Tests.Logging
{
    /// <summary>
    /// Locks gzip replacement, source deletion, and the self-log written when a completed gzip cannot replace its source.
    /// </summary>
    public sealed class GzipLogFileCompressorTests
    {
        /// <summary>A successful compression writes <c>.gz</c> and deletes the source without a retention diagnostic.</summary>
        [Fact]
        public void Successful_compression_deletes_the_source()
        {
            var directory = Directory.CreateTempSubdirectory("common-gzip-ok-").FullName;
            try
            {
                var source = Path.Join(directory, "app-20261001.log");
                File.WriteAllText(source, "plain-day");
                var diagnostic = SelfLogCapture.Capture(() =>
                    Assert.True(new GzipLogFileCompressor().TryCompressAndReplace(source)));

                Assert.False(File.Exists(source));
                Assert.True(File.Exists(source + ".gz"));
                Assert.DoesNotContain("Compressed log was kept", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// When the source cannot be deleted after the gzip is in place, the gzip stays and SelfLog records that the source was kept.
        /// Platforms that allow deleting a read-only file take the successful-deletion path instead.
        /// </summary>
        [Fact]
        public void Read_only_source_keeps_gzip_and_reports_a_failed_delete_when_the_platform_refuses()
        {
            var directory = Directory.CreateTempSubdirectory("common-gzip-ro-").FullName;
            var source = Path.Join(directory, "app-20261001.log");
            try
            {
                File.WriteAllText(source, "kept-source");
                File.SetAttributes(source, FileAttributes.ReadOnly);
                var diagnostic = SelfLogCapture.Capture(() =>
                    Assert.True(new GzipLogFileCompressor().TryCompressAndReplace(source)));

                Assert.True(File.Exists(source + ".gz"));
                if (File.Exists(source))
                {
                    Assert.Contains(
                        "Compressed log was kept and the source log could not be deleted",
                        diagnostic,
                        StringComparison.Ordinal);
                }
                else
                {
                    Assert.DoesNotContain("Compressed log was kept", diagnostic, StringComparison.Ordinal);
                }
            }
            finally
            {
                if (File.Exists(source))
                {
                    File.SetAttributes(source, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// A read-only <c>.gz.partial</c> that cannot be removed is reported on SelfLog and compression returns false.
        /// Platforms that can delete that partial continue and compress the source.
        /// </summary>
        [Fact]
        public void Read_only_partial_is_reported_when_it_cannot_be_deleted()
        {
            var directory = Directory.CreateTempSubdirectory("common-gzip-partial-").FullName;
            var source = Path.Join(directory, "app-20261001.log");
            var partial = source + GzipLogFileCompressor.PartialSuffix;
            try
            {
                File.WriteAllText(source, "waiting");
                File.WriteAllBytes(partial, [1, 2, 3, 4]);
                File.SetAttributes(partial, FileAttributes.ReadOnly);
                var compressed = false;
                var diagnostic = SelfLogCapture.Capture(() =>
                    compressed = new GzipLogFileCompressor().TryCompressAndReplace(source));

                if (File.Exists(partial))
                {
                    Assert.False(compressed);
                    Assert.Contains("Temporary log file could not be deleted", diagnostic, StringComparison.Ordinal);
                    Assert.True(File.Exists(source));
                }
                else
                {
                    Assert.True(compressed);
                    Assert.True(File.Exists(source + ".gz"));
                }
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.SetAttributes(partial, FileAttributes.Normal);
                }

                if (File.Exists(source))
                {
                    File.SetAttributes(source, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
