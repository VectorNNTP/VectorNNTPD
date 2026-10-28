using Microsoft.Extensions.Options;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>Validates <see cref="LabStageProfilingOptions"/> at host start.</summary>
    internal sealed class LabStageProfilingOptionsValidator : IValidateOptions<LabStageProfilingOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, LabStageProfilingOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!options.Enabled)
            {
                return ValidateOptionsResult.Success;
            }

            if (options.ExportIntervalSeconds is < 1 or > 3600)
            {
                return ValidateOptionsResult.Fail(
                    "BackFiller:LabStageProfiling:ExportIntervalSeconds must be between 1 and 3600 when Enabled.");
            }

            if (!string.IsNullOrWhiteSpace(options.ExportDirectory))
            {
                try
                {
                    _ = Path.GetFullPath(options.ExportDirectory);
                }
                catch (Exception ex) when (ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
                {
                    return ValidateOptionsResult.Fail(
                        "BackFiller:LabStageProfiling:ExportDirectory is not a usable path: " + ex.Message);
                }
            }

            return ValidateOptionsResult.Success;
        }
    }
}
