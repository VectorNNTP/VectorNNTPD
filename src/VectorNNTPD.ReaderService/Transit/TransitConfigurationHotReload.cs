using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Applies <see cref="IOptionsMonitor{TOptions}"/> updates to <see cref="TransitConfigurationStore"/>.
    /// </summary>
    /// <remarks>
    /// An empty top-level <c>Transit</c> section does not replace the store. The published
    /// MySQL catalogue is authoritative, and a JSON reload must not wipe it. Non-empty
    /// in-memory sections still replace the snapshot so tests can exercise admission.
    /// Invalid non-empty reloads are ignored so the last valid snapshot remains.
    /// </remarks>
    public sealed class TransitConfigurationHotReload : IDisposable
    {
        private readonly IDisposable? _subscription;

        /// <summary>Initializes hot reload from the options monitor into the snapshot store.</summary>
        public TransitConfigurationHotReload(
            IOptionsMonitor<TransitPeersOptions> monitor,
            TransitConfigurationStore store,
            ILogger<TransitConfigurationHotReload> logger)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(logger);

            Apply(monitor.CurrentValue, store, logger, isReload: false);
            _subscription = monitor.OnChange(options => Apply(options, store, logger, isReload: true));
        }

        /// <inheritdoc />
        public void Dispose() => _subscription?.Dispose();

        private static void Apply(
            TransitPeersOptions options,
            TransitConfigurationStore store,
            ILogger logger,
            bool isReload)
        {
            if (options.Count == 0)
            {
                return;
            }

            var result = new TransitPeersOptionsValidator().Validate(Options.DefaultName, options);
            if (result.Failed)
            {
                var first = result.Failures?.FirstOrDefault() ?? result.FailureMessage ?? "invalid Transit configuration";
                if (isReload)
                {
                    TransitLogMessages.InvalidReloadIgnored(logger, first);
                }
                else
                {
                    throw new OptionsValidationException(
                        Options.DefaultName,
                        typeof(TransitPeersOptions),
                        result.Failures);
                }

                return;
            }

            store.Replace(TransitConfigurationSnapshot.Create(options));
            if (isReload)
            {
                TransitLogMessages.ConfigurationReloaded(logger, options.Count);
            }
        }
    }
}
