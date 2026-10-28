using System.Diagnostics.Metrics;

namespace VectorNNTPD.BackFillerService.Retention
{
    /// <summary>
    /// Publishes live <see cref="IArticleRetentionAuthority"/> occupancy as .NET runtime metrics.
    /// Values are read from the authority on each observe callback; no duplicate counters are kept.
    /// </summary>
    /// <remarks>
    /// Meter name <see cref="MeterName"/> is intended for <c>dotnet-counters</c> / EventPipe sampling
    /// (same tooling used for <c>System.Runtime</c>). Instruments carry no Message-ID or article labels.
    /// Registration does not change retention policy, TTL, reclaim, or ArrayPool behaviour.
    /// Debug log events 5500–5502 are unchanged.
    /// </remarks>
    internal sealed class ArticleRetentionMetrics : IHostedService, IDisposable
    {
        /// <summary>Meter name passed to <c>dotnet-counters --counters</c>.</summary>
        internal const string MeterName = "VectorNNTPD.BackFillerService.ArticleRetention";

        /// <summary>
        /// Observable gauge of authority-owned ArtData bytes
        /// (<see cref="IArticleRetentionAuthority.RetainedPayloadBytes"/>). Unit <c>By</c> (bytes).
        /// </summary>
        internal const string RetainedPayloadBytesInstrumentName = "retained_payload_bytes";

        /// <summary>
        /// Observable gauge of entries not yet physically disposed
        /// (<see cref="IArticleRetentionAuthority.RetainedCount"/>). Unit <c>{article}</c>.
        /// </summary>
        internal const string RetainedCountInstrumentName = "retained_count";

        /// <summary>Authority observed by the gauges. Not owned or disposed by this type.</summary>
        private readonly IArticleRetentionAuthority _authority;

        /// <summary>Meter that owns the observable instruments. Disposed with this instance.</summary>
        private readonly Meter _meter;

        /// <summary>0 until <see cref="Dispose"/>; 1 after. Prevents double meter disposal.</summary>
        private int _disposed;

        /// <summary>
        /// Creates observable gauges bound to <paramref name="authority"/>.
        /// </summary>
        /// <param name="authority">Live retention authority. Source of truth for both gauges.</param>
        /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
        internal ArticleRetentionMetrics(IArticleRetentionAuthority authority)
        {
            ArgumentNullException.ThrowIfNull(authority);
            _authority = authority;
            _meter = new Meter(MeterName);
            _ = _meter.CreateObservableGauge(
                RetainedPayloadBytesInstrumentName,
                ObserveRetainedPayloadBytes,
                unit: "By",
                description: "Authority-owned retained article ArtData bytes (IArticleRetentionAuthority.RetainedPayloadBytes).");
            _ = _meter.CreateObservableGauge(
                RetainedCountInstrumentName,
                ObserveRetainedCount,
                unit: "{article}",
                description: "Retained entries not yet physically disposed (IArticleRetentionAuthority.RetainedCount).");
        }

        /// <summary>No-op. Instruments are active from construction until <see cref="Dispose"/>.</summary>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>No-op. Meter disposal runs via <see cref="Dispose"/> when the DI container releases this singleton.</summary>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Disposes the <see cref="Meter"/> so observe callbacks stop. Idempotent.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _meter.Dispose();
        }

        /// <summary>Callback for <see cref="RetainedPayloadBytesInstrumentName"/>.</summary>
        /// <returns>Current <see cref="IArticleRetentionAuthority.RetainedPayloadBytes"/>.</returns>
        private long ObserveRetainedPayloadBytes() => _authority.RetainedPayloadBytes;

        /// <summary>Callback for <see cref="RetainedCountInstrumentName"/>.</summary>
        /// <returns>Current <see cref="IArticleRetentionAuthority.RetainedCount"/> as <see cref="long"/>.</returns>
        private long ObserveRetainedCount() => _authority.RetainedCount;
    }
}
