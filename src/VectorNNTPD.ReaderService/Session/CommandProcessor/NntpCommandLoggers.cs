using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Resolves per-command <see cref="ILogger"/> instances from the host <see cref="ILoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Command modules own completion logging; categories use the command type's full name
    /// (for example <c>VectorNNTPD.ReaderService.Session.Commands.StartTls</c>).
    /// </remarks>
    internal static class NntpCommandLoggers
    {
        private static ILoggerFactory _factory = NullLoggerFactory.Instance;
        private static readonly ConcurrentDictionary<string, ILogger> Cache = new(StringComparer.Ordinal);

        /// <summary>Configures the factory used for subsequent <see cref="For"/> calls.</summary>
        public static void Configure(ILoggerFactory? loggerFactory)
        {
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
            Cache.Clear();
        }

        /// <summary>Returns an <see cref="ILogger"/> whose category is <paramref name="commandType"/>'s full name.</summary>
        public static ILogger For(Type commandType)
        {
            ArgumentNullException.ThrowIfNull(commandType);
            var name = commandType.FullName ?? commandType.Name;
            return Cache.GetOrAdd(name, static (category, factory) => factory.CreateLogger(category), _factory);
        }
    }
}
