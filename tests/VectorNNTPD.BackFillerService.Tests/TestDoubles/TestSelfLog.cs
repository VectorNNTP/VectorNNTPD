using Serilog.Debugging;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Serializes Serilog SelfLog capture for diagnostics tests.
    /// </summary>

    internal static class TestSelfLog
    {
        /// <summary>Serializes SelfLog enable and disable so concurrent tests do not interleave capture.</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Runs <paramref name="action"/> with SelfLog enabled and returns the captured text. Blocks the caller.</summary>
        /// <param name="action">Code whose SelfLog output the test wants.</param>
        /// <returns>Text SelfLog emitted during <paramref name="action"/>.</returns>
        internal static string Capture(Action action)
        {
            Gate.Wait();
            try
            {
                return CaptureCore(() =>
                {
                    action();
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
            }
            finally
            {
                Gate.Release();
            }
        }


        /// <summary>Runs <paramref name="action"/> with SelfLog enabled and returns the captured text without blocking on the action.</summary>
        /// <param name="action">Async code whose SelfLog output the test wants.</param>
        /// <returns>Text SelfLog emitted during <paramref name="action"/>.</returns>
        internal static async Task<string> CaptureAsync(Func<Task> action)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await CaptureCore(action).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }


        /// <summary>Enables SelfLog, awaits <paramref name="action"/>, and always disables SelfLog afterward.</summary>
        /// <param name="action">Code under capture.</param>
        /// <returns>Text appended while SelfLog was enabled.</returns>
        private static async Task<string> CaptureCore(Func<Task> action)
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
                await action().ConfigureAwait(false);
                lock (buffer)
                {
                    return buffer.ToString();
                }
            }
            finally
            {
                SelfLog.Disable();
            }
        }
    }
}
