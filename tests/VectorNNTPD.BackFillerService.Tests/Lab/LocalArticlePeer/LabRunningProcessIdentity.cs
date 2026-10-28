using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Captures identity of an already-running process without starting services.
    /// Distinguishes test assemblies from published binaries from live process modules.
    /// </summary>
    internal static class LabRunningProcessIdentity
    {
        internal sealed class ProcessSnapshot
        {
            public required int ProcessId { get; init; }

            public required string ProcessName { get; init; }

            public required string? MainModulePath { get; init; }

            public required string? MainModuleSha256 { get; init; }

            public required DateTime StartTimeUtc { get; init; }

            public required string RuntimeDescription { get; init; }

            public required IReadOnlyList<ModuleHash> Modules { get; init; }

            public required string Note { get; init; }
        }

        internal sealed class ModuleHash
        {
            public required string Path { get; init; }

            public required string Sha256 { get; init; }
        }

        /// <summary>
        /// Attempts to capture identity for <paramref name="processId"/>. Returns null when the
        /// process is gone or inaccessible. Does not start processes.
        /// </summary>
        internal static ProcessSnapshot? TryCapture(int processId, params string[] moduleNameContains)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                string? mainPath = null;
                string? mainSha = null;
                try
                {
                    mainPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(mainPath) && File.Exists(mainPath))
                    {
                        mainSha = Sha256File(mainPath);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // Main module may be inaccessible for some hosts; continue with partial evidence.
                }

                var modules = new List<ModuleHash>();
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        var path = module.FileName;
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        {
                            continue;
                        }

                        if (moduleNameContains.Length > 0
                            && !moduleNameContains.Any(fragment =>
                                path.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        modules.Add(new ModuleHash { Path = path, Sha256 = Sha256File(path) });
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // Module enumeration can fail; partial snapshot still useful.
                }

                return new ProcessSnapshot
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    MainModulePath = mainPath,
                    MainModuleSha256 = mainSha,
                    StartTimeUtc = process.StartTime.ToUniversalTime(),
                    RuntimeDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Modules = modules,
                    Note =
                        "Captured from a live process. This is not a test-assembly hash and is not proof "
                        + "of effective MySQL account-pool state (record UpstreamProviderSnapshot separately).",
                };
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        internal static async Task WriteAsync(string path, ProcessSnapshot snapshot, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }

        private static string Sha256File(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
