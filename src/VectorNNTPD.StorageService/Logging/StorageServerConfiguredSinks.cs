using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Sinks.File;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.StorageService.Logging
{

    /// <summary>
    /// Registers the application Console, File, and Async File sinks with direct Serilog calls.
    /// </summary>
    /// <remarks>
    /// <c>ReadFrom.Configuration</c> discovers sink extension methods by reflection. Native AOT does
    /// not retain that metadata, so a configured
    /// <c>Serilog:WriteTo</c> section produced a logger with no sinks. Argument values still come from
    /// configuration. Omitted arguments use the same defaults as the Serilog extension methods.
    /// A sink this type cannot represent exactly stays on <c>ReadFrom.Configuration</c>.
    /// </remarks>
    internal static class StorageServerConfiguredSinks
    {
        /// <summary>
        /// Default File template used by Serilog when <c>outputTemplate</c> is omitted.
        /// </summary>
        private const string DefaultFileTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Default Console template used by Serilog when <c>outputTemplate</c> is omitted.
        /// </summary>
        private const string DefaultConsoleTemplate =
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Serilog File default when <c>fileSizeLimitBytes</c> is absent. An explicit JSON null stays unlimited.
        /// </summary>
        private const long DefaultFileSizeLimitBytes = 1L * 1024 * 1024 * 1024;

        private static readonly string[] ConsoleArgumentNames = ["restrictedToMinimumLevel", "outputTemplate"];

        private static readonly string[] AsyncArgumentNames = ["bufferSize", "blockWhenFull", "configure"];

        private static readonly string[] FileArgumentNames =
        [
            "path",
            "restrictedToMinimumLevel",
            "outputTemplate",
            "fileSizeLimitBytes",
            "buffered",
            "flushToDiskInterval",
            "rollingInterval",
            "rollOnFileSizeLimit",
            "retainedFileCountLimit"
        ];

        /// <summary>
        /// Adds each <c>Serilog:WriteTo</c> entry. An empty section keeps the direct console fallback.
        /// </summary>
        /// <param name="logger">The host logger being built.</param>
        /// <param name="configuration">The application configuration after the File path has been resolved.</param>
        public static void Apply(LoggerConfiguration logger, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(configuration);

            var children = configuration.GetSection("Serilog:WriteTo").GetChildren().ToList();
            if (children.Count == 0)
            {
                logger.WriteTo.Console(
                    restrictedToMinimumLevel: StorageServerFileLogging.ConsoleMinimumLevel,
                    outputTemplate: StorageServerLoggingExtensions.ConsoleOutputTemplate);
                return;
            }

            foreach (var sink in children)
            {
                if (!TryApply(logger, sink))
                    ApplyRemainingSink(logger, configuration, sink);
            }
        }

        /// <summary>
        /// Returns the Serilog sections that do not construct sinks, so
        /// <c>ReadFrom.Configuration</c> can still apply minimum level and enrichment.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>A configuration whose <c>Serilog</c> section has no <c>WriteTo</c> entries.</returns>
        public static IConfiguration WithoutWriteTo(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            CopySection(configuration.GetSection("Serilog:MinimumLevel"), values);
            CopySection(configuration.GetSection("Serilog:Properties"), values);
            CopySection(configuration.GetSection("Serilog:LevelSwitches"), values);
            CopySection(configuration.GetSection("Serilog:FilterSwitches"), values);
            CopySection(configuration.GetSection("Serilog:Enrich"), values);
            CopySection(configuration.GetSection("Serilog:Filter"), values);
            CopySection(configuration.GetSection("Serilog:Destructure"), values);
            CopySection(configuration.GetSection("Serilog:AuditTo"), values);
            if (RequiresSinkAssemblies(configuration))
                CopySection(configuration.GetSection("Serilog:Using"), values);

            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        /// <summary>
        /// Applies one sink by name. Returns false when the entry needs the configuration binder.
        /// </summary>
        private static bool TryApply(LoggerConfiguration logger, IConfigurationSection sink)
        {
            var name = sink["Name"];
            var args = sink.GetSection("Args");
            if (name is null || !HasOnlyArguments(args, name.Equals("Console", StringComparison.OrdinalIgnoreCase)
                    ? ConsoleArgumentNames
                    : name.Equals("File", StringComparison.OrdinalIgnoreCase)
                        ? FileArgumentNames
                        : name.Equals("Async", StringComparison.OrdinalIgnoreCase)
                            ? AsyncArgumentNames
                            : []))
            {
                return false;
            }

            if (name.Equals("Console", StringComparison.OrdinalIgnoreCase))
            {
                logger.WriteTo.Console(
                    restrictedToMinimumLevel: ReadLevel(args, LogEventLevel.Verbose),
                    outputTemplate: ReadText(args, "outputTemplate", DefaultConsoleTemplate));
                return true;
            }

            if (name.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                WriteFile(logger.WriteTo, args);
                return true;
            }

            if (name.Equals("Async", StringComparison.OrdinalIgnoreCase)
                && TryReadSingleFile(args, out var fileArgs))
            {
                logger.WriteTo.Async(
                    wrapped => WriteFile(wrapped, fileArgs),
                    bufferSize: ReadInt(args, "bufferSize", 10_000),
                    blockWhenFull: ReadBool(args, "blockWhenFull", false));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Passes one unsupported sink through the configuration binder without the sinks already applied.
        /// </summary>
        private static void ApplyRemainingSink(
            LoggerConfiguration logger,
            IConfiguration configuration,
            IConfigurationSection sink)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            CopySection(configuration.GetSection("Serilog:Using"), values);
            CopySection(sink, values, "Serilog:WriteTo:0");
            var remaining = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            logger.ReadFrom.Configuration(remaining);
        }

        /// <summary>
        /// Reads the single nested File sink inside an Async wrapper.
        /// </summary>
        private static bool TryReadSingleFile(
            IConfigurationSection args,
            [NotNullWhen(true)] out IConfigurationSection? fileArgs)
        {
            fileArgs = null;
            var nested = args.GetSection("configure").GetChildren().ToList();
            if (nested.Count != 1
                || !string.Equals(nested[0]["Name"], "File", StringComparison.OrdinalIgnoreCase)
                || !HasOnlyArguments(nested[0].GetSection("Args"), FileArgumentNames))
            {
                return false;
            }

            fileArgs = nested[0].GetSection("Args");
            return true;
        }

        /// <summary>
        /// Adds a File sink from the same argument names Serilog.Settings.Configuration binds.
        /// </summary>
        private static void WriteFile(LoggerSinkConfiguration sink, IConfigurationSection args)
        {
            var path = args["path"];
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("The Serilog File sink requires Args:path.");

            sink.DailyGzipFile(
                path,
                restrictedToMinimumLevel: ReadLevel(args, LogEventLevel.Verbose),
                outputTemplate: ReadText(args, "outputTemplate", DefaultFileTemplate),
                fileSizeLimitBytes: ReadNullableLong(args, "fileSizeLimitBytes", DefaultFileSizeLimitBytes),
                buffered: ReadBool(args, "buffered", false),
                flushToDiskInterval: ReadTimeSpan(args, "flushToDiskInterval"),
                rollingInterval: ReadRollingInterval(args),
                rollOnFileSizeLimit: ReadBool(args, "rollOnFileSizeLimit", true),
                retainedFileCountLimit: ReadNullableInt(args, "retainedFileCountLimit", 31));
        }

        /// <summary>
        /// True when a copied section discovers methods and therefore needs <c>Serilog:Using</c>.
        /// </summary>
        private static bool RequiresSinkAssemblies(IConfiguration configuration)
        {
            return configuration.GetSection("Serilog:Enrich").GetChildren().Any()
                || configuration.GetSection("Serilog:Filter").GetChildren().Any()
                || configuration.GetSection("Serilog:Destructure").GetChildren().Any()
                || configuration.GetSection("Serilog:AuditTo").GetChildren().Any();
        }

        /// <summary>
        /// True when every argument name is one this type binds itself.
        /// </summary>
        private static bool HasOnlyArguments(IConfigurationSection args, string[] allowed)
        {
            if (allowed.Length == 0 && args.GetChildren().Any())
                return false;

            foreach (var child in args.GetChildren())
            {
                if (!allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Copies a configuration section, optionally placing it at a different key.
        /// </summary>
        private static void CopySection(
            IConfigurationSection section,
            Dictionary<string, string?> values,
            string? destinationPath = null)
        {
            if (!section.Exists())
                return;

            var path = destinationPath ?? section.Path;
            if (section.Value is not null || !section.GetChildren().Any())
                values[path] = section.Value;

            foreach (var child in section.GetChildren())
                CopySection(child, values, path + ":" + child.Key);
        }

        /// <summary>
        /// Reads a minimum level, using <paramref name="fallback"/> when the argument is absent.
        /// </summary>
        private static LogEventLevel ReadLevel(IConfigurationSection args, LogEventLevel fallback)
        {
            var text = args["restrictedToMinimumLevel"];
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!Enum.TryParse(text, ignoreCase: true, out LogEventLevel level))
            {
                throw new InvalidOperationException(
                    $"Serilog level '{text}' at '{args.Path}:restrictedToMinimumLevel' is not a log event level.");
            }

            return level;
        }

        /// <summary>
        /// Reads text, using <paramref name="fallback"/> when the argument is absent.
        /// </summary>
        private static string ReadText(IConfigurationSection args, string name, string fallback)
        {
            var text = args[name];
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        /// <summary>
        /// Reads a boolean, using <paramref name="fallback"/> when the argument is absent.
        /// </summary>
        private static bool ReadBool(IConfigurationSection args, string name, bool fallback)
        {
            var text = args[name];
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!bool.TryParse(text, out var value))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{text}' at '{args.Path}:{name}' is not a boolean.");
            }

            return value;
        }

        /// <summary>
        /// Reads an integer, using <paramref name="fallback"/> when the argument is absent.
        /// </summary>
        private static int ReadInt(IConfigurationSection args, string name, int fallback)
        {
            var text = args[name];
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (!int.TryParse(text, out var value))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{text}' at '{args.Path}:{name}' is not an integer.");
            }

            return value;
        }

        /// <summary>
        /// Reads a nullable long. A missing key uses <paramref name="missing"/>; an explicit null is unlimited.
        /// </summary>
        /// <remarks>
        /// <see cref="ConfigurationExtensions.Exists(IConfigurationSection)"/> is false for a JSON null,
        /// so presence is taken from <see cref="IConfiguration.GetChildren"/>.
        /// </remarks>
        private static long? ReadNullableLong(IConfigurationSection args, string name, long missing)
        {
            if (!TryGetArgument(args, name, out var argument))
                return missing;

            if (string.IsNullOrWhiteSpace(argument.Value))
                return null;

            if (!long.TryParse(argument.Value, out var value))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{argument.Value}' at '{argument.Path}' is not an integer.");
            }

            return value;
        }

        /// <summary>
        /// Reads a nullable integer. A missing key uses <paramref name="missing"/>; an explicit null stays null.
        /// </summary>
        private static int? ReadNullableInt(IConfigurationSection args, string name, int missing)
        {
            if (!TryGetArgument(args, name, out var argument))
                return missing;

            if (string.IsNullOrWhiteSpace(argument.Value))
                return null;

            if (!int.TryParse(argument.Value, out var value))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{argument.Value}' at '{argument.Path}' is not an integer.");
            }

            return value;
        }

        /// <summary>
        /// Finds an argument that is present even when its value is JSON null.
        /// </summary>
        private static bool TryGetArgument(
            IConfigurationSection args,
            string name,
            [NotNullWhen(true)] out IConfigurationSection? argument)
        {
            foreach (var child in args.GetChildren())
            {
                if (child.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    argument = child;
                    return true;
                }
            }

            argument = null;
            return false;
        }

        /// <summary>
        /// Reads a flush interval. A missing key leaves flushing to the File sink default.
        /// </summary>
        private static TimeSpan? ReadTimeSpan(IConfigurationSection args, string name)
        {
            var text = args[name];
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (!TimeSpan.TryParse(text, out var value))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{text}' at '{args.Path}:{name}' is not a time span.");
            }

            return value;
        }

        /// <summary>
        /// Reads the rolling interval. A missing key uses <see cref="RollingInterval.Infinite"/>.
        /// </summary>
        private static RollingInterval ReadRollingInterval(IConfigurationSection args)
        {
            var text = args["rollingInterval"];
            if (string.IsNullOrWhiteSpace(text))
                return RollingInterval.Infinite;

            if (!Enum.TryParse(text, ignoreCase: true, out RollingInterval interval))
            {
                throw new InvalidOperationException(
                    $"Serilog value '{text}' at '{args.Path}:rollingInterval' is not a rolling interval.");
            }

            return interval;
        }
    }
}
