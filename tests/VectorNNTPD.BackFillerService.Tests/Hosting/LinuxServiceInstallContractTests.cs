using VectorNNTPD.BackFillerService.Tests.Fixtures;

namespace VectorNNTPD.BackFillerService.Tests.Hosting
{
    /// <summary>
    /// Checks the documented install procedure against the shipped unit.
    /// This does not start systemd.
    /// </summary>
    public sealed class LinuxServiceInstallContractTests
    {
        /// <summary>README install steps and the shipped unit file agree on account, paths, permissions, and systemd enablement without world-writable modes.</summary>
        [Fact]
        public void Readme_creates_the_service_account_and_limits_write_access_to_logs_and_certs()
        {
            var readme = File.ReadAllText(RepositoryFiles.Under("README.md"));
            var unit = File.ReadAllText(RepositoryFiles.Under("deploy", "systemd", "vectornntp-backfiller.service"));

            Assert.Contains("groupadd --system vectornntp", readme, StringComparison.Ordinal);
            Assert.Contains("useradd --system --gid vectornntp", readme, StringComparison.Ordinal);
            Assert.Contains("/opt/vectornntp/backfiller/logs", readme, StringComparison.Ordinal);
            Assert.Contains("/opt/vectornntp/backfiller/certs", readme, StringComparison.Ordinal);
            Assert.Contains("-o vectornntp -g vectornntp -m 0750", readme, StringComparison.Ordinal);
            Assert.Contains("-o root -g root -m 0755 VectorNNTPD.BackFillerService", readme, StringComparison.Ordinal);
            Assert.Contains("-m 0644 VectorNNTPD.BackFillerService.json", readme, StringComparison.Ordinal);
            Assert.Contains("/etc/systemd/system/vectornntp-backfiller.service", readme, StringComparison.Ordinal);
            Assert.Contains("systemctl daemon-reload", readme, StringComparison.Ordinal);
            Assert.Contains("systemctl enable --now", readme, StringComparison.Ordinal);
            Assert.Contains("do not use mode `0777`", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod 0777", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod -R", readme, StringComparison.Ordinal);
            Assert.Contains("does not change Unix ownership", readme, StringComparison.Ordinal);

            Assert.Contains("User=vectornntp", unit, StringComparison.Ordinal);
            Assert.Contains("Group=vectornntp", unit, StringComparison.Ordinal);
            Assert.Contains("WorkingDirectory=/opt/vectornntp/backfiller", unit, StringComparison.Ordinal);
            Assert.Contains("ExecStart=/opt/vectornntp/backfiller/VectorNNTPD.BackFillerService", unit, StringComparison.Ordinal);
            Assert.Contains(
                "ReadWritePaths=/opt/vectornntp/backfiller/logs /opt/vectornntp/backfiller/certs",
                unit,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ReadWritePaths=/opt/vectornntp/backfiller\n", unit, StringComparison.Ordinal);
        }

        /// <summary>
        /// The unit loads the optional environment file, the docs name that path, and WatchdogSec stays commented out.
        /// </summary>
        [Fact]
        public void Unit_loads_the_optional_environment_file_and_keeps_watchdog_disabled()
        {
            var unit = File.ReadAllText(RepositoryFiles.Under("deploy", "systemd", "vectornntp-backfiller.service"));
            var readme = File.ReadAllText(RepositoryFiles.Under("README.md"));
            var configuration = File.ReadAllText(RepositoryFiles.Under("docs", "configuration.md"));
            var active = ActiveLines(unit);

            Assert.Equal(
                ["EnvironmentFile=-/etc/vectornntp/backfiller.env"],
                active.Where(line => line.StartsWith("EnvironmentFile=", StringComparison.Ordinal)));
            Assert.Contains("/etc/vectornntp/backfiller.env", readme, StringComparison.Ordinal);
            Assert.Contains("EnvironmentFile=-/etc/vectornntp/backfiller.env", configuration, StringComparison.Ordinal);
            Assert.Contains("The leading `-` suppresses errors reading the environment file.", readme, StringComparison.Ordinal);
            Assert.Contains("The leading `-` suppresses errors reading the environment file.", configuration, StringComparison.Ordinal);
            Assert.Contains("The leading '-' suppresses errors", unit, StringComparison.Ordinal);
            Assert.Contains("An unquoted backslash is an escape.", unit, StringComparison.Ordinal);
            Assert.Contains("a backslash escapes the next character", readme, StringComparison.Ordinal);
            Assert.Contains("a backslash escapes the next character", configuration, StringComparison.Ordinal);
            Assert.DoesNotContain(active, line => line.StartsWith("WatchdogSec=", StringComparison.Ordinal));
            Assert.DoesNotContain(active, line => line.StartsWith("Restart=on-watchdog", StringComparison.Ordinal));
            Assert.Contains("Type=notify", active);
            Assert.Contains("NotifyAccess=main", active);
            Assert.Contains("User=vectornntp", active);
            Assert.Contains("Group=vectornntp", active);
            Assert.Contains("WorkingDirectory=/opt/vectornntp/backfiller", active);
            Assert.Contains("ExecStart=/opt/vectornntp/backfiller/VectorNNTPD.BackFillerService", active);
            Assert.Contains("TimeoutStartSec=180", active);
            Assert.Contains("TimeoutStopSec=60", active);
            Assert.Contains("Restart=on-failure", active);
            Assert.Contains("RestartSec=5", active);
            Assert.Contains("KillSignal=SIGTERM", active);
            Assert.Contains("SendSIGKILL=yes", active);
            Assert.Contains("NoNewPrivileges=true", active);
            Assert.Contains("PrivateTmp=true", active);
            Assert.Contains("ProtectSystem=strict", active);
            Assert.Contains("ProtectHome=true", active);
            Assert.Contains(
                "ReadWritePaths=/opt/vectornntp/backfiller/logs /opt/vectornntp/backfiller/certs",
                active);
            Assert.Contains("Environment=DOTNET_ENVIRONMENT=Production", active);
            Assert.DoesNotContain("REPLACE_WITH_", unit, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", unit, StringComparison.Ordinal);
        }

        /// <summary>
        /// The example and the install docs name the one required variable and keep the shared secret names unset.
        /// </summary>
        [Fact]
        public void Environment_example_requires_only_the_nntpdb_connection_string_placeholder()
        {
            const string connectionString = "ConnectionStrings__NntpDB=Server=REPLACE_WITH_MYSQL_HOST;User ID=REPLACE_WITH_MYSQL_USER;Password=REPLACE_WITH_MYSQL_PASSWORD;Database=REPLACE_WITH_DATABASE_NAME";
            var example = File.ReadAllText(RepositoryFiles.Under("deploy", "systemd", "backfiller.env.example"));
            var readme = File.ReadAllText(RepositoryFiles.Under("README.md"));
            var configuration = File.ReadAllText(RepositoryFiles.Under("docs", "configuration.md"));
            var assignments = ActiveLines(example);

            Assert.Equal([connectionString], assignments);
            Assert.Contains(connectionString, readme, StringComparison.Ordinal);
            Assert.Contains("mode `0600`", readme, StringComparison.Ordinal);
            Assert.Contains("sudoedit", readme, StringComparison.Ordinal);
            Assert.Contains("systemd-analyze verify vectornntp-backfiller.service", readme, StringComparison.Ordinal);
            Assert.Contains("journalctl -u vectornntp-backfiller.service", readme, StringComparison.Ordinal);
            Assert.Contains("ConnectionStrings:NntpDB must be configured.", readme, StringComparison.Ordinal);
            Assert.Contains("Do not run `systemctl show -p Environment`", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("$(", example, StringComparison.Ordinal);
            Assert.DoesNotContain("`", example, StringComparison.Ordinal);

            foreach (var name in SharedSecretNames)
            {
                Assert.Contains(name, readme, StringComparison.Ordinal);
                Assert.Contains(name, configuration, StringComparison.Ordinal);
                Assert.Contains("# " + name, example, StringComparison.Ordinal);
                Assert.DoesNotContain(assignments, line => line.StartsWith(name + "=", StringComparison.Ordinal));
            }

            Assert.Contains("These names are not required in `/etc/vectornntp/backfiller.env`:", readme, StringComparison.Ordinal);
            Assert.Contains("These names are not required in `/etc/vectornntp/backfiller.env`:", configuration, StringComparison.Ordinal);
            Assert.Contains("passes MySqlConnector's parser", readme, StringComparison.Ordinal);

            var editAt = readme.IndexOf("sudoedit /etc/vectornntp/backfiller.env", StringComparison.Ordinal);
            var enableAt = readme.IndexOf("systemctl enable --now", StringComparison.Ordinal);
            Assert.True(editAt >= 0 && editAt < enableAt);
            var startupBlocks = FencedBlocks(readme)
                .Where(block => block.Contains("systemctl enable --now", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(startupBlocks);
            Assert.All(
                startupBlocks,
                block =>
                {
                    Assert.DoesNotContain("backfiller.env.example", block, StringComparison.Ordinal);
                    Assert.DoesNotContain("REPLACE_WITH_", block, StringComparison.Ordinal);
                });
        }

        /// <summary>Windows release checks reject the Linux environment-file example.</summary>
        [Fact]
        public void Windows_release_validation_rejects_the_linux_environment_example()
        {
            var script = File.ReadAllText(RepositoryFiles.Under("tools", "Publish-Release.ps1"));
            var workflow = File.ReadAllText(RepositoryFiles.Under(".github", "workflows", "build.yml"));

            Assert.Contains("Missing environment-file example in the linux-x64 release", script, StringComparison.Ordinal);
            Assert.Contains("Environment-file example must not be packaged for ${Rid}", script, StringComparison.Ordinal);
            Assert.Contains(
                "'vectornntp-backfiller.service', 'backfiller.env.example'",
                workflow,
                StringComparison.Ordinal);
        }

        /// <summary>Shared VectorNNTP names that BackFiller loads from nntpsharedconfig rather than the environment file.</summary>
        private static readonly string[] SharedSecretNames =
        [
            "VECTOR__RABBITMQ__USERNAME",
            "VECTOR__RABBITMQ__PASSWORD",
            "VECTOR__CLOUDFLAREAPIKEY",
            "VECTOR__CLOUDFLAREZONEID",
            "VECTOR__ACMEACCOUNT",
            "VECTOR__ACMECERTIFICATEPASSWORD",
        ];

        /// <summary>Unit and environment-file lines that systemd treats as settings.</summary>
        /// <param name="text">File text.</param>
        /// <returns>Non-empty lines whose first non-space character is not a comment marker.</returns>
        private static List<string> ActiveLines(string text) =>
            text.Split('\n')
                .Select(static raw => raw.TrimEnd('\r').Trim())
                .Where(static line => line.Length > 0 && line[0] is not '#' and not ';')
                .ToList();

        /// <summary>Markdown fenced blocks, without the fence markers.</summary>
        /// <param name="text">Markdown document.</param>
        /// <returns>The text inside each complete fence.</returns>
        private static List<string> FencedBlocks(string text)
        {
            var blocks = new List<string>();
            var current = new System.Text.StringBuilder();
            var inside = false;
            foreach (var line in text.Split('\n').Select(static raw => raw.TrimEnd('\r')))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (inside)
                    {
                        blocks.Add(current.ToString());
                        current.Clear();
                        inside = false;
                    }
                    else
                    {
                        inside = true;
                    }

                    continue;
                }

                if (inside)
                {
                    current.AppendLine(line);
                }
            }

            return blocks;
        }
    }
}
