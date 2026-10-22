using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.StorageService.Hosting;
using VectorNNTPD.StorageService.Logging;

namespace VectorNNTPD.StorageService.Tests.Hosting
{
    /// <summary>
    /// Regresses StorageServerStartupSmoke gating, isolated configuration, and Native AOT Serilog analysis settings.
    /// </summary>
    public sealed class StorageServerStartupSmokeTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("true")]
        [InlineData("0")]
        public void Smoke_is_requested_only_for_the_exact_enabled_value(string? value)
        {
            Assert.False(StorageServerStartupSmoke.IsRequested(value));
        }

        [Fact]
        public void Smoke_is_requested_for_the_enabled_value()
        {
            Assert.True(StorageServerStartupSmoke.IsRequested(StorageServerStartupSmoke.EnabledValue));
        }

        [Fact]
        public void Isolated_configuration_binds_and_writes_the_checkpoint()
        {
            var fixture = FindFixture();
            var builder = Host.CreateApplicationBuilder([]);
            Environment.SetEnvironmentVariable(StorageServerStartupSmoke.ConfigurationPathVariable, fixture);
            try
            {
                StorageServerStartupSmoke.UseIsolatedConfiguration(builder.Configuration);
            }
            finally
            {
                Environment.SetEnvironmentVariable(StorageServerStartupSmoke.ConfigurationPathVariable, null);
            }

            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();
            using var host = builder.Build();
            using var output = new StringWriter();

            var code = StorageServerStartupSmoke.Complete(host, output);

            Assert.Equal(0, code);
            Assert.Contains(
                "StorageService startup smoke: configuration serverId=1 fqdn=cache01.usenet.ninja",
                output.ToString(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void Missing_smoke_configuration_file_fails_before_host_construction()
        {
            var builder = Host.CreateApplicationBuilder([]);
            var missing = Path.Join(Path.GetTempPath(), "ss-missing-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            Environment.SetEnvironmentVariable(StorageServerStartupSmoke.ConfigurationPathVariable, missing);
            try
            {
                var error = Assert.Throws<FileNotFoundException>(
                    () => StorageServerStartupSmoke.UseIsolatedConfiguration(builder.Configuration));
                Assert.Equal(missing, error.FileName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(StorageServerStartupSmoke.ConfigurationPathVariable, null);
            }
        }

        /// <summary>
        /// win-x64 Release publish enables AOT and scopes Serilog.Settings.Configuration IL suppressions to StorageService only.
        /// </summary>
        [Fact]
        public void Native_aot_publish_scopes_serilog_settings_il_suppressions()
        {
            var project = FindProjectFile();
            var publishAot = Evaluate(project, "PublishAot");
            var selfContained = Evaluate(project, "SelfContained");
            Assert.True(string.IsNullOrEmpty(publishAot));
            Assert.Equal("false", selfContained, StringComparer.OrdinalIgnoreCase);

            var aotPublish = Evaluate(project, "PublishAot", "win-x64");
            var aotSelfContained = Evaluate(project, "SelfContained", "win-x64");
            var noWarn = Evaluate(project, "NoWarn", "win-x64");
            var linkErrors = Evaluate(project, "ILLinkTreatWarningsAsErrors", "win-x64");
            var ilcErrors = Evaluate(project, "IlcTreatWarningsAsErrors", "win-x64");
            Assert.Equal("true", aotPublish, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("true", aotSelfContained, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("IL3000", noWarn, StringComparison.Ordinal);
            Assert.Contains("IL3002", noWarn, StringComparison.Ordinal);
            Assert.Contains("IL2104", noWarn, StringComparison.Ordinal);
            Assert.Contains("IL3050", noWarn, StringComparison.Ordinal);
            Assert.Equal("false", linkErrors, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("false", ilcErrors, StringComparer.OrdinalIgnoreCase);

            var rejected = Run(
                "dotnet",
                [
                    "msbuild",
                    project,
                    "-nologo",
                    "-t:RejectUnsupportedProductionRuntimeIdentifier",
                    "-p:Platform=x64",
                    "-p:Configuration=Release",
                    "-p:RuntimeIdentifier=osx-arm64",
                ]);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("win-x64 and linux-x64", rejected.Output, StringComparison.Ordinal);
        }

        private static string Evaluate(string project, string property, string? runtimeIdentifier = null)
        {
            var arguments = new List<string>
            {
                "msbuild",
                project,
                "-nologo",
                "-getProperty:" + property,
                "-p:Platform=x64",
                "-p:Configuration=Release",
            };
            if (!string.IsNullOrEmpty(runtimeIdentifier))
            {
                arguments.Add("-p:RuntimeIdentifier=" + runtimeIdentifier);
            }

            var result = Run("dotnet", arguments);
            Assert.True(result.ExitCode == 0, result.Output);
            return result.Output.Trim();
        }

        private static (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.WorkingDirectory = FindRepositoryRoot();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)));
            return (process.ExitCode, stdout + stderr);
        }

        private static string FindFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Join(
                    directory.FullName,
                    "tests",
                    "VectorNNTPD.StorageService.Tests",
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

        private static string FindProjectFile()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Join(directory.FullName, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("VectorNNTPD.StorageService.csproj was not found.");
        }

        private static string FindRepositoryRoot() =>
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(FindProjectFile())))!;
    }
}
