namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// In-process persistence counters. They are the metrics surface for this phase.
    /// StorageServer does not register a separate meter pipeline.
    /// </summary>
    internal sealed class SpoolPersistenceCounters
    {
        private long _stored;
        private long _duplicates;
        private long _conflicts;
        private long _diskFull;
        private long _accessDenied;
        private long _ioFailures;
        private long _deferred;
        private long _storedBytes;
        private long _workerFaults;

        /// <summary>Articles whose rename completed.</summary>
        internal long Stored => Volatile.Read(ref _stored);

        /// <summary>Articles that matched an existing file.</summary>
        internal long Duplicates => Volatile.Read(ref _duplicates);

        /// <summary>Articles that conflicted with an existing file.</summary>
        internal long Conflicts => Volatile.Read(ref _conflicts);

        /// <summary>Publishes that failed because the volume was full.</summary>
        internal long DiskFull => Volatile.Read(ref _diskFull);

        /// <summary>Publishes that failed because access was denied.</summary>
        internal long AccessDenied => Volatile.Read(ref _accessDenied);

        /// <summary>Other publish I/O failures.</summary>
        internal long IoFailures => Volatile.Read(ref _ioFailures);

        /// <summary>Selection attempts that found no eligible spool or no in-flight slot.</summary>
        internal long Deferred => Volatile.Read(ref _deferred);

        /// <summary>Bytes renamed into a final path. Duplicates are not included.</summary>
        internal long StoredBytes => Volatile.Read(ref _storedBytes);

        /// <summary>Worker loops that exited because of an unexpected exception.</summary>
        internal long WorkerFaults => Volatile.Read(ref _workerFaults);

        /// <summary>Records a completed rename.</summary>
        /// <param name="bytes">Length of the published article.</param>
        internal void NoteStored(long bytes)
        {
            Interlocked.Increment(ref _stored);
            Interlocked.Add(ref _storedBytes, bytes);
        }

        /// <summary>Records a matching duplicate.</summary>
        internal void NoteDuplicate() => Interlocked.Increment(ref _duplicates);

        /// <summary>Records a conflicting duplicate.</summary>
        internal void NoteConflict() => Interlocked.Increment(ref _conflicts);

        /// <summary>Records a full volume.</summary>
        internal void NoteDiskFull() => Interlocked.Increment(ref _diskFull);

        /// <summary>Records an access denial.</summary>
        internal void NoteAccessDenied() => Interlocked.Increment(ref _accessDenied);

        /// <summary>Records a general I/O failure.</summary>
        internal void NoteIoFailure() => Interlocked.Increment(ref _ioFailures);

        /// <summary>Records a deferred article.</summary>
        internal void NoteDeferred() => Interlocked.Increment(ref _deferred);

        /// <summary>Records an unexpected worker fault.</summary>
        internal void NoteWorkerFault() => Interlocked.Increment(ref _workerFaults);
    }
}
