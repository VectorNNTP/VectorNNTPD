using Microsoft.Extensions.Options;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Validates <see cref="ModerationOptions"/>. Runtime authorization is loaded from
    /// <c>nntpmoderators</c>; a leftover <see cref="ModerationOptions.Moderators"/> list is rejected.
    /// </summary>
    public sealed class ModerationOptionsValidator : IValidateOptions<ModerationOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, ModerationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Moderators is { Length: > 0 })
            {
                return ValidateOptionsResult.Fail(
                    "Moderation:Moderators is no longer a runtime authorization source. "
                    + "Load moderator routes from nntpmoderators.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
