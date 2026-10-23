namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// In-process article-index counters. They are the metrics surface, same as spool persistence counters.
    /// StorageServer does not register a separate meter pipeline.
    /// </summary>
    internal sealed class ArticleIndexCounters
    {
        private long _insertsCommitted;
        private long _duplicates;
        private long _touchesUpdated;
        private long _touchesUnchanged;
        private long _touchesMissing;
        private long _batches;
        private long _batchTicks;
        private long _failures;
        private long _retries;
        private long _busy;
        private long _backpressure;
        private long _checkpoints;
        private long _checkpointBusy;
        private long _checkpointTicks;
        private long _maxCheckpointMilliseconds;
        private long _lastCheckpointLogFrames;
        private long _lastCheckpointedFrames;
        private long _membershipHits;
        private long _membershipMisses;
        private long _cachePublishFailures;
        private long _startupRows;
        private long _startupMilliseconds;
        private long _lastBatchSize;
        private long _lastBatchMilliseconds;
        private long _queueDepth;
        private int _queueCapacity;
        private long _reconciliationMissing;
        private long _reconciliationStale;
        private long _readCount;
        private long _readTicks;

        /// <summary>Inserts that created a row.</summary>
        internal long InsertsCommitted => Volatile.Read(ref _insertsCommitted);

        /// <summary>Inserts that left an existing row unchanged.</summary>
        internal long Duplicates => Volatile.Read(ref _duplicates);

        /// <summary>Touches that moved <c>LastAccessedAt</c>.</summary>
        internal long TouchesUpdated => Volatile.Read(ref _touchesUpdated);

        /// <summary>Touches that found a row and did not move the timestamp.</summary>
        internal long TouchesUnchanged => Volatile.Read(ref _touchesUnchanged);

        /// <summary>Touches that found no row.</summary>
        internal long TouchesMissing => Volatile.Read(ref _touchesMissing);

        /// <summary>Transactions that committed.</summary>
        internal long Batches => Volatile.Read(ref _batches);

        /// <summary>Commit attempts that failed.</summary>
        internal long Failures => Volatile.Read(ref _failures);

        /// <summary>Extra attempts after a busy or locked result.</summary>
        internal long Retries => Volatile.Read(ref _retries);

        /// <summary>Busy or locked results observed by the writer.</summary>
        internal long BusyEvents => Volatile.Read(ref _busy);

        /// <summary>Enqueue calls that observed a full queue before waiting.</summary>
        internal long BackpressureWaits => Volatile.Read(ref _backpressure);

        /// <summary>Checkpoint attempts.</summary>
        internal long Checkpoints => Volatile.Read(ref _checkpoints);

        /// <summary>Checkpoints that reported busy.</summary>
        internal long CheckpointBusy => Volatile.Read(ref _checkpointBusy);

        /// <summary>Total time spent inside checkpoint calls, in milliseconds.</summary>
        internal long CheckpointMilliseconds => Volatile.Read(ref _checkpointTicks) / TimeSpan.TicksPerMillisecond;

        /// <summary>Longest single checkpoint call, in milliseconds.</summary>
        internal long MaxCheckpointMilliseconds => Volatile.Read(ref _maxCheckpointMilliseconds);

        /// <summary>WAL frames reported by the most recent checkpoint.</summary>
        internal long LastCheckpointLogFrames => Volatile.Read(ref _lastCheckpointLogFrames);

        /// <summary>Frames copied by the most recent checkpoint.</summary>
        internal long LastCheckpointedFrames => Volatile.Read(ref _lastCheckpointedFrames);

        /// <summary>Membership checks that found the hash.</summary>
        internal long MembershipHits => Volatile.Read(ref _membershipHits);

        /// <summary>Membership checks that did not find the hash.</summary>
        internal long MembershipMisses => Volatile.Read(ref _membershipMisses);

        /// <summary>Committed rows whose memory publication threw.</summary>
        internal long CachePublishFailures => Volatile.Read(ref _cachePublishFailures);

        /// <summary>Rows copied into the membership index at startup.</summary>
        internal long StartupRows => Volatile.Read(ref _startupRows);

        /// <summary>Startup membership load time in milliseconds.</summary>
        internal long StartupMilliseconds => Volatile.Read(ref _startupMilliseconds);

        /// <summary>Mutations in the most recent committed batch.</summary>
        internal long LastBatchSize => Volatile.Read(ref _lastBatchSize);

        /// <summary>Duration of the most recent committed batch in milliseconds.</summary>
        internal long LastBatchMilliseconds => Volatile.Read(ref _lastBatchMilliseconds);

        /// <summary>Last observed writer queue depth.</summary>
        internal long QueueDepth => Volatile.Read(ref _queueDepth);

        /// <summary>Writer queue capacity.</summary>
        internal int QueueCapacity => Volatile.Read(ref _queueCapacity);

        /// <summary>Spool files reconciled into the index during this process.</summary>
        internal long ReconciliationMissing => Volatile.Read(ref _reconciliationMissing);

        /// <summary>Index rows whose final file was missing. Those rows were not deleted.</summary>
        internal long ReconciliationStale => Volatile.Read(ref _reconciliationStale);

        /// <summary>Location and eviction reads.</summary>
        internal long Reads => Volatile.Read(ref _readCount);

        /// <summary>Average read time in milliseconds, or zero when no read has finished.</summary>
        internal double AverageReadMilliseconds
        {
            get
            {
                var reads = Volatile.Read(ref _readCount);
                if (reads == 0)
                {
                    return 0;
                }

                var ticks = Volatile.Read(ref _readTicks);
                return TimeSpan.FromTicks(ticks / reads).TotalMilliseconds;
            }
        }

        /// <summary>Average committed batch time in milliseconds, or zero when no batch has committed.</summary>
        internal double AverageBatchMilliseconds
        {
            get
            {
                var batches = Volatile.Read(ref _batches);
                if (batches == 0)
                {
                    return 0;
                }

                var ticks = Volatile.Read(ref _batchTicks);
                return TimeSpan.FromTicks(ticks / batches).TotalMilliseconds;
            }
        }

        /// <summary>Records a new row.</summary>
        internal void NoteInserted() => Interlocked.Increment(ref _insertsCommitted);

        /// <summary>Records an insert that did not replace an existing row.</summary>
        internal void NoteDuplicate() => Interlocked.Increment(ref _duplicates);

        /// <summary>Records a moved access timestamp.</summary>
        internal void NoteTouchUpdated() => Interlocked.Increment(ref _touchesUpdated);

        /// <summary>Records an access timestamp that was not newer.</summary>
        internal void NoteTouchUnchanged() => Interlocked.Increment(ref _touchesUnchanged);

        /// <summary>Records a touch for a hash that is not indexed.</summary>
        internal void NoteTouchMissing() => Interlocked.Increment(ref _touchesMissing);

        /// <summary>Records one committed batch.</summary>
        /// <param name="size">Mutations in the batch.</param>
        /// <param name="duration">Time from the start of the transaction to the commit return.</param>
        internal void NoteBatch(int size, TimeSpan duration)
        {
            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _batchTicks, duration.Ticks);
            Volatile.Write(ref _lastBatchSize, size);
            Volatile.Write(ref _lastBatchMilliseconds, (long)duration.TotalMilliseconds);
        }

        /// <summary>Records a failed commit attempt.</summary>
        internal void NoteFailure() => Interlocked.Increment(ref _failures);

        /// <summary>Records a retry after busy or locked.</summary>
        internal void NoteRetry() => Interlocked.Increment(ref _retries);

        /// <summary>Records a busy or locked SQLite result.</summary>
        internal void NoteBusy() => Interlocked.Increment(ref _busy);

        /// <summary>Records an enqueue that found the queue full.</summary>
        internal void NoteBackpressure() => Interlocked.Increment(ref _backpressure);

        /// <summary>Records a checkpoint result.</summary>
        /// <param name="busy">True when SQLite reported frames it could not copy.</param>
        /// <param name="duration">Time spent in the checkpoint call.</param>
        /// <param name="logFrames">WAL frames SQLite reported.</param>
        /// <param name="checkpointedFrames">Frames copied by this call.</param>
        internal void NoteCheckpoint(bool busy, TimeSpan duration, int logFrames, int checkpointedFrames)
        {
            Interlocked.Increment(ref _checkpoints);
            if (busy)
            {
                Interlocked.Increment(ref _checkpointBusy);
            }

            var milliseconds = (long)duration.TotalMilliseconds;
            Interlocked.Add(ref _checkpointTicks, duration.Ticks);
            long observed;
            do
            {
                observed = Volatile.Read(ref _maxCheckpointMilliseconds);
                if (milliseconds <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maxCheckpointMilliseconds, milliseconds, observed) != observed);

            Volatile.Write(ref _lastCheckpointLogFrames, logFrames);
            Volatile.Write(ref _lastCheckpointedFrames, checkpointedFrames);
        }

        /// <summary>Records a membership hit.</summary>
        internal void NoteHit() => Interlocked.Increment(ref _membershipHits);

        /// <summary>Records a membership miss.</summary>
        internal void NoteMiss() => Interlocked.Increment(ref _membershipMisses);

        /// <summary>Records a cache publication failure after a commit.</summary>
        internal void NoteCachePublishFailure() => Interlocked.Increment(ref _cachePublishFailures);

        /// <summary>Records the startup membership load.</summary>
        /// <param name="rows">Hashes published into the membership index.</param>
        /// <param name="elapsed">Time spent reading hashes and inserting them.</param>
        internal void NoteStartup(long rows, TimeSpan elapsed)
        {
            Volatile.Write(ref _startupRows, rows);
            Volatile.Write(ref _startupMilliseconds, (long)elapsed.TotalMilliseconds);
        }

        /// <summary>Publishes the latest queue observation.</summary>
        /// <param name="depth">Commands waiting to be read by the writer.</param>
        /// <param name="capacity">Queue capacity.</param>
        internal void NoteQueue(int depth, int capacity)
        {
            Volatile.Write(ref _queueDepth, depth);
            Volatile.Write(ref _queueCapacity, capacity);
        }

        /// <summary>Records reconciliation totals.</summary>
        /// <param name="missing">Files inserted because they were absent from the membership index.</param>
        /// <param name="stale">Index rows whose file was absent.</param>
        internal void NoteReconciliation(long missing, long stale)
        {
            Volatile.Write(ref _reconciliationMissing, missing);
            Volatile.Write(ref _reconciliationStale, stale);
        }

        /// <summary>Records one location or eviction read.</summary>
        /// <param name="duration">Time spent in SQLite for that read.</param>
        internal void NoteRead(TimeSpan duration)
        {
            Interlocked.Increment(ref _readCount);
            Interlocked.Add(ref _readTicks, duration.Ticks);
        }
    }
}
