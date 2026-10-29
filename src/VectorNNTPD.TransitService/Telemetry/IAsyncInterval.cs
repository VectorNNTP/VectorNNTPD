namespace VectorNNTPD.TransitService.Telemetry
{
    /// <summary>Wait-for-next-period seam used by application telemetry.</summary>
    internal interface IAsyncInterval : IAsyncDisposable
    {
        /// <summary>
        /// Waits until the next period. Returns <see langword="false"/> when the interval is completed.
        /// </summary>
        ValueTask<bool> WaitNextAsync(CancellationToken cancellationToken);
    }

    /// <summary><see cref="PeriodicTimer"/> implementation of <see cref="IAsyncInterval"/>.</summary>
    internal sealed class PeriodicTimerInterval : IAsyncInterval
    {
        private readonly PeriodicTimer _timer;

        /// <summary>Initializes a period timer using <paramref name="timeProvider"/>.</summary>
        public PeriodicTimerInterval(TimeSpan period, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            _timer = new PeriodicTimer(period, timeProvider);
        }

        /// <inheritdoc />
        public ValueTask<bool> WaitNextAsync(CancellationToken cancellationToken) =>
            _timer.WaitForNextTickAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _timer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
