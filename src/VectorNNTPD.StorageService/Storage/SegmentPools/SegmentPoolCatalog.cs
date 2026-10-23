namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>One validated physical device and the pool directories that use it.</summary>
    /// <param name="DeviceId">Canonical physical-device identity.</param>
    /// <param name="PoolPaths">Canonical pool directories backed by <paramref name="DeviceId"/>.</param>
    internal sealed record SegmentPoolDevice(string DeviceId, IReadOnlyList<string> PoolPaths);

    /// <summary>
    /// Devices published by startup validation. The monitor reads this after validation succeeds.
    /// </summary>
    internal sealed class SegmentPoolCatalog
    {
        private SegmentPoolDevice[] _devices = [];

        /// <summary>Validated devices. Empty until validation publishes a successful result.</summary>
        internal IReadOnlyList<SegmentPoolDevice> Devices => _devices;

        /// <summary>Replaces the catalog. Called once, on the startup thread, before the monitor starts.</summary>
        /// <param name="devices">Devices grouped from the successful inspections.</param>
        internal void Publish(IReadOnlyList<SegmentPoolDevice> devices)
        {
            ArgumentNullException.ThrowIfNull(devices);
            _devices = devices.ToArray();
        }
    }
}
