using VectorNNTPD.Common.Cloudflare;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>
    /// In-memory Cloudflare DNS client for offline tests. Never performs network I/O.
    /// </summary>
    internal sealed class FakeCloudflareDnsClient : ICloudflareDnsClient
    {
        private readonly object _sync = new();
        private readonly List<CloudflareDnsRecord> _records = [];
        private int _nextId = 1;

        public int ListCallCount { get; private set; }
        public int ListAllCallCount { get; private set; }
        public int CreateCallCount { get; private set; }
        public int UpdateCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }

        public Func<CancellationToken, Task>? OnMutate { get; set; }
        public Exception? ListException { get; set; }
        public Exception? ListAllException { get; set; }
        public Exception? CreateException { get; set; }
        public Exception? UpdateException { get; set; }
        public Exception? DeleteException { get; set; }
        public Exception? FailListAfterMutationsException { get; set; }
        public int MutationsBeforeListFailure { get; set; } = int.MaxValue;

        /// <summary>
        /// How many delete calls should fail when <see cref="DeleteException"/> is set.
        /// Use <c>-1</c> (default) for unlimited failures; a positive count fails that many times then succeeds.
        /// </summary>
        public int DeleteFailRemaining { get; set; } = -1;

        /// <summary>
        /// When true, a failing delete still removes the record then throws (timeout after apply).
        /// </summary>
        public bool ApplyDeleteThenFailUncertain { get; set; }

        /// <summary>When set, create fails only for this DNS record type (e.g. <c>AAAA</c>).</summary>
        public string? CreateFailType { get; set; }

        /// <summary>When set with <see cref="CreateFailType"/>, throws this instead of <see cref="CreateException"/>.</summary>
        public Exception? CreateFailTypeException { get; set; }

        /// <summary>
        /// How many matching <see cref="CreateFailType"/> creates should fail before succeeding.
        /// Use <see cref="int.MaxValue"/> for permanent failure.
        /// </summary>
        public int CreateFailTypeRemaining { get; set; } = int.MaxValue;

        /// <summary>
        /// When true, a failing create for <see cref="CreateFailType"/> still mutates the store, then throws
        /// (simulates timeout after Cloudflare applied the change).
        /// </summary>
        public bool ApplyCreateThenFailUncertain { get; set; }

        public int SuccessfulCreateCount { get; private set; }
        public int SuccessfulDeleteCount { get; private set; }

        /// <summary>Ordered mutation labels such as <c>Create:A</c> or <c>Delete</c> for successful mutations.</summary>
        public List<string> MutationOrder { get; } = [];

        /// <summary>
        /// Optional transform applied to typed list results (e.g. inject an external record during verification).
        /// Arguments: record type, listed records, current list call count.
        /// </summary>
        public Func<string, IReadOnlyList<CloudflareDnsRecord>, int, IReadOnlyList<CloudflareDnsRecord>>?
            TransformListedRecords
        { get; set; }

        /// <summary>
        /// Optional transform applied to <see cref="ListAllRecordsForNameAsync"/> results.
        /// Arguments: listed records, current list-all call count.
        /// </summary>
        public Func<IReadOnlyList<CloudflareDnsRecord>, int, IReadOnlyList<CloudflareDnsRecord>>?
            TransformListedAllRecords
        { get; set; }

        public IReadOnlyList<CloudflareDnsRecord> Snapshot()
        {
            lock (_sync)
            {
                return _records.Select(Clone).ToArray();
            }
        }

        public void Seed(params CloudflareDnsRecord[] records)
        {
            lock (_sync)
            {
                _records.Clear();
                foreach (var record in records)
                {
                    _records.Add(Clone(record));
                }
            }
        }

        /// <summary>Adds a record without clearing existing ones (simulates an external DNS actor).</summary>
        public void Inject(CloudflareDnsRecord record)
        {
            lock (_sync)
            {
                _records.Add(Clone(record));
            }
        }

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string zoneId,
            string fqdn,
            string type,
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (ListException is not null)
            {
                throw ListException;
            }

            if (CreateCallCount + UpdateCallCount + DeleteCallCount >= MutationsBeforeListFailure
                && FailListAfterMutationsException is not null)
            {
                throw FailListAfterMutationsException;
            }

            IReadOnlyList<CloudflareDnsRecord> matches;
            lock (_sync)
            {
                var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
                matches = _records
                    .Where(r =>
                        string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .ToArray();
            }

            if (TransformListedRecords is not null)
            {
                matches = TransformListedRecords(type, matches, ListCallCount);
            }

            return Task.FromResult(matches);
        }

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken)
        {
            ListAllCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (ListAllException is not null)
            {
                throw ListAllException;
            }

            if (ListException is not null)
            {
                throw ListException;
            }

            IReadOnlyList<CloudflareDnsRecord> matches;
            lock (_sync)
            {
                var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
                matches = _records
                    .Where(r =>
                        string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .ToArray();
            }

            if (TransformListedAllRecords is not null)
            {
                matches = TransformListedAllRecords(matches, ListAllCallCount);
            }

            return Task.FromResult(matches);
        }

        public async Task<CloudflareDnsRecord> CreateRecordAsync(
            string zoneId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (OnMutate is not null)
            {
                await OnMutate(cancellationToken).ConfigureAwait(false);
            }

            var failForThisType = CreateFailType is not null
                && CreateFailTypeRemaining > 0
                && string.Equals(request.Type, CreateFailType, StringComparison.OrdinalIgnoreCase);
            var failEx = failForThisType
                ? CreateFailTypeException ?? new CloudflareDnsException($"create {request.Type} failed")
                : CreateFailType is null
                    ? CreateException
                    : null;

            if (failEx is not null)
            {
                if (failForThisType)
                {
                    CreateFailTypeRemaining--;
                }

                if (ApplyCreateThenFailUncertain && failForThisType)
                {
                    lock (_sync)
                    {
                        _records.Add(new CloudflareDnsRecord
                        {
                            Id = $"rec-{_nextId++}",
                            Type = request.Type,
                            Name = request.Name,
                            Content = request.Content,
                            Ttl = request.Ttl,
                            Proxied = request.Proxied,
                        });
                        SuccessfulCreateCount++;
                        MutationOrder.Add($"Create:{request.Type}");
                    }
                }

                throw failEx;
            }

            lock (_sync)
            {
                var record = new CloudflareDnsRecord
                {
                    Id = $"rec-{_nextId++}",
                    Type = request.Type,
                    Name = request.Name,
                    Content = request.Content,
                    Ttl = request.Ttl,
                    Proxied = request.Proxied,
                };
                _records.Add(record);
                SuccessfulCreateCount++;
                MutationOrder.Add($"Create:{request.Type}");
                return Clone(record);
            }
        }

        public async Task<CloudflareDnsRecord> UpdateRecordAsync(
            string zoneId,
            string recordId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (OnMutate is not null)
            {
                await OnMutate(cancellationToken).ConfigureAwait(false);
            }

            if (UpdateException is not null)
            {
                throw UpdateException;
            }

            lock (_sync)
            {
                var existing = _records.FirstOrDefault(r => r.Id == recordId)
                    ?? throw new CloudflareDnsException($"Record '{recordId}' was not found.");
                existing.Type = request.Type;
                existing.Name = request.Name;
                existing.Content = request.Content;
                existing.Ttl = request.Ttl;
                existing.Proxied = request.Proxied;
                MutationOrder.Add($"Update:{request.Type}");
                return Clone(existing);
            }
        }

        public async Task DeleteRecordAsync(
            string zoneId,
            string recordId,
            CancellationToken cancellationToken)
        {
            DeleteCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (OnMutate is not null)
            {
                await OnMutate(cancellationToken).ConfigureAwait(false);
            }

            if (DeleteException is not null && DeleteFailRemaining != 0)
            {
                if (DeleteFailRemaining > 0)
                {
                    DeleteFailRemaining--;
                }

                if (ApplyDeleteThenFailUncertain)
                {
                    lock (_sync)
                    {
                        _records.RemoveAll(r => r.Id == recordId);
                        SuccessfulDeleteCount++;
                        MutationOrder.Add("Delete");
                    }
                }

                throw DeleteException;
            }

            lock (_sync)
            {
                _records.RemoveAll(r => r.Id == recordId);
                SuccessfulDeleteCount++;
                MutationOrder.Add("Delete");
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static CloudflareDnsRecord Clone(CloudflareDnsRecord record) =>
            new()
            {
                Id = record.Id,
                Type = record.Type,
                Name = record.Name,
                Content = record.Content,
                Ttl = record.Ttl,
                Proxied = record.Proxied,
            };
    }
}
