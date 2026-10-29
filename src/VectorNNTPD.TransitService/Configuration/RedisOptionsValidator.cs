using Microsoft.Extensions.Options;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>Validates <see cref="RedisOptions"/> at bind / startup time.</summary>
    public sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, RedisOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            if (options.Host is null || options.Host.Length == 0)
            {
                failures.Add($"{nameof(RedisOptions.Host)} must contain at least one Redis hostname or IP address.");
            }
            else
            {
                for (var i = 0; i < options.Host.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(options.Host[i]))
                    {
                        failures.Add($"{nameof(RedisOptions.Host)}[{i}] must not be empty.");
                    }
                }
            }

            if (options.Port is < 1 or > 65535)
            {
                failures.Add($"{nameof(RedisOptions.Port)} must be an integer in the range 1–65535.");
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}
