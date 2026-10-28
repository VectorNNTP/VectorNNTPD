using Microsoft.Extensions.Options;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Startup validation for <see cref="LabUpstreamIsolationOptions"/>.
    /// When disabled, always succeeds. When enabled, incomplete or non-loopback config fails closed.
    /// </summary>
    internal sealed class LabUpstreamIsolationOptionsValidator : IValidateOptions<LabUpstreamIsolationOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, LabUpstreamIsolationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Enabled)
            {
                if (options.RewritePublishedDestinations)
                {
                    return ValidateOptionsResult.Fail(
                        "BackFiller:LabUpstreamIsolation:RewritePublishedDestinations requires Enabled=true "
                        + "(lab destination rewrite cannot run without the dial allowlist).");
                }

                return ValidateOptionsResult.Success;
            }

            var failures = new List<string>();
            if (string.IsNullOrWhiteSpace(options.AllowedHost))
            {
                failures.Add(
                    "BackFiller:LabUpstreamIsolation:AllowedHost is required when Enabled is true "
                    + $"(must be {LabUpstreamIsolationOptions.RequiredLiteralHost}).");
            }
            else if (!string.Equals(
                         options.AllowedHost.Trim(),
                         LabUpstreamIsolationOptions.RequiredLiteralHost,
                         StringComparison.Ordinal))
            {
                failures.Add(
                    "BackFiller:LabUpstreamIsolation:AllowedHost must be exactly "
                    + $"{LabUpstreamIsolationOptions.RequiredLiteralHost} when Enabled is true "
                    + "(localhost, DNS names, IPv6, and non-loopback addresses are rejected). "
                    + $"Configured value is not accepted.");
            }

            if (options.AllowedPort is null)
            {
                failures.Add(
                    "BackFiller:LabUpstreamIsolation:AllowedPort is required when Enabled is true.");
            }
            else if (options.AllowedPort is < 1 or > 65535)
            {
                failures.Add(
                    "BackFiller:LabUpstreamIsolation:AllowedPort must be an integer in 1..65535 when Enabled is true.");
            }

            if (options.RewritePublishedDestinations && !options.RejectTls)
            {
                failures.Add(
                    "BackFiller:LabUpstreamIsolation:RejectTls must be true when RewritePublishedDestinations is true "
                    + "(local ARTICLE peer is cleartext; rewritten destinations use UseTls=false).");
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}
