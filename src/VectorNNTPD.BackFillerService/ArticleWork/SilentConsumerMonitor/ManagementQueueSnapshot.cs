namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>Queue-level Management metrics. Missing fields stay null (never invent zeros).</summary>
    internal sealed class ManagementQueueSnapshot
    {
        /// <summary>Queue entity name from the Management request.</summary>
        public required string QueueName { get; init; }

        /// <summary>Virtual host from the Management request.</summary>
        public required string VirtualHost { get; init; }

        /// <summary>UTC sample time from the engine <see cref="TimeProvider"/> at parse time.</summary>
        public required DateTimeOffset SampledAtUtc { get; init; }

        /// <summary>Broker <c>messages_ready</c> gauge when present; otherwise null (never invented as zero).</summary>
        public long? MessagesReady { get; init; }

        /// <summary>Broker <c>messages_unacknowledged</c> gauge when present; otherwise null.</summary>
        public long? MessagesUnacknowledged { get; init; }

        /// <summary>Broker <c>consumers</c> gauge when present; otherwise null.</summary>
        public long? Consumers { get; init; }

        /// <summary>Broker <c>message_stats.ack_details.rate</c> when present and finite; otherwise null.</summary>
        public double? AckRate { get; init; }

        /// <summary>
        /// Broker deliver/get rate from <c>deliver_get_details.rate</c> or <c>deliver_details.rate</c> when present; otherwise null.
        /// </summary>
        public double? DeliverGetRate { get; init; }

        /// <summary>Cumulative <c>message_stats.ack</c> counter when present; used to derive rates across samples.</summary>
        public long? AckTotal { get; init; }

        /// <summary>Cumulative <c>message_stats.deliver_get</c> counter when present; used to derive rates across samples.</summary>
        public long? DeliverGetTotal { get; init; }

        /// <summary>
        /// True when <see cref="Consumers"/>, <see cref="MessagesReady"/>, and <see cref="MessagesUnacknowledged"/> are all non-null.
        /// </summary>
        public bool HasRequiredGauges =>
            Consumers is not null && MessagesReady is not null && MessagesUnacknowledged is not null;
    }

    /// <summary>Management poll classification.</summary>
    internal enum ManagementPollKind
    {
        /// <summary>Parsed document.</summary>
        Success = 0,

        /// <summary>HTTP 404.</summary>
        QueueMissing = 1,

        /// <summary>401/403.</summary>
        AuthenticationFailure = 2,

        /// <summary>Timeout, transport, HTTP, or parse failure.</summary>
        TransientFailure = 3,

        /// <summary>Caller cancel.</summary>
        Cancelled = 4,
    }

    /// <summary>One Management poll result (success snapshot or classified failure).</summary>
    internal sealed class ManagementPollResult
    {
        /// <summary>Success vs missing queue, auth failure, transient, or cancel.</summary>
        public required ManagementPollKind Kind { get; init; }

        /// <summary>Non-secret reason suitable for diagnostics (never credentials).</summary>
        public required string Reason { get; init; }

        /// <summary>Parsed gauges when <see cref="Kind"/> is Success; otherwise null.</summary>
        public ManagementQueueSnapshot? Snapshot { get; init; }

        /// <summary>True when Kind is Success and a snapshot is present.</summary>
        public bool IsSuccess => Kind == ManagementPollKind.Success && Snapshot is not null;
    }
}
