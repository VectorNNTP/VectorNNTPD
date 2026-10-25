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
        private int _waiting;
        private int _brokerOutstanding;
        private int _lateReplies;

        public int WaitingCount => Volatile.Read(ref _waiting);

        public int BrokerOutstandingCount => Volatile.Read(ref _brokerOutstanding);

        public int LateRepliesAfterTimeout => Volatile.Read(ref _lateReplies);

        public void OnPublished()
        {
            Interlocked.Increment(ref _waiting);
            Interlocked.Increment(ref _brokerOutstanding);
        }

        public void OnCallerTimeout()
        {
            var waiting = Interlocked.Decrement(ref _waiting);
            if (waiting < 0)
            {
                Interlocked.Exchange(ref _waiting, 0);
                throw new InvalidOperationException("Caller timeout decremented waiting below zero.");
            }
        }

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

        public void OnPublishFailed()
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Decrement(ref _brokerOutstanding);
        }
    }
}
