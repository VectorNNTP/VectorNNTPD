using System.Text;
using Serilog.Debugging;

namespace VectorNNTPD.Common.Tests.Logging
{
    /// <summary>
    /// Captures Serilog <see cref="SelfLog"/> text for one action.
    /// </summary>
    internal static class SelfLogCapture
    {
        /// <summary>Serializes enable and disable so concurrent tests do not interleave capture.</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Runs <paramref name="action"/> while this process holds the SelfLog gate.</summary>
        /// <param name="action">Async code that enables and disables SelfLog.</param>
        /// <returns>A task that completes when <paramref name="action"/> finishes and the gate is released.</returns>
        /// <remarks>
        /// Serilog allows one SelfLog callback. Callers that enable SelfLog themselves use this method so a
        /// concurrent <see cref="Capture"/> cannot replace that callback.
        /// </remarks>
        internal static async Task RunAsync(Func<Task> action)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
        /// <summary>Runs <paramref name="action"/> with SelfLog enabled and returns the captured text.</summary>
        /// <param name="action">Code whose SelfLog output the test wants.</param>
        /// <returns>Text SelfLog emitted during <paramref name="action"/>.</returns>
        internal static string Capture(Action action)
        {
            Gate.Wait();
            try
            {
                var buffer = new StringBuilder();
                SelfLog.Enable(message =>
                {
                    lock (buffer)
                    {
                        buffer.AppendLine(message);
                    }
                });
                try
                {
                    action();
                    return buffer.ToString();
                }
                finally
                {
                    SelfLog.Disable();
                }
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
