namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>Reads one cumulative counter sample for a validated physical device.</summary>
    internal interface IBlockDeviceCounterSource
    {
        /// <summary>
        /// Reads counters for <paramref name="deviceId"/>.
        /// </summary>
        /// <param name="deviceId">Canonical device identity published by startup validation.</param>
        /// <param name="counters">Sample when the method returns <see langword="true"/>.</param>
        /// <param name="error">Diagnostic when the method returns <see langword="false"/>. This is not a zero sample.</param>
        /// <returns><see langword="true"/> when a sample was read.</returns>
        bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error);
    }
}
