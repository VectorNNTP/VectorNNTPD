using Microsoft.Extensions.DependencyInjection;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>Replaces the production pool inspector after hosting registration.</summary>
    internal static class SegmentPoolTestSupport
    {
        /// <summary>
        /// Installs <see cref="PermissiveSegmentPoolInspector"/> so host tests can start without a dedicated disk.
        /// </summary>
        /// <param name="services">Service collection that already contains StorageServer hosting.</param>
        internal static void UsePermissiveInspector(IServiceCollection services) =>
            ReplaceInspector(services, typeof(PermissiveSegmentPoolInspector));

        /// <summary>
        /// Installs <see cref="FailingSegmentPoolInspector"/> so host startup fails inside the real application lifecycle.
        /// </summary>
        /// <param name="services">Service collection that already contains StorageServer hosting.</param>
        internal static void UseFailingInspector(IServiceCollection services) =>
            ReplaceInspector(services, typeof(FailingSegmentPoolInspector));

        /// <summary>
        /// Installs an in-memory directory creator so host tests do not create the real shard tree.
        /// </summary>
        /// <param name="services">Service collection that already contains StorageServer hosting.</param>
        internal static void UseMemoryShardDirectories(IServiceCollection services) =>
            ReplaceDirectories(services, new MemorySpoolShardDirectorySystem());

        /// <summary>Installs a directory creator that fails on the first create.</summary>
        /// <param name="services">Service collection that already contains StorageServer hosting.</param>
        internal static void UseFailingShardDirectories(IServiceCollection services) =>
            ReplaceDirectories(services, new MemorySpoolShardDirectorySystem { FailMessage = "shard create denied" });

        private static void ReplaceDirectories(IServiceCollection services, ISpoolShardDirectorySystem directories)
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISpoolShardDirectorySystem))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddSingleton(directories);
        }

        private static void ReplaceInspector(IServiceCollection services, Type implementation)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(implementation);
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISegmentPoolInspector))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddSingleton(typeof(ISegmentPoolInspector), implementation);
        }
    }
}
