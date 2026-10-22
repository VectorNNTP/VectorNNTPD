using System.Runtime.CompilerServices;
using VectorNNTPD.Common.Acme;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Locks ACME path construction on <c>Path.Join</c> so rooted later segments cannot
    /// silently discard the state-directory prefix (<c>cs/path-combine</c>).
    /// </summary>
    public sealed class AcmePathJoinTests
    {
        /// <summary>
        /// <c>Path.Join</c> keeps earlier segments when a later segment is rooted.
        /// (<c>Path.Combine</c> would return only the rooted segment; this suite does not call it.)
        /// </summary>
        [Fact]
        public void Path_Join_does_not_drop_earlier_segments_for_rooted_later_argument()
        {
            var baseDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rooted = Path.GetPathRoot(baseDir)!;
            var joined = Path.Join(baseDir, rooted);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            Assert.StartsWith(baseDir, joined, comparison);
            Assert.NotEqual(rooted, joined);
            Assert.Contains(
                rooted.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                joined,
                comparison);
        }

        /// <summary>
        /// <see cref="AcmePaths"/> layout helpers stay under the configured state root for normal FQDN input.
        /// </summary>
        [Fact]
        public void AcmePaths_layout_stays_under_configured_state_root()
        {
            var stateDir = Path.Join(Path.GetTempPath(), "vectornntp-acme-join-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stateDir);
                var fqdn = "backfiller01.usenet.ninja";
                Assert.True(IsUnder(stateDir, AcmePaths.AccountKeyPath(stateDir)));
                Assert.True(IsUnder(stateDir, AcmePaths.TransactionJournalPath(stateDir, fqdn)));
                Assert.True(IsUnder(stateDir, AcmePaths.LiveDir(stateDir, fqdn)));
                Assert.True(IsUnder(stateDir, AcmePaths.Dns01RecoveryDir(stateDir, fqdn)));
                Assert.True(IsUnder(stateDir, AcmePaths.IssuanceLockPath(stateDir, fqdn)));
                Assert.True(IsUnder(stateDir, AcmePaths.GenerationCertificatePaths(stateDir, fqdn, "gen1").PfxPath));
            }
            finally
            {
                if (Directory.Exists(stateDir))
                {
                    Directory.Delete(stateDir, recursive: true);
                }
            }
        }

        /// <summary>
        /// Production ACME path sources must not call <c>Path.Combine</c>.
        /// </summary>
        [Fact]
        public void Acme_path_sources_do_not_call_Path_Combine()
        {
            var repoRoot = FindRepoRoot();
            var files = new[]
            {
                Path.Join(repoRoot, "src", "VectorNNTPD.Common", "Acme", "AcmePaths.cs"),
                Path.Join(repoRoot, "src", "VectorNNTPD.Common", "Acme", "AtomicFile.cs"),
                Path.Join(repoRoot, "src", "VectorNNTPD.Common", "Acme", "Dns01Solver.cs"),
            };

            foreach (var file in files)
            {
                Assert.True(File.Exists(file), file);
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("Path.Combine(", text, StringComparison.Ordinal);
                Assert.Contains("Path.Join(", text, StringComparison.Ordinal);
            }
        }

        private static bool IsUnder(string root, string candidate)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.StartsWith(
                fullRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static string FindRepoRoot([CallerFilePath] string? sourceFile = null)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
            while (directory is not null)
            {
                if (File.Exists(Path.Join(directory.FullName, "VectorNNTPD.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate VectorNNTPD.slnx from the test source path.");
        }
    }
}
