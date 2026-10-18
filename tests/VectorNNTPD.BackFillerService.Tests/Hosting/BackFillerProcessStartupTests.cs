using System.Diagnostics;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>Spawns the real BackFiller executable to regress validation failures, startup smoke, configuration base path, and secret redaction in output.</summary>
    public sealed class BackFillerProcessStartupTests
    {
        /// <summary>Invalid BindPortTls exits with code 1, names the option in output, and does not leak configured secrets.</summary>
        [Fact]
        public async Task Published_or_built_process_fails_fast_with_a_useful_validation_error()
        {
            var (fileName, arguments) = ResolveApplicationStart();
            var workingDirectory = Directory.CreateTempSubdirectory("bf-process-cwd-").FullName;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.StartInfo.ArgumentList.Add("--BackFiller:BindPortTls=0");
                process.StartInfo.Environment[NntpDbOptions.ConnectionStringEnvironmentVariable] =
                    "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz";
                process.StartInfo.Environment["VECTOR__CLOUDFLAREAPIKEY"] = "unit-test-cloudflare-key-not-secret";
                process.StartInfo.Environment["VECTOR__CLOUDFLAREZONEID"] = "0123456789abcdef0123456789abcdef";

                var applicationPath = arguments.Length > 0 ? arguments[0] : fileName;
                var logBefore = SnapshotLogs(applicationPath);
                Assert.True(process.Start());
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    Assert.Fail("BackFiller process did not exit within 20s after invalid BindPortTls.");
                }

                var output = string.Concat(await stdout, await stderr, ReadNewLogs(applicationPath, logBefore));
                Assert.Equal(1, process.ExitCode);
                Assert.Contains("BindPortTls", output, StringComparison.Ordinal);
                Assert.DoesNotContain("super-secret", output, StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        /// <summary>Startup smoke environment exits 0 and prints bound server id and FQDN without running full hosted services.</summary>
        [Fact]
        public async Task Startup_smoke_binds_configuration_and_exits_before_hosted_services()
        {
            var (fileName, arguments) = ResolveApplicationStart();
            var workingDirectory = Directory.CreateTempSubdirectory("bf-startup-smoke-").FullName;
            var fixture = FindStartupSmokeFixture();
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.StartInfo.Environment[BackFillerStartupSmoke.EnvironmentVariable] =
                    BackFillerStartupSmoke.EnabledValue;
                process.StartInfo.Environment[BackFillerStartupSmoke.ConfigurationPathVariable] = fixture;

                Assert.True(process.Start());
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    Assert.Fail("Startup smoke did not exit within 30s.");
                }

                var output = string.Concat(await stdout, await stderr);
                Assert.Equal(0, process.ExitCode);
                Assert.Contains(
                    "BackFiller startup smoke: configuration serverId=1 fqdn=backfiller01.usenet.ninja",
                    output,
                    StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        /// <summary>With a foreign working directory and blank env overrides, bundled JSON still supplies identity; validation fails on BindPortTls without requiring secrets from env.</summary>
        [Fact]
        public async Task Process_loads_configuration_from_the_application_base_not_the_working_directory()
        {
            var (fileName, arguments) = ResolveApplicationStart();
            var workingDirectory = Directory.CreateTempSubdirectory("bf-process-config-").FullName;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.StartInfo.ArgumentList.Add("--BackFiller:BindPortTls=0");
                process.StartInfo.Environment["VECTOR__SERVERID"] = "";
                process.StartInfo.Environment["VECTOR__CLOUDFLAREAPIKEY"] = "env-key-must-not-be-required";
                process.StartInfo.Environment["VECTOR__ACMECERTIFICATEPASSWORD"] = "env-pfx-must-not-be-required";
                process.StartInfo.Environment["VECTOR__ACMEACCOUNT"] = "env-account-must-not-be-required@example.test";
                process.StartInfo.Environment["VECTOR__RABBITMQ__USERNAME"] = "env-user-must-not-be-required";
                process.StartInfo.Environment["VECTOR__RABBITMQ__PASSWORD"] = "env-pass-must-not-be-required";

                var applicationPath = arguments.Length > 0 ? arguments[0] : fileName;
                var logBefore = SnapshotLogs(applicationPath);
                Assert.True(process.Start());
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    Assert.Fail("BackFiller process did not exit within 20s while loading configuration from a foreign cwd.");
                }

                var output = string.Concat(await stdout, await stderr, ReadNewLogs(applicationPath, logBefore));
                Assert.Equal(1, process.ExitCode);
                Assert.Contains("BindPortTls", output, StringComparison.Ordinal);
                Assert.DoesNotContain("CloudFlareApiKey", output, StringComparison.Ordinal);
                Assert.DoesNotContain("env-key-must-not-be-required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("env-pfx-must-not-be-required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("env-account-must-not-be-required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("env-user-must-not-be-required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("env-pass-must-not-be-required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("Name is required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("ServerId is required", output, StringComparison.Ordinal);
                Assert.DoesNotContain("Hosts must contain", output, StringComparison.Ordinal);
                Assert.DoesNotContain("super-secret", output, StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        /// <summary>Captures byte lengths of existing log files next to the application for delta reads after process exit.</summary>
        /// <param name="applicationPath">Path to BackFiller exe or dll.</param>
        private static Dictionary<string, long> SnapshotLogs(string applicationPath)
        {
            var snapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var logDir = Path.Join(Path.GetDirectoryName(applicationPath)!, "logs");
            if (!Directory.Exists(logDir))
            {
                return snapshot;
            }

            foreach (var file in Directory.GetFiles(logDir, "*.log"))
            {
                snapshot[file] = new FileInfo(file).Length;
            }

            return snapshot;
        }

        /// <summary>Reads log file bytes appended since <paramref name="before"/> snapshot.</summary>
        private static string ReadNewLogs(string applicationPath, Dictionary<string, long> before)
        {
            var logDir = Path.Join(Path.GetDirectoryName(applicationPath)!, "logs");
            if (!Directory.Exists(logDir))
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            foreach (var file in Directory.GetFiles(logDir, "*.log"))
            {
                before.TryGetValue(file, out var offset);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (offset > stream.Length)
                {
                    offset = 0;
                }

                stream.Position = offset;
                using var reader = new StreamReader(stream);
                text.Append(reader.ReadToEnd());
            }

            return text.ToString();
        }

        /// <summary>Locates committed <c>startup-smoke.json</c> fixture under tests/VectorNNTPD.BackFillerService.Tests/Fixtures.</summary>
        private static string FindStartupSmokeFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Join(
                    directory.FullName,
                    "tests",
                    "VectorNNTPD.BackFillerService.Tests",
                    "Fixtures",
                    "startup-smoke.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("startup-smoke.json was not found.");
        }

        /// <summary>Finds VectorNNTPD.BackFillerService next to VectorNNTPD.BackFillerService.json for process spawn.</summary>
        private static (string FileName, string[] Arguments) ResolveApplicationStart()
        {
            foreach (var directory in EnumerateApplicationDirectories())
            {
                var exe = Path.Join(directory, "VectorNNTPD.BackFillerService.exe");
                var settings = Path.Join(directory, "VectorNNTPD.BackFillerService.json");
                if (File.Exists(exe) && File.Exists(settings))
                {
                    return (exe, []);
                }

                var dll = Path.Join(directory, "VectorNNTPD.BackFillerService.dll");
                if (File.Exists(dll) && File.Exists(settings))
                {
                    return ("dotnet", [dll]);
                }
            }

            Assert.Fail("Could not locate VectorNNTPD.BackFillerService next to VectorNNTPD.BackFillerService.json.");
            return ("", []);
        }

        /// <summary>Yields candidate output directories (test host, Debug/Release bin) for the application binary.</summary>
        private static IEnumerable<string> EnumerateApplicationDirectories()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(BackFillerServiceCollectionExtensions).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                yield return assemblyDirectory;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                yield return RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "bin", "x64", "Release", "net10.0");
                yield return RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "bin", "x64", "Debug", "net10.0");
                directory = directory.Parent;
            }
        }

        /// <summary>Best-effort recursive delete; IO failures must not fail the test.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                /* A directory that is still in use must not fail the test. */
            }
            catch (UnauthorizedAccessException)
            {
                /* A directory that cannot be deleted must not fail the test. */
            }
        }

        /// <summary>Best-effort process tree kill when wait times out.</summary>
        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                /* The process may already have exited. Cleanup must not fail the test. */
            }
        }
    }
}
