using System.IO.Compression;

namespace VectorNNTPD.Common.Logging
{
    /// <summary>
    /// Gzip-compresses a completed log beside itself using a temporary file and an atomic move.
    /// </summary>
    public sealed class GzipLogFileCompressor : ILogFileCompressor
    {
        /// <summary>Suffix of the temporary file that becomes <c>.gz</c> only after the write finishes.</summary>
        public const string PartialSuffix = ".gz.partial";

        /// <inheritdoc />
        public bool TryCompressAndReplace(string sourceLogPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceLogPath);
            if (!File.Exists(sourceLogPath))
            {
                return false;
            }

            var destination = sourceLogPath + ".gz";
            if (File.Exists(destination))
            {
                TryDelete(sourceLogPath);
                return !File.Exists(sourceLogPath);
            }

            var partial = sourceLogPath + PartialSuffix;
            try
            {
                TryDelete(partial);
                using (var input = new FileStream(
                    sourceLogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(
                    partial,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
                    input.CopyTo(gzip);
                }

                File.Move(partial, destination, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partial);
                return false;
            }

            try
            {
                File.Delete(sourceLogPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Debugging.SelfLog.WriteLine(
                    "Compressed log was kept and the source log could not be deleted: {0}",
                    ex);
            }

            return File.Exists(destination);
        }

        /// <summary>
        /// Deletes <paramref name="path"/> when it exists. <see cref="IOException"/> and
        /// <see cref="UnauthorizedAccessException"/> are written to Serilog <c>SelfLog</c> and not thrown.
        /// </summary>
        /// <param name="path">Source log or <c>.gz.partial</c> path.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Debugging.SelfLog.WriteLine("Temporary log file could not be deleted: {0}", ex);
            }
        }
    }
}
