using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Proves Common <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>
    /// and the ACME wrapper <see cref="AcmeCloudflareOptionsValidator.ResolveAcmeStateDir"/>.
    /// </summary>
    [Collection("WorkingDirectory")]
    public sealed class ApplicationLocalPathResolutionTests
    {
        /// <summary>
        /// Asserts that a relative <c>certs/</c> path resolves under the supplied application base and ends with <c>certs</c>.
        /// </summary>
        [Fact]
        public void Relative_path_resolves_beneath_supplied_application_base()
        {
            var baseDir = Directory.CreateTempSubdirectory("app-base-").FullName;
            try
            {
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir);
                Assert.StartsWith(
                    Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith("certs", resolved, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(baseDir);
            }
        }

        /// <summary>
        /// Asserts that a null application base matches resolution against <see cref="AppContext.BaseDirectory"/> and stays under that directory.
        /// </summary>
        [Fact]
        public void Relative_path_with_omitted_base_uses_appcontext_base_directory()
        {
            var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", applicationBaseDirectory: null);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
                resolved);
            Assert.StartsWith(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that an absolute path is returned normalized, without a trailing separator, and does not include the unused application base.
        /// </summary>
        [Fact]
        public void Absolute_path_remains_absolute()
        {
            var absolute = Directory.CreateTempSubdirectory("app-abs-").FullName;
            var unusedBase = Directory.CreateTempSubdirectory("app-unused-").FullName;
            try
            {
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath(absolute, unusedBase);
                Assert.Equal(
                    Path.GetFullPath(absolute).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved);
                Assert.DoesNotContain(
                    Path.GetFullPath(unusedBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(absolute);
                TryDelete(unusedBase);
            }
        }

        /// <summary>
        /// Asserts that a relative path still resolves under the supplied base when the process current directory is a different folder, then restores that directory.
        /// </summary>
        [Fact]
        public void Foreign_cwd_cannot_influence_the_result()
        {
            var previous = Environment.CurrentDirectory;
            var baseDir = Directory.CreateTempSubdirectory("app-base-cwd-").FullName;
            var cwd = Directory.CreateTempSubdirectory("app-cwd-").FullName;
            try
            {
                Environment.CurrentDirectory = cwd;
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("logs/", baseDir);
                Assert.StartsWith(
                    Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(Path.GetFullPath("logs/"), resolved);
                Assert.DoesNotContain(
                    Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(baseDir);
                TryDelete(cwd);
            }
        }

        /// <summary>
        /// Asserts that resolution against <see cref="AppContext.BaseDirectory"/> ignores both a decoy content root and a changed current directory, then restores that directory.
        /// </summary>
        [Fact]
        public void Decoy_ide_content_root_cannot_influence_when_base_is_appcontext()
        {
            var previous = Environment.CurrentDirectory;
            var decoyContentRoot = Directory.CreateTempSubdirectory("app-decoy-content-").FullName;
            var cwd = Directory.CreateTempSubdirectory("app-decoy-cwd-").FullName;
            try
            {
                Environment.CurrentDirectory = cwd;
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory);
                Assert.Equal(
                    ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
                    resolved);
                Assert.DoesNotContain(
                    Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    resolved,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                TryDelete(decoyContentRoot);
                TryDelete(cwd);
            }
        }

        /// <summary>
        /// Asserts that <c>certs/</c>, <c>certs</c>, and a padded <c>certs/</c> resolve to the same path with no trailing separator.
        /// </summary>
        [Fact]
        public void Normalization_is_deterministic_and_strips_trailing_separators()
        {
            var baseDir = Directory.CreateTempSubdirectory("app-norm-").FullName;
            try
            {
                var first = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir);
                var second = ApplicationLocalPath.ResolveApplicationLocalPath("certs", baseDir);
                var third = ApplicationLocalPath.ResolveApplicationLocalPath("  certs/  ", baseDir);
                Assert.Equal(first, second);
                Assert.Equal(first, third);
                Assert.False(first.EndsWith(Path.DirectorySeparatorChar));
                Assert.False(first.EndsWith(Path.AltDirectorySeparatorChar));
            }
            finally
            {
                TryDelete(baseDir);
            }
        }

        /// <summary>
        /// Asserts that relative <c>..</c> segments which leave the base are rejected, while <c>.</c> and <c>..</c> that remain inside are accepted.
        /// A sibling whose name starts with the base name is not treated as a child.
        /// </summary>
        [Fact]
        public void Relative_traversal_is_rejected_and_interior_dot_segments_are_kept()
        {
            var parent = Directory.CreateTempSubdirectory("app-contain-").FullName;
            var app = Path.Join(parent, "app");
            Directory.CreateDirectory(app);
            try
            {
                var sibling = Path.GetFileName(app) + "-extra";
                var escaped = Assert.Throws<ArgumentException>(() =>
                    ApplicationLocalPath.ResolveApplicationLocalPath(
                        ".." + Path.DirectorySeparatorChar + sibling,
                        app));
                Assert.Equal("path", escaped.ParamName);
                Assert.Throws<ArgumentException>(() =>
                    ApplicationLocalPath.ResolveApplicationLocalPath("certs/../../" + sibling, app));
                Assert.Throws<ArgumentException>(() =>
                    AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/../../" + sibling, app));

                var inside = ApplicationLocalPath.ResolveApplicationLocalPath(
                    "nested" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "certs",
                    app);
                Assert.Equal(ApplicationLocalPath.ResolveApplicationLocalPath("certs", app), inside);
                Assert.Equal(
                    ApplicationLocalPath.ResolveApplicationLocalPath(".", app),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)));
            }
            finally
            {
                TryDelete(parent);
            }
        }

        /// <summary>
        /// Asserts that a rooted path may leave the base, a drive root keeps its separator, and a relative <c>..</c> from that root stays on the root.
        /// </summary>
        [Fact]
        public void Rooted_paths_stay_rooted_and_a_path_root_keeps_its_separator()
        {
            var app = Directory.CreateTempSubdirectory("app-rooted-").FullName;
            var outside = Directory.CreateTempSubdirectory("app-outside-").FullName;
            try
            {
                Assert.Equal(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(outside)),
                    ApplicationLocalPath.ResolveApplicationLocalPath(outside, app));

                if (OperatingSystem.IsWindows())
                {
                    var driveRoot = Path.GetPathRoot(app)!;
                    Assert.Equal(driveRoot, ApplicationLocalPath.ResolveApplicationLocalPath(driveRoot, app));
                    Assert.Equal(driveRoot, ApplicationLocalPath.ResolveApplicationLocalPath("..", driveRoot));
                    var unc = @"\\server\share\certs";
                    Assert.Equal(
                        Path.GetFullPath(unc),
                        ApplicationLocalPath.ResolveApplicationLocalPath(unc, app));
                }
            }
            finally
            {
                TryDelete(app);
                TryDelete(outside);
            }
        }

        /// <summary>Asserts that an embedded null is rejected by path resolution.</summary>
        [Fact]
        public void Invalid_path_syntax_is_rejected()
        {
            var app = Directory.CreateTempSubdirectory("app-invalid-").FullName;
            try
            {
                Assert.Throws<ArgumentException>(() =>
                    ApplicationLocalPath.ResolveApplicationLocalPath("certs\0", app));
            }
            finally
            {
                TryDelete(app);
            }
        }

        /// <summary>
        /// Asserts that a null path throws <see cref="ArgumentNullException"/> and a whitespace path throws <see cref="ArgumentException"/>.
        /// </summary>
        [Fact]
        public void Null_or_whitespace_path_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(static () =>
                ApplicationLocalPath.ResolveApplicationLocalPath(null!, AppContext.BaseDirectory));
            Assert.Throws<ArgumentException>(static () =>
                ApplicationLocalPath.ResolveApplicationLocalPath("   ", AppContext.BaseDirectory));
            Assert.Throws<ArgumentException>(static () =>
                ApplicationLocalPath.ResolveApplicationLocalPath(string.Empty, AppContext.BaseDirectory));
        }

        /// <summary>
        /// Asserts that the rooted <c>Path.GetFullPath</c> call lives in <c>ApplicationLocalPath.cs</c> and that the ACME validator source delegates to it without copying that call.
        /// </summary>
        [Fact]
        public void Relative_path_algorithm_exists_only_in_application_local_path()
        {
            var helper = File.ReadAllText(FindRepoFile(
                Path.Combine("src", "VectorNNTPD.Common", "Configuration", "ApplicationLocalPath.cs")));
            var acmeWrapper = File.ReadAllText(FindRepoFile(
                Path.Combine("src", "VectorNNTPD.Common", "Configuration", "AcmeCloudflareOptionsValidator.cs")));
            Assert.Contains("Path.GetFullPath(trimmed, fullRoot)", helper, StringComparison.Ordinal);
            Assert.Contains("A relative path must resolve inside the application base directory.", helper, StringComparison.Ordinal);
            Assert.Contains("ApplicationLocalPath.ResolveApplicationLocalPath", acmeWrapper, StringComparison.Ordinal);
            Assert.DoesNotContain("Path.GetFullPath(trimmed, fullRoot)", acmeWrapper, StringComparison.Ordinal);
        }

        /// <summary>
        /// Asserts that <see cref="AcmeCloudflareOptionsValidator.ResolveAcmeStateDir"/> returns the same path as <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> for the same relative input and base.
        /// </summary>
        [Fact]
        public void Acme_wrapper_delegates_to_the_generic_resolver()
        {
            var baseDir = Directory.CreateTempSubdirectory("app-acme-wrap-").FullName;
            try
            {
                Assert.Equal(
                    ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir),
                    AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", baseDir));
            }
            finally
            {
                TryDelete(baseDir);
            }
        }

        /// <summary>
        /// Deletes <paramref name="path"/> recursively. <see cref="IOException"/> is ignored.
        /// </summary>
        /// <param name="path">Temporary directory to delete.</param>
        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// Walks parent directories from <see cref="AppContext.BaseDirectory"/> until <paramref name="relativePath"/> exists.
        /// </summary>
        /// <param name="relativePath">Repository-relative path to locate.</param>
        /// <returns>The absolute path of the first matching file.</returns>
        /// <exception cref="FileNotFoundException">Thrown when no parent contains <paramref name="relativePath"/>.</exception>
        private static string FindRepoFile(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not locate {relativePath}.");
        }
    }
}
