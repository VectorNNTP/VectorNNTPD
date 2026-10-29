using Microsoft.Extensions.Options;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Rejects leftover <c>Nntpd:PostFilter</c> so cluster policy cannot split from NntpDB.
    /// </summary>
    public sealed class PostFilterLeftoverConfigurationValidator : IValidateOptions<NntpdOptions>
    {
        private readonly IConfiguration _configuration;

        /// <summary>Initializes the leftover-section check.</summary>
        public PostFilterLeftoverConfigurationValidator(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _configuration = configuration;
        }

        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var section = _configuration.GetSection("Nntpd:PostFilter");
            if (!section.Exists() || !section.GetChildren().Any())
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                "Nntpd:PostFilter is not node-local configuration. Cluster PostFilter policy is loaded from NntpDB table nntppostfiltercurrent.");
        }
    }
}
