using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Validates <see cref="NntpdOptions"/> at options bind / startup time.
    /// </summary>
    /// <remarks>
    /// Does not bind sockets or call Cloudflare APIs. Never includes secret values in failure messages.
    /// </remarks>
    public sealed class NntpdOptionsValidator : IValidateOptions<NntpdOptions>
    {
        private readonly ILocalIpAddressAssignee _localIpAddressAssignee;

        /// <summary>
        /// Initializes a new instance of the <see cref="NntpdOptionsValidator"/> class.
        /// </summary>
        /// <param name="localIpAddressAssignee">Probe used to verify explicit bind addresses.</param>
        public NntpdOptionsValidator(ILocalIpAddressAssignee localIpAddressAssignee)
        {
            ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
            _localIpAddressAssignee = localIpAddressAssignee;
        }

        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();

            ValidateApplicationName(options, failures);
            ValidateTimeouts(options, failures);
            ValidateTop1000(options, failures);
            ValidateSystemd(options, failures);
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(options, _localIpAddressAssignee, failures);
            ValidateProxyHosts(options, failures);
            ValidatePorts(options, failures);
            ValidateDnsSuffixAndServerId(options, failures);
            ValidateAcme(options, failures);
            ValidateLogDir(options, failures);
            ValidateArticleIngestion(options, failures);
            ValidateTransitQueueMemoryLimit(options, failures);
            ValidateTransit(options, failures);
            ValidateSpeedTest(options, failures);
            ValidateFeedDiagnostics(options, failures);
            ValidateXTraceKeys(options, failures);
            ValidateNewsmaster(options, failures);

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static void ValidateSpeedTest(NntpdOptions options, List<string> failures)
        {
            var speed = options.SpeedTest ?? new SpeedTestOptions();
            if (speed.MaxDurationSeconds is < SpeedTestOptions.MinDurationSeconds
                or > SpeedTestOptions.MaxDurationSecondsLimit)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.SpeedTest)}.{nameof(SpeedTestOptions.MaxDurationSeconds)} must be between {SpeedTestOptions.MinDurationSeconds} and {SpeedTestOptions.MaxDurationSecondsLimit}.");
            }

            if (speed.MaxBytes is < SpeedTestOptions.MinBytes or > SpeedTestOptions.MaxBytesLimit)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.SpeedTest)}.{nameof(SpeedTestOptions.MaxBytes)} must be between {SpeedTestOptions.MinBytes} and {SpeedTestOptions.MaxBytesLimit}.");
            }

            if (speed.MaxConcurrent is < SpeedTestOptions.MinConcurrent
                or > SpeedTestOptions.MaxConcurrentLimit)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.SpeedTest)}.{nameof(SpeedTestOptions.MaxConcurrent)} must be between {SpeedTestOptions.MinConcurrent} and {SpeedTestOptions.MaxConcurrentLimit}.");
            }

            if (speed.MaxConcurrentPerPeer is < SpeedTestOptions.MinConcurrentPerPeer
                or > SpeedTestOptions.MaxConcurrentPerPeerLimit)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.SpeedTest)}.{nameof(SpeedTestOptions.MaxConcurrentPerPeer)} must be between {SpeedTestOptions.MinConcurrentPerPeer} and {SpeedTestOptions.MaxConcurrentPerPeerLimit}.");
            }
        }

        private static void ValidateFeedDiagnostics(NntpdOptions options, List<string> failures)
        {
            var feed = options.FeedDiagnostics ?? new FeedDiagnosticsOptions();
            if (feed.IntervalSeconds is < FeedDiagnosticsOptions.MinIntervalSeconds
                or > FeedDiagnosticsOptions.MaxIntervalSeconds)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.FeedDiagnostics)}.{nameof(FeedDiagnosticsOptions.IntervalSeconds)} must be between {FeedDiagnosticsOptions.MinIntervalSeconds} and {FeedDiagnosticsOptions.MaxIntervalSeconds}.");
            }
        }

        private static void ValidateTransit(NntpdOptions options, List<string> failures)
        {
            var transit = options.Transit ?? new TransitOptions();
            var depth = transit.StreamOutstandingArticleDepth;
            if (!NntpStreamArticleTxScheduler.IsValidDepth(depth))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.StreamOutstandingArticleDepth)} must be between {NntpStreamArticleTxScheduler.MinDepth} and {NntpStreamArticleTxScheduler.MaxDepth}.");
            }
        }

        private static void ValidateArticleIngestion(NntpdOptions options, List<string> failures)
        {
            var ingestion = options.ArticleIngestion ?? new ArticleIngestionOptions();
            if (ingestion.QueueCapacity is < 1 or > 100_000)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.QueueCapacity)} must be between 1 and 100000.");
            }

            if (ingestion.MaxArticleBytes is < 1 or > 100 * 1024 * 1024)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxArticleBytes)} must be between 1 and 104857600.");
            }

            if (ingestion.MaxOutstandingStoresPerConnection is not (1 or 2))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxOutstandingStoresPerConnection)} must be 1 or 2.");
            }

            if (ingestion.MinWorkers is < 1 or > 512)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MinWorkers)} must be between 1 and 512.");
            }

            if (ingestion.MaxWorkers is < 1 or > 512)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxWorkers)} must be between 1 and 512.");
            }

            if (ingestion.MaxWorkers < ingestion.MinWorkers)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxWorkers)} must be greater than or equal to {nameof(ArticleIngestionOptions.MinWorkers)}.");
            }

            if (ingestion.MaxPublishConcurrency is < 1 or > 512)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxPublishConcurrency)} must be between 1 and 512.");
            }

            if (ingestion.OverviewDbPublisherBatchSize is < 1 or > 10_000)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbPublisherBatchSize)} must be between 1 and 10000.");
            }

            if (ingestion.OverviewDbPublisherShutdownSeconds is < 1 or > 60)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbPublisherShutdownSeconds)} must be between 1 and 60.");
            }

            if (ingestion.OverviewDbWorkQueueMemoryLimit < 1)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbWorkQueueMemoryLimit)} must be at least 1.");
            }

            if (ingestion.OverviewDbMinPublisherWorkers is < 1 or > 512)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbMinPublisherWorkers)} must be between 1 and 512.");
            }

            if (ingestion.OverviewDbMaxPublisherWorkers is < 1 or > 512)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbMaxPublisherWorkers)} must be between 1 and 512.");
            }

            if (ingestion.OverviewDbMaxPublisherWorkers < ingestion.OverviewDbMinPublisherWorkers)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.OverviewDbMaxPublisherWorkers)} must be greater than or equal to {nameof(ArticleIngestionOptions.OverviewDbMinPublisherWorkers)}.");
            }

            if (ingestion.ScaleIntervalSeconds is < 1 or > 3600)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleIntervalSeconds)} must be between 1 and 3600.");
            }

            if (ingestion.ScaleUpPressureThreshold is < 0 or > 1)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleUpPressureThreshold)} must be between 0 and 1.");
            }

            if (ingestion.ScaleDownPressureThreshold is < 0 or > 1)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleDownPressureThreshold)} must be between 0 and 1.");
            }

            if (ingestion.ScaleUpPressureThreshold <= ingestion.ScaleDownPressureThreshold)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleUpPressureThreshold)} must be greater than {nameof(ArticleIngestionOptions.ScaleDownPressureThreshold)}.");
            }

            if (ingestion.ScaleUpConsecutiveIntervals is < 1 or > 100)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleUpConsecutiveIntervals)} must be between 1 and 100.");
            }

            if (ingestion.ScaleDownConsecutiveIntervals is < 1 or > 100)
            {
                failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.ScaleDownConsecutiveIntervals)} must be between 1 and 100.");
            }
        }

        private static void ValidateTransitQueueMemoryLimit(NntpdOptions options, List<string> failures)
        {
            if (options.TransitQueueMemoryLimit < 1)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.TransitQueueMemoryLimit)} must be a positive byte count (1 through {long.MaxValue}).");
            }
        }

        private static void ValidateApplicationName(NntpdOptions options, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(options.ApplicationName))
            {
                failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be a non-empty string.");
            }
            else if (options.ApplicationName.Length > 128)
            {
                failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be 128 characters or fewer.");
            }
        }

        private static void ValidateTimeouts(NntpdOptions options, List<string> failures)
        {
            if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must be at least 1 second.");
            }

            if (options.GracefulShutdownTimeout > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must not exceed 1 hour.");
            }

            if (options.StartupTimeout is { } startupTimeout)
            {
                if (startupTimeout < TimeSpan.FromSeconds(1))
                {
                    failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must be at least 1 second when specified.");
                }

                if (startupTimeout > TimeSpan.FromHours(1))
                {
                    failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must not exceed 1 hour.");
                }
            }

            if (options.CloudFlareOperationTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(NntpdOptions.CloudFlareOperationTimeout)} must be at least 1 second.");
            }

            if (options.CloudFlareOperationTimeout > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(NntpdOptions.CloudFlareOperationTimeout)} must not exceed 1 hour.");
            }

            if (options.HistoryTime < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(NntpdOptions.HistoryTime)} must be at least 1 second.");
            }

            if (options.HistoryTime > TimeSpan.FromDays(7))
            {
                failures.Add($"{nameof(NntpdOptions.HistoryTime)} must not exceed 7 days.");
            }

            if (options.IdleTime < NntpdOptions.MinIdleTime)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.IdleTime)} must be at least {NntpdOptions.MinIdleTime} second.");
            }

            if (options.IdleTime > NntpdOptions.MaxIdleTime)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.IdleTime)} must not exceed {NntpdOptions.MaxIdleTime} seconds.");
            }

            if (options.MaxArticleSize < NntpdOptions.MinMaxArticleSize
                || options.MaxArticleSize > NntpdOptions.MaxMaxArticleSize)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.MaxArticleSize)} must be between {NntpdOptions.MinMaxArticleSize} and {NntpdOptions.MaxMaxArticleSize}.");
            }

            if (string.IsNullOrWhiteSpace(options.MailComplaintsTo)
                || !IsPlausibleEmail(options.MailComplaintsTo))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.MailComplaintsTo)} must be a valid mailbox address.");
            }
        }

        private static void ValidateTop1000(NntpdOptions options, List<string> failures)
        {
            if (options.Top1000 is null || options.Top1000.Length == 0)
            {
                return;
            }

            for (var i = 0; i < options.Top1000.Length; i++)
            {
                var entry = options.Top1000[i];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (!EmailOptionsValidator.TryValidateMailbox(entry, out _))
                {
                    failures.Add(
                        $"{nameof(NntpdOptions.Top1000)}[{i}] must be a mailbox address or whitespace.");
                }
            }
        }

        private static void ValidateXTraceKeys(NntpdOptions options, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(options.XTraceKey)
                || !XTraceKeyParser.TryDecode(options.XTraceKey, out _))
            {
                failures.Add(
                    $"{NntpdOptions.XTraceKeyConfigurationKey} must be a 32-byte AES-256 key encoded as 64 hex characters or Base64 " +
                    $"(use environment variable {NntpdOptions.XTraceKeyEnvironmentVariable} or secrets; never commit the value).");
            }

            if (!string.IsNullOrWhiteSpace(options.XTracePreviousKey)
                && !XTraceKeyParser.TryDecode(options.XTracePreviousKey, out _))
            {
                failures.Add(
                    $"{NntpdOptions.XTracePreviousKeyConfigurationKey} must be a 32-byte AES-256 key encoded as 64 hex characters or Base64 " +
                    $"when set (use environment variable {NntpdOptions.XTracePreviousKeyEnvironmentVariable} or secrets; never commit the value).");
            }
        }

        private static void ValidateNewsmaster(NntpdOptions options, List<string> failures)
        {
            var userSet = !string.IsNullOrWhiteSpace(options.NewsmasterUser);
            var passwordSet = !string.IsNullOrEmpty(options.NewsmasterPassword);
            if (userSet != passwordSet)
            {
                failures.Add(
                    $"{NntpdOptions.NewsmasterUserConfigurationKey} and {NntpdOptions.NewsmasterPasswordConfigurationKey} must both be set or both omitted " +
                    $"(use {NntpdOptions.NewsmasterPasswordEnvironmentVariable} or secrets for the password; never commit the value).");
                return;
            }

            if (!userSet)
            {
                return;
            }

            var user = options.NewsmasterUser.Trim();
            if (user.Length is < 1 or > 64 || ContainsControl(user))
            {
                failures.Add($"{NntpdOptions.NewsmasterUserConfigurationKey} must be 1–64 characters without control characters.");
            }

            if (options.NewsmasterPassword.Length > 256)
            {
                failures.Add($"{NntpdOptions.NewsmasterPasswordConfigurationKey} must not exceed 256 characters.");
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

        private static void ValidateSystemd(NntpdOptions options, List<string> failures)
        {
            if (options.Systemd is null)
            {
                failures.Add($"{nameof(NntpdOptions.Systemd)} must be provided.");
                return;
            }

            var fraction = options.Systemd.WatchdogIntervalFraction;
            if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction is <= 0 or >= 1)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
            }
            else if (fraction is < 0.05 or > 0.9)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
            }
        }

        private static void ValidateProxyHosts(NntpdOptions options, List<string> failures)
        {
            if (options.ProxyHosts is null)
            {
                return;
            }

            for (var i = 0; i < options.ProxyHosts.Length; i++)
            {
                var entry = options.ProxyHosts[i];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    failures.Add($"{nameof(NntpdOptions.ProxyHosts)}[{i}] must not be empty.");
                    continue;
                }

                var trimmed = entry.Trim();
                if (trimmed.Contains('/') || trimmed.Contains('*'))
                {
                    failures.Add(
                        $"{nameof(NntpdOptions.ProxyHosts)}[{i}] must be a literal IPv4 or IPv6 address (CIDR and wildcards are not supported).");
                    continue;
                }

                if (!IPAddress.TryParse(trimmed, out var address))
                {
                    failures.Add($"{nameof(NntpdOptions.ProxyHosts)}[{i}] is not a valid IPv4 or IPv6 address.");
                    continue;
                }

                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    failures.Add(
                        $"{nameof(NntpdOptions.ProxyHosts)}[{i}] must not be an any-address wildcard; use an explicit proxy peer address.");
                }
            }
        }

        private static void ValidatePorts(NntpdOptions options, List<string> failures)
        {
            if (options.BindPort is < 1 or > 65535)
            {
                failures.Add($"{nameof(NntpdOptions.BindPort)} must be an integer in the range 1–65535.");
            }

            if (options.BindPortTls is < 0 or > 65535)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.BindPortTls)} must be 0 (TLS disabled) or an integer in the range 1–65535.");
            }
        }

        private static void ValidateAcme(NntpdOptions options, List<string> failures)
        {
            // Directory URL, renewal threshold, zone id, DNS suffix, account email,
            // certificate password, and Cloudflare API key come from nntpsharedconfig.
            // State directory stays on this application.
            if (string.IsNullOrWhiteSpace(options.AcmeStateDir))
            {
                failures.Add($"{nameof(NntpdOptions.AcmeStateDir)} must be a non-empty filesystem path.");
            }
        }

        private static void ValidateLogDir(NntpdOptions options, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(options.LogDir))
            {
                failures.Add($"{nameof(NntpdOptions.LogDir)} must be a non-empty filesystem path.");
            }
        }

        private static bool IsPlausibleEmail(string email)
        {
            var trimmed = email.Trim();
            if (trimmed.Length is 0 or > 254)
            {
                return false;
            }

            var at = trimmed.IndexOf('@');
            if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
            {
                return false;
            }

            var domain = trimmed[(at + 1)..];
            return domain.Contains('.', StringComparison.Ordinal) && IsValidDnsSuffix(domain);
        }

        private static void ValidateDnsSuffixAndServerId(NntpdOptions options, List<string> failures)
        {
            switch (ServerIdRules.Classify(options.ServerId))
            {
                case ServerIdValidationStatus.Missing:
                    failures.Add(
                        $"{nameof(NntpdOptions.ServerId)} is required and must be an integer in the range {ServerIdRules.MinimumInclusive}–{ServerIdRules.MaximumInclusive} (no default; set {NntpdOptions.SectionName}:{nameof(NntpdOptions.ServerId)} or {NntpdOptions.ServerIdEnvironmentVariable}).");
                    break;
                case ServerIdValidationStatus.OutOfRange:
                    failures.Add(
                        $"{nameof(NntpdOptions.ServerId)} must be an integer in the range {ServerIdRules.MinimumInclusive}–{ServerIdRules.MaximumInclusive}.");
                    break;
            }
        }

        /// <summary>
        /// Validates DNS suffix / name syntax (labels, length, allowed characters).
        /// </summary>
        internal static bool IsValidDnsSuffix(string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return false;
            }

            var value = suffix.Trim().TrimEnd('.');
            if (value.Length is 0 or > 253)
            {
                return false;
            }

            if (value.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            var labels = value.Split('.');
            if (labels.Length == 0)
            {
                return false;
            }

            foreach (var label in labels)
            {
                if (label.Length is 0 or > 63)
                {
                    return false;
                }

                if (label[0] == '-' || label[^1] == '-')
                {
                    return false;
                }

                foreach (var ch in label)
                {
                    if (!IsDnsLabelChar(ch))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsDnsLabelChar(char ch) =>
            char.IsAsciiLetterOrDigit(ch) || ch == '-';

        /// <summary>
        /// Returns whether <paramref name="text"/> contains a configured secret (for tests / diagnostics hygiene).
        /// </summary>
        public static bool ContainsSecret(string text, string? secret)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.Contains(secret, StringComparison.Ordinal);
        }

        /// <summary>
        /// Joins validation failures for assertions without exposing caller-supplied secrets.
        /// </summary>
        public static string JoinFailures(ValidateOptionsResult result)
        {
            if (result.Failures is null)
            {
                return result.FailureMessage ?? string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var failure in result.Failures)
            {
                if (sb.Length > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"; {failure}");
                }
                else
                {
                    sb.Append(failure);
                }
            }

            return sb.ToString();
        }
    }
}
