using Microsoft.Extensions.Options;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Validates <see cref="ControlOptions"/> when the section is bound.
    /// </summary>
    /// <remarks>
    /// An omitted or empty catalogue is valid. This validator does not authorize
    /// control messages and does not load PGP keys.
    /// </remarks>
    public sealed class ControlOptionsValidator : IValidateOptions<ControlOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, ControlOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            var authorities = options.PgpAuthorities?.Authorities;
            if (authorities is null || authorities.Length == 0)
            {
                return ValidateOptionsResult.Success;
            }

            for (var i = 0; i < authorities.Length; i++)
            {
                var authority = authorities[i];
                if (authority is null)
                {
                    failures.Add($"{nameof(ControlOptions.PgpAuthorities)}.{nameof(PgpAuthoritiesOptions.Authorities)}[{i}] must not be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(authority.Name))
                {
                    failures.Add($"{nameof(ControlOptions.PgpAuthorities)}.{nameof(PgpAuthoritiesOptions.Authorities)}[{i}].{nameof(PgpAuthorityOptions.Name)} must not be empty.");
                }

                if (authority.KeyFingerprint is not null
                    && !string.IsNullOrWhiteSpace(authority.KeyFingerprint)
                    && !PgpAuthorityFingerprint.TryNormalize(authority.KeyFingerprint, out _))
                {
                    failures.Add($"{nameof(ControlOptions.PgpAuthorities)}.{nameof(PgpAuthoritiesOptions.Authorities)}[{i}].{nameof(PgpAuthorityOptions.KeyFingerprint)} must be hexadecimal.");
                }

                var authorizations = authority.Authorizations;
                if (authorizations is null)
                {
                    continue;
                }

                for (var j = 0; j < authorizations.Length; j++)
                {
                    var rule = authorizations[j];
                    if (rule is null)
                    {
                        failures.Add($"{nameof(ControlOptions.PgpAuthorities)}.{nameof(PgpAuthoritiesOptions.Authorities)}[{i}].{nameof(PgpAuthorityOptions.Authorizations)}[{j}] must not be null.");
                        continue;
                    }

                    RequireNonEmpty(
                        failures,
                        rule.Message,
                        i,
                        j,
                        nameof(PgpAuthorityAuthorizationOptions.Message));
                    RequireNonEmpty(
                        failures,
                        rule.From,
                        i,
                        j,
                        nameof(PgpAuthorityAuthorizationOptions.From));
                    RequireNonEmpty(
                        failures,
                        rule.Newsgroups,
                        i,
                        j,
                        nameof(PgpAuthorityAuthorizationOptions.Newsgroups));
                    RequireNonEmpty(
                        failures,
                        rule.VerificationIdentity,
                        i,
                        j,
                        nameof(PgpAuthorityAuthorizationOptions.VerificationIdentity));
                }
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static void RequireNonEmpty(
            List<string> failures,
            string? value,
            int authorityIndex,
            int ruleIndex,
            string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add(
                    $"{nameof(ControlOptions.PgpAuthorities)}.{nameof(PgpAuthoritiesOptions.Authorities)}[{authorityIndex}].{nameof(PgpAuthorityOptions.Authorizations)}[{ruleIndex}].{propertyName} must not be empty.");
            }
        }
    }
}
