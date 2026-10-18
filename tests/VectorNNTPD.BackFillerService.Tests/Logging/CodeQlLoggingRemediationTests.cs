using VectorNNTPD.BackFillerService.Logging;

namespace VectorNNTPD.BackFillerService.Tests.Logging
{
    /// <summary>
    /// BackFiller console-output restoration. Shared gzip and flush contracts live in Common tests.
    /// </summary>
    public sealed class CodeQlLoggingRemediationTests
    {
        /// <summary>Serializes console replacement tests that mutate Console.Out.</summary>
        private static readonly object ConsoleGate = new();

        /// <summary>UseAutoFlushConsoleOutput swaps writers and RestoreConsoleOutput restores original stdout.</summary>
        [Fact]
        public void Console_writer_replaces_and_restores_without_closing_stdout()
        {
            lock (ConsoleGate)
            {
                var before = Console.Out;
                try
                {
                    BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
                    var installed = Console.Out;
                    Assert.NotSame(before, installed);
                    BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
                    Assert.Same(installed, Console.Out);
                    installed.Write("bf-console");
                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    Assert.Same(before, Console.Out);
                    before.Flush();
                    Console.Out.Flush();
                    using (var stdout = Console.OpenStandardOutput())
                    {
                        stdout.Write([]);
                        stdout.Flush();
                    }

                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    Assert.Same(before, Console.Out);
                }
                finally
                {
                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    if (!ReferenceEquals(Console.Out, before))
                    {
                        Console.SetOut(before);
                    }
                }
            }
        }
    }
}
