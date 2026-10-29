using Microsoft.Extensions.Options;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Rejects JSON that still owns the Transit catalogue or the site-wide junk flags.
    /// </summary>
    public sealed class TransitLeftoverConfigurationValidator : IValidateOptions<NntpdOptions>
    {
        private readonly IConfiguration _configuration;

        /// <summary>Initializes the leftover-section check.</summary>
        public TransitLeftoverConfigurationValidator(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _configuration = configuration;
        }

        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (_configuration.GetSection(TransitPeersOptions.SectionName).GetChildren().Any())
            {
                return ValidateOptionsResult.Fail(
                    "Transit peers are not JSON configuration. The catalogue is loaded from NntpDB table nntptransitcurrent.");
            }

            if (_configuration.GetSection("Nntpd:Transit:WantTrash").Exists()
                || _configuration.GetSection("Nntpd:Transit:LogTrash").Exists())
            {
                return ValidateOptionsResult.Fail(
                    "Nntpd:Transit:WantTrash and Nntpd:Transit:LogTrash are not node-local settings. They are loaded from nntptransitglobalrevision.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
