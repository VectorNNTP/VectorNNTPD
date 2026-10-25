namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Separates caller waiters from broker-outstanding RPC correlations for exclusive ReplyTo lifetime.
    /// </summary>
    /// <remarks>
    /// Used by unit tests to lock the lifecycle contract also implemented inside
    /// <see cref="ArticleWorkStressClient"/>. Timeout clears the waiter without clearing broker outstanding.
    /// </remarks>
    internal sealed class ArticleWorkRpcOutstandingTracker
    {
        /// <summary>Callers still awaiting a correlated reply.</summary>
        private int _waiting;

        /// <summary>Published correlations not yet settled by reply or publish failure.</summary>
        private int _brokerOutstanding;

        /// <summary>Replies received after the caller wait timed out.</summary>
        private int _lateReplies;

        /// <summary>Number of active caller waiters.</summary>
        public int WaitingCount => Volatile.Read(ref _waiting);

        /// <summary>Number of in-flight broker correlations (includes post-timeout late replies).</summary>
        public int BrokerOutstandingCount => Volatile.Read(ref _brokerOutstanding);

        /// <summary>Cumulative late replies counted when <see cref="OnReply"/> runs with <c>callerStillWaiting: false</c>.</summary>
        public int LateRepliesAfterTimeout => Volatile.Read(ref _lateReplies);

        /// <summary>Increments both waiter and broker-outstanding when a request is published.</summary>
        public void OnPublished()
        {
            Interlocked.Increment(ref _waiting);
            Interlocked.Increment(ref _brokerOutstanding);
        }

        /// <summary>Removes the caller waiter without decrementing broker outstanding.</summary>
        /// <exception cref="InvalidOperationException">Waiting count would go negative.</exception>
        public void OnCallerTimeout()
        {
            var waiting = Interlocked.Decrement(ref _waiting);
            if (waiting < 0)
            {
                Interlocked.Exchange(ref _waiting, 0);
                throw new InvalidOperationException("Caller timeout decremented waiting below zero.");
            }
        }

        /// <summary>Settles one correlation; increments late-reply counter when the caller already timed out.</summary>
        /// <param name="callerStillWaiting">When <see langword="false"/>, the reply is treated as late after caller timeout.</param>
        /// <exception cref="InvalidOperationException">An internal counter would go negative.</exception>
        public void OnReply(bool callerStillWaiting)
        {
            if (callerStillWaiting)
            {
                var waiting = Interlocked.Decrement(ref _waiting);
                if (waiting < 0)
                {
                    Interlocked.Exchange(ref _waiting, 0);
                    throw new InvalidOperationException("Reply decremented waiting below zero.");
                }
            }
            else
            {
                Interlocked.Increment(ref _lateReplies);
            }

            var outstanding = Interlocked.Decrement(ref _brokerOutstanding);
            if (outstanding < 0)
            {
                Interlocked.Exchange(ref _brokerOutstanding, 0);
                throw new InvalidOperationException("Reply decremented broker outstanding below zero.");
            }
        }

        /// <summary>Rolls back both counters when publish fails before the broker accepts the message.</summary>
        public void OnPublishFailed()
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Decrement(ref _brokerOutstanding);
        }
    }
}
