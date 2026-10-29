using Microsoft.Extensions.Options;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>Validates <see cref="EmailOptions"/> at bind / startup time.</summary>
    /// <remarks>Never includes credentials in failure messages.</remarks>
    public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, EmailOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.Smtp ??= new SmtpOptions();
            options.Spool ??= new EmailSpoolOptions();

            var failures = new List<string>();
            ValidateSpool(options.Spool, failures);

            if (!options.Enabled)
            {
                return failures.Count > 0
                    ? ValidateOptionsResult.Fail(failures)
                    : ValidateOptionsResult.Success;
            }

            ValidateMailbox(
                options.DefaultFrom,
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.DefaultFrom)}",
                failures);

            if (!string.IsNullOrWhiteSpace(options.EnvelopeSender))
            {
                ValidateMailbox(
                    options.EnvelopeSender,
                    $"{EmailOptions.SectionName}:{nameof(EmailOptions.EnvelopeSender)}",
                    failures);
            }

            ValidateSmtp(options.Smtp, failures);
            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static void ValidateSpool(EmailSpoolOptions spool, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(spool.Directory) || spool.Directory.Trim().Length > 512)
            {
                failures.Add($"{EmailOptions.SectionName}:Spool:Directory must be a non-empty filesystem path of 512 characters or fewer.");
            }

            if (spool.ShutdownTimeout < TimeSpan.FromSeconds(1)
                || spool.ShutdownTimeout > TimeSpan.FromMinutes(5))
            {
                failures.Add(
                    $"{EmailOptions.SectionName}:Spool:ShutdownTimeout must be between 1 second and 5 minutes.");
            }

            if (spool.ScanInterval < TimeSpan.FromMilliseconds(20)
                || spool.ScanInterval > TimeSpan.FromMinutes(5))
            {
                failures.Add(
                    $"{EmailOptions.SectionName}:Spool:ScanInterval must be between 20ms and 5 minutes.");
            }
        }

        private static void ValidateSmtp(SmtpOptions smtp, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(smtp.Host))
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:Host is required when Email is enabled.");
            }
            else if (ContainsControl(smtp.Host) || smtp.Host.Trim().Length > 253)
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:Host must be a hostname or IP without control characters.");
            }

            if (smtp.Port is < 1 or > 65535)
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:Port must be between 1 and 65535.");
            }

            if (!Enum.IsDefined(smtp.Security))
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:Security must be None, StartTls, or ImplicitTls.");
            }

            if (smtp.ConnectTimeout < TimeSpan.FromMilliseconds(100)
                || smtp.ConnectTimeout > TimeSpan.FromMinutes(5))
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:ConnectTimeout must be between 100ms and 5 minutes.");
            }

            if (smtp.CommandTimeout < TimeSpan.FromMilliseconds(100)
                || smtp.CommandTimeout > TimeSpan.FromMinutes(10))
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:CommandTimeout must be between 100ms and 10 minutes.");
            }

            if (smtp.MaxAttempts is < 1 or > 20)
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:MaxAttempts must be between 1 and 20.");
            }

            if (smtp.InitialRetryDelay < TimeSpan.Zero || smtp.InitialRetryDelay > TimeSpan.FromHours(1))
            {
                failures.Add($"{EmailOptions.SectionName}:Smtp:InitialRetryDelay must be between 0 and 1 hour.");
            }

            if (smtp.MaximumRetryDelay < smtp.InitialRetryDelay || smtp.MaximumRetryDelay > TimeSpan.FromHours(1))
            {
                failures.Add(
                    $"{EmailOptions.SectionName}:Smtp:MaximumRetryDelay must be at least InitialRetryDelay and at most 1 hour.");
            }

            var userSet = !string.IsNullOrWhiteSpace(smtp.Username);
            var passwordSet = !string.IsNullOrEmpty(smtp.Password);
            if (userSet != passwordSet)
            {
                failures.Add(
                    $"{EmailOptions.SectionName}:Smtp:Username and Password must both be set or both omitted " +
                    "(use Email__Smtp__Password or secrets; never commit the value).");
            }
            else if (userSet)
            {
                if (ContainsControl(smtp.Username) || smtp.Username.Trim().Length > 256)
                {
                    failures.Add($"{EmailOptions.SectionName}:Smtp:Username must be 1–256 characters without control characters.");
                }

                if (smtp.RequireTlsForAuthentication && smtp.Security == SmtpSecurityMode.None)
                {
                    failures.Add(
                        $"{EmailOptions.SectionName}:Smtp:RequireTlsForAuthentication is true, so Security cannot be None. " +
                        "Set RequireTlsForAuthentication to false only when a controlled plaintext relay is intended.");
                }
            }
        }

        internal static bool TryValidateMailbox(string? value, out string mailbox)
        {
            mailbox = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.Length is 0 or > 254 || ContainsControl(trimmed))
            {
                return false;
            }

            var at = trimmed.IndexOf('@');
            if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
            {
                return false;
            }

            mailbox = trimmed;
            return true;
        }

        private static void ValidateMailbox(string value, string key, List<string> failures)
        {
            if (!TryValidateMailbox(value, out _))
            {
                failures.Add($"{key} must be a mailbox address.");
            }
        }

        private static bool ContainsControl(string value)
        {
            foreach (var ch in value)
            {
                if (char.IsControl(ch))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
