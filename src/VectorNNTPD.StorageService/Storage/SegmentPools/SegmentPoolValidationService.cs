using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Hard startup gate. Completes, or throws, before the accepted-article worker, VATP listener,
    /// and RabbitMQ advertisement publisher are started. Thresholds are not enforced here.
    /// </summary>
    internal sealed class SegmentPoolValidationService : IApplicationService
    {
        private readonly IOptions<StorageServerOptions> _options;
        private readonly ISegmentPoolInspector _inspector;
        private readonly SegmentPoolCatalog _catalog;
        private readonly ILogger<SegmentPoolValidationService> _logger;

        /// <summary>Initializes the gate.</summary>
        /// <param name="options">Bound StorageServer options.</param>
        /// <param name="inspector">Directory, mount, device, and writability checks.</param>
        /// <param name="catalog">Receives validated devices only after every pool succeeds.</param>
        /// <param name="logger">Validation diagnostics. Failures name the pool path.</param>
        public SegmentPoolValidationService(
            IOptions<StorageServerOptions> options,
            ISegmentPoolInspector inspector,
            SegmentPoolCatalog catalog,
            ILogger<SegmentPoolValidationService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(inspector);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _inspector = inspector;
            _catalog = catalog;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "SegmentPoolValidation";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var directories = _options.Value.SegmentPools?.Directories;
            if (directories is null || directories.Length == 0)
            {
                throw new InvalidOperationException(
                    "StorageServer:SegmentPools:Directories must contain at least one directory.");
            }
            var inspections = new List<SegmentPoolInspection>(directories.Length);
            var failures = new List<string>();
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspection = _inspector.Inspect(directory, AppContext.BaseDirectory, cancellationToken);
                inspections.Add(inspection);
                if (inspection.Failure is not null)
                {
                    var message = "StorageServer:SegmentPools directory '" + inspection.ConfiguredPath + "' ("
                        + inspection.CanonicalPath + "): " + inspection.Failure;
                    failures.Add(message);
                    SegmentPoolLogMessages.ValidationFailed(_logger, inspection.CanonicalPath.Length == 0 ? inspection.ConfiguredPath : inspection.CanonicalPath, inspection.Failure);
                }
            }

            var succeeded = inspections.Where(static inspection => inspection.Succeeded).ToArray();
            for (var i = 0; i < succeeded.Length; i++)
            {
                for (var j = i + 1; j < succeeded.Length; j++)
                {
                    var shared = succeeded[i].PhysicalDeviceIds
                        .Intersect(succeeded[j].PhysicalDeviceIds, StringComparer.Ordinal)
                        .ToArray();
                    if (shared.Length == 0)
                    {
                        continue;
                    }

                    var message = "StorageServer:SegmentPools directories '" + succeeded[i].ConfiguredPath
                        + "' and '" + succeeded[j].ConfiguredPath
                        + "' share physical device '" + string.Join(",", shared) + "'.";
                    failures.Add(message);
                    SegmentPoolLogMessages.ValidationFailed(_logger, succeeded[i].CanonicalPath, message);
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            }

            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var inspection in succeeded)
            {
                foreach (var device in inspection.PhysicalDeviceIds)
                {
                    if (!groups.TryGetValue(device, out var paths))
                    {
                        paths = [];
                        groups.Add(device, paths);
                    }

                    paths.Add(inspection.CanonicalPath);
                }

                SegmentPoolLogMessages.ValidationSucceeded(
                    _logger,
                    inspection.CanonicalPath,
                    string.Join(",", inspection.PhysicalDeviceIds));
            }

            _catalog.Publish(groups.Select(static pair => new SegmentPoolDevice(pair.Key, pair.Value)).ToArray());
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
