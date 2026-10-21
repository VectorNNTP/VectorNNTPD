using System.Net;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Validates <see cref="RabbitMqOptions"/> connection and reserved fields at bind / startup time.</summary>
    /// <remarks>
    /// <para>
    /// Error-severity rules match the historical BackFiller / NNTPD RabbitMQ connection contract.
    /// Nested <see cref="RabbitMqOptions.Management"/> is optional for connectivity-only hosts:
    /// when <see cref="RabbitMqManagementOptions.BaseUrl"/> is omitted, Management is not validated.
    /// When BaseUrl is present, URL shape and timeout are checked.
    /// </para>
    /// <para>
    /// Applications that require Management (for example NNTPD ArticleWork availability discovery)
    /// should register a decorator that also requires BaseUrl and broker credentials.
    /// </para>
    /// </remarks>
    internal sealed class RabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            ValidateConnection(options, failures);
            ValidateOptionalManagement(options, failures);
            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        /// <summary>
        /// Validates connection, credential, and reserved pool/timeout fields.
        /// Does not require Management HTTP settings.
        /// </summary>
        /// <param name="rabbitMq">Options instance to validate.</param>
        /// <param name="failures">Failure list to append to.</param>
        internal static void ValidateConnection(RabbitMqOptions rabbitMq, List<string> failures)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(failures);

            if (rabbitMq.ChannelLeaseTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:ChannelLeaseTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.ChannelLeaseTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:ChannelLeaseTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.ChannelLeaseTimeoutSeconds > 3600)
            {
                failures.Add("RabbitMQ:ChannelLeaseTimeoutSeconds must be between 1 and 3600.");
            }

            if (rabbitMq.RpcTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:RpcTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.RpcTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:RpcTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.RpcTimeoutSeconds > 3600)
            {
                failures.Add("RabbitMQ:RpcTimeoutSeconds must be between 1 and 3600.");
            }

            if (rabbitMq.ChannelLeaseTimeoutSeconds < rabbitMq.RpcTimeoutSeconds)
            {
                failures.Add("RabbitMQ:ChannelLeaseTimeoutSeconds must be greater than or equal to RpcTimeoutSeconds.");
            }

            if (rabbitMq.ConnectionBlockedTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:ConnectionBlockedTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.ConnectionBlockedTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:ConnectionBlockedTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.ConnectionBlockedTimeoutSeconds is < 5 or > 3600)
            {
                failures.Add("RabbitMQ:ConnectionBlockedTimeoutSeconds must be between 5 and 3600.");
            }

            if (rabbitMq.ConnectionBlockedTimeoutSeconds < rabbitMq.RpcTimeoutSeconds)
            {
                failures.Add("RabbitMQ:ConnectionBlockedTimeoutSeconds must be greater than or equal to RpcTimeoutSeconds.");
            }

            if (rabbitMq.WorkRequestMaxPayloadBytes is null)
            {
                failures.Add("RabbitMQ:WorkRequestMaxPayloadBytes is required.");
                return;
            }

            if (rabbitMq.WorkRequestMaxPayloadBytes is < 1 or > 4096)
            {
                failures.Add("RabbitMQ:WorkRequestMaxPayloadBytes must be between 1 and 4096.");
            }

            if (rabbitMq.Hosts is null || rabbitMq.Hosts.Length == 0)
            {
                failures.Add("RabbitMQ:Hosts must contain at least one entry.");
                return;
            }

            var normalizedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rabbitMq.Hosts.Length; i++)
            {
                var host = rabbitMq.Hosts[i];
                if (string.IsNullOrWhiteSpace(host))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must not be empty.");
                    continue;
                }

                var trimmedHost = host.Trim();
                if (trimmedHost.Contains("://", StringComparison.Ordinal))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must not include a URI scheme.");
                    continue;
                }

                if (trimmedHost.Contains('@'))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must not include credentials.");
                    continue;
                }

                if (trimmedHost.Contains('/'))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must not include path or virtual host syntax.");
                    continue;
                }

                if (trimmedHost.Contains('?'))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must not include query parameters.");
                    continue;
                }

                var isIpAddress = IPAddress.TryParse(trimmedHost, out _);
                if (!isIpAddress && Uri.CheckHostName(trimmedHost) != UriHostNameType.Dns)
                {
                    failures.Add($"RabbitMQ:Hosts:{i} must be a valid hostname or IP address.");
                    continue;
                }

                if (!normalizedHosts.Add(trimmedHost))
                {
                    failures.Add($"RabbitMQ:Hosts:{i} duplicate host entries are not allowed.");
                }
            }

            // Unique host count selects Article Work classic vs quorum topology and, for quorum,
            // is also x-quorum-initial-group-size. Reject oversized lists rather than clamping.
            if (normalizedHosts.Count > ArticleWorkQueueTopology.MaxConfiguredHosts)
            {
                failures.Add(
                    $"RabbitMQ:Hosts must contain at most {ArticleWorkQueueTopology.MaxConfiguredHosts} unique entries (Article Work quorum initial group size limit).");
            }

            var hasUsername = !string.IsNullOrWhiteSpace(rabbitMq.Username);
            if (rabbitMq.Username is not null && string.IsNullOrWhiteSpace(rabbitMq.Username))
            {
                failures.Add("RabbitMQ:Username must not be empty or whitespace when configured.");
            }

            if (hasUsername && string.IsNullOrWhiteSpace(rabbitMq.Password))
            {
                failures.Add("RabbitMQ:Password is required when Username is configured.");
            }

            if (!hasUsername && rabbitMq.Password is not null)
            {
                failures.Add("RabbitMQ:Username is required when Password is configured.");
            }

            if (rabbitMq.Password is not null && string.IsNullOrWhiteSpace(rabbitMq.Password))
            {
                failures.Add("RabbitMQ:Password must not be empty or whitespace when password authentication is configured.");
            }

            if (rabbitMq.VirtualHost is null)
            {
                failures.Add("RabbitMQ:VirtualHost is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(rabbitMq.VirtualHost))
            {
                failures.Add("RabbitMQ:VirtualHost must not be empty or whitespace.");
                return;
            }

            if (rabbitMq.VirtualHost.Contains('\0'))
            {
                failures.Add("RabbitMQ:VirtualHost contains invalid null character.");
            }

            if (rabbitMq.EnableSsl is null)
            {
                failures.Add("RabbitMQ:EnableSsl is required.");
                return;
            }

            if (rabbitMq.Port is null)
            {
                failures.Add("RabbitMQ:Port is required.");
                return;
            }

            if (rabbitMq.Port <= 0)
            {
                failures.Add("RabbitMQ:Port must be greater than zero.");
                return;
            }

            if (rabbitMq.Port > 65535)
            {
                failures.Add("RabbitMQ:Port must be between 1 and 65535.");
            }

            if (rabbitMq.ConnectionScaleDownIdleSeconds is null)
            {
                failures.Add("RabbitMQ:ConnectionScaleDownIdleSeconds is required.");
                return;
            }

            if (rabbitMq.ConnectionScaleDownIdleSeconds <= 0)
            {
                failures.Add("RabbitMQ:ConnectionScaleDownIdleSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.ConnectionScaleDownIdleSeconds is < 30 or > 86400)
            {
                failures.Add("RabbitMQ:ConnectionScaleDownIdleSeconds must be between 30 and 86400.");
            }

            if (rabbitMq.ScaleDownCooldownSeconds is null)
            {
                failures.Add("RabbitMQ:ScaleDownCooldownSeconds is required.");
                return;
            }

            if (rabbitMq.ScaleDownCooldownSeconds < 0)
            {
                failures.Add("RabbitMQ:ScaleDownCooldownSeconds must be greater than or equal to zero.");
                return;
            }

            if (rabbitMq.ScaleDownCooldownSeconds > 3600)
            {
                failures.Add("RabbitMQ:ScaleDownCooldownSeconds must be between 0 and 3600.");
            }

            if (rabbitMq.MinimumConnectionLifetimeSeconds is null)
            {
                failures.Add("RabbitMQ:MinimumConnectionLifetimeSeconds is required.");
                return;
            }

            if (rabbitMq.MinimumConnectionLifetimeSeconds <= 0)
            {
                failures.Add("RabbitMQ:MinimumConnectionLifetimeSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.MinimumConnectionLifetimeSeconds is < 30 or > 86400)
            {
                failures.Add("RabbitMQ:MinimumConnectionLifetimeSeconds must be between 30 and 86400.");
            }

            if (rabbitMq.MinConnections is null)
            {
                failures.Add("RabbitMQ:MinConnections is required.");
                return;
            }

            if (rabbitMq.MinConnections <= 0)
            {
                failures.Add("RabbitMQ:MinConnections must be greater than zero.");
                return;
            }

            if (rabbitMq.MinConnections > 512)
            {
                failures.Add("RabbitMQ:MinConnections must be between 1 and 512.");
            }

            if (rabbitMq.MaxConnections is null)
            {
                failures.Add("RabbitMQ:MaxConnections is required.");
                return;
            }

            if (rabbitMq.MaxConnections <= 0)
            {
                failures.Add("RabbitMQ:MaxConnections must be greater than zero.");
                return;
            }

            if (rabbitMq.MaxConnections > 512)
            {
                failures.Add("RabbitMQ:MaxConnections must be between 1 and 512.");
            }

            if (rabbitMq.MinConnections is > 0 && rabbitMq.MaxConnections is > 0 && rabbitMq.MinConnections > rabbitMq.MaxConnections)
            {
                failures.Add("RabbitMQ:MinConnections must be less than or equal to MaxConnections.");
            }

            if (rabbitMq.NetworkRecoveryIntervalSeconds is null)
            {
                failures.Add("RabbitMQ:NetworkRecoveryIntervalSeconds is required.");
                return;
            }

            if (rabbitMq.NetworkRecoveryIntervalSeconds <= 0)
            {
                failures.Add("RabbitMQ:NetworkRecoveryIntervalSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.NetworkRecoveryIntervalSeconds > 3600)
            {
                failures.Add("RabbitMQ:NetworkRecoveryIntervalSeconds must be between 1 and 3600.");
            }

            if (rabbitMq.PoolReconnectBaseDelayMs is null)
            {
                failures.Add("RabbitMQ:PoolReconnectBaseDelayMs is required.");
                return;
            }

            if (rabbitMq.PoolReconnectBaseDelayMs <= 0)
            {
                failures.Add("RabbitMQ:PoolReconnectBaseDelayMs must be greater than zero.");
                return;
            }

            if (rabbitMq.PoolReconnectBaseDelayMs is < 50 or > 60000)
            {
                failures.Add("RabbitMQ:PoolReconnectBaseDelayMs must be between 50 and 60000.");
            }

            if (rabbitMq.PoolReconnectMaxDelayMs is null)
            {
                failures.Add("RabbitMQ:PoolReconnectMaxDelayMs is required.");
                return;
            }

            if (rabbitMq.PoolReconnectMaxDelayMs <= 0)
            {
                failures.Add("RabbitMQ:PoolReconnectMaxDelayMs must be greater than zero.");
                return;
            }

            if (rabbitMq.PoolReconnectMaxDelayMs is < 50 or > 300000)
            {
                failures.Add("RabbitMQ:PoolReconnectMaxDelayMs must be between 50 and 300000.");
            }

            if (rabbitMq.PoolReconnectBaseDelayMs is > 0
                && rabbitMq.PoolReconnectMaxDelayMs is > 0
                && rabbitMq.PoolReconnectMaxDelayMs < rabbitMq.PoolReconnectBaseDelayMs)
            {
                failures.Add("RabbitMQ:PoolReconnectMaxDelayMs must be greater than or equal to PoolReconnectBaseDelayMs.");
            }

            if (rabbitMq.MaxPendingLeaseWaiters is null)
            {
                failures.Add("RabbitMQ:MaxPendingLeaseWaiters is required.");
                return;
            }

            if (rabbitMq.MaxPendingLeaseWaiters < 0)
            {
                failures.Add("RabbitMQ:MaxPendingLeaseWaiters must be greater than or equal to zero.");
                return;
            }

            if (rabbitMq.MaxPendingLeaseWaiters > 65536)
            {
                failures.Add("RabbitMQ:MaxPendingLeaseWaiters must be between 0 and 65536.");
            }

            if (rabbitMq.PublishConfirmTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:PublishConfirmTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.PublishConfirmTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:PublishConfirmTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.PublishConfirmTimeoutSeconds > 3600)
            {
                failures.Add("RabbitMQ:PublishConfirmTimeoutSeconds must be between 1 and 3600.");
            }

            if (rabbitMq.MaximumShutdownDrainTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:MaximumShutdownDrainTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.MaximumShutdownDrainTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:MaximumShutdownDrainTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.MaximumShutdownDrainTimeoutSeconds > 3600)
            {
                failures.Add("RabbitMQ:MaximumShutdownDrainTimeoutSeconds must be between 1 and 3600.");
            }

            if (rabbitMq.RequestedHeartbeatSeconds is null)
            {
                failures.Add("RabbitMQ:RequestedHeartbeatSeconds is required.");
                return;
            }

            if (rabbitMq.RequestedHeartbeatSeconds < 0)
            {
                failures.Add("RabbitMQ:RequestedHeartbeatSeconds must be greater than or equal to zero.");
                return;
            }

            if (rabbitMq.RequestedHeartbeatSeconds > 3600)
            {
                failures.Add("RabbitMQ:RequestedHeartbeatSeconds must be between 0 and 3600.");
            }

            if (rabbitMq.SocketTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:SocketTimeoutSeconds is required.");
                return;
            }

            if (rabbitMq.SocketTimeoutSeconds <= 0)
            {
                failures.Add("RabbitMQ:SocketTimeoutSeconds must be greater than zero.");
                return;
            }

            if (rabbitMq.SocketTimeoutSeconds is < 5 or > 600)
            {
                failures.Add("RabbitMQ:SocketTimeoutSeconds must be between 5 and 600.");
            }

            if (rabbitMq.DegradedThreshold is null)
            {
                failures.Add("RabbitMQ:DegradedThreshold is required.");
                return;
            }

            if (rabbitMq.DegradedThreshold is <= 0d or > 1d)
            {
                failures.Add("RabbitMQ:DegradedThreshold must be greater than 0 and less than or equal to 1.");
            }

            if (rabbitMq.UnhealthyThreshold is null)
            {
                failures.Add("RabbitMQ:UnhealthyThreshold is required.");
                return;
            }

            if (rabbitMq.UnhealthyThreshold <= 0)
            {
                failures.Add("RabbitMQ:UnhealthyThreshold must be greater than zero.");
                return;
            }

            if (rabbitMq.UnhealthyThreshold > 120)
            {
                failures.Add("RabbitMQ:UnhealthyThreshold must be between 1 and 120.");
            }

            if (rabbitMq.ConsumerPrefetchCount is 0)
            {
                failures.Add("RabbitMQ:ConsumerPrefetchCount must be between 1 and 65535.");
            }

            if (rabbitMq.ArticleWorkDeliveryLimit is null)
            {
                failures.Add("RabbitMQ:ArticleWorkDeliveryLimit is required.");
            }
            else if (rabbitMq.ArticleWorkDeliveryLimit is < 1 or > 1000)
            {
                failures.Add("RabbitMQ:ArticleWorkDeliveryLimit must be between 1 and 1000.");
            }

            if (rabbitMq.RequestedChannelMax is null)
            {
                failures.Add("RabbitMQ:RequestedChannelMax is required.");
                return;
            }

            if (rabbitMq.RequestedChannelMax <= 0)
            {
                failures.Add("RabbitMQ:RequestedChannelMax must be greater than zero.");
                return;
            }

            if (rabbitMq.RequestedChannelMax > 65535)
            {
                failures.Add("RabbitMQ:RequestedChannelMax must be between 1 and 65535.");
            }

            if (rabbitMq.ChannelPoolSize is null)
            {
                failures.Add("RabbitMQ:ChannelPoolSize is required.");
                return;
            }

            if (rabbitMq.ChannelPoolSize <= 0)
            {
                failures.Add("RabbitMQ:ChannelPoolSize must be greater than zero.");
                return;
            }

            if (rabbitMq.ChannelPoolSize > 8192)
            {
                failures.Add("RabbitMQ:ChannelPoolSize must be between 1 and 8192.");
            }

            if (rabbitMq.MaxConnections is > 0 && rabbitMq.RequestedChannelMax is > 0)
            {
                int effectiveChannelLimit;
                try
                {
                    effectiveChannelLimit = checked(rabbitMq.MaxConnections.Value * rabbitMq.RequestedChannelMax.Value);
                }
                catch (OverflowException)
                {
                    failures.Add("RabbitMQ:ChannelPoolSize: MaxConnections and RequestedChannelMax produce an invalid effective channel limit.");
                    return;
                }

                if (rabbitMq.ChannelPoolSize > effectiveChannelLimit)
                {
                    failures.Add(
                        $"RabbitMQ:ChannelPoolSize must be less than or equal to effective channel limit ({effectiveChannelLimit}) derived from MaxConnections * RequestedChannelMax.");
                }
            }
        }

        /// <summary>
        /// Validates nested Management settings when <see cref="RabbitMqManagementOptions.BaseUrl"/> is present.
        /// </summary>
        private static void ValidateOptionalManagement(RabbitMqOptions rabbitMq, List<string> failures)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(failures);

            rabbitMq.Management ??= new RabbitMqManagementOptions();
            var management = rabbitMq.Management;
            if (string.IsNullOrWhiteSpace(management.BaseUrl))
            {
                return;
            }

            ValidateManagementShape(management, failures);
        }

        /// <summary>
        /// Validates Management BaseUrl shape and request timeout (BaseUrl must already be non-empty).
        /// </summary>
        internal static void ValidateManagementShape(RabbitMqManagementOptions management, List<string> failures)
        {
            ArgumentNullException.ThrowIfNull(management);
            ArgumentNullException.ThrowIfNull(failures);

            var trimmedBase = management.BaseUrl!.Trim();
            if (!Uri.TryCreate(trimmedBase, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment)
                || !string.IsNullOrEmpty(baseUri.UserInfo))
            {
                failures.Add(
                    "RabbitMQ:Management:BaseUrl must be an absolute http or https URL without credentials, query, or fragment.");
                return;
            }

            if (!string.IsNullOrEmpty(baseUri.AbsolutePath.TrimEnd('/')))
            {
                failures.Add(
                    "RabbitMQ:Management:BaseUrl must not include a path (omit /api; the client appends /api/queues/{vhost}).");
            }

            if (management.RequestTimeoutSeconds is null)
            {
                failures.Add("RabbitMQ:Management:RequestTimeoutSeconds is required.");
                return;
            }

            if (management.RequestTimeoutSeconds is < 1 or > 60)
            {
                failures.Add("RabbitMQ:Management:RequestTimeoutSeconds must be between 1 and 60.");
            }
        }
    }
}
