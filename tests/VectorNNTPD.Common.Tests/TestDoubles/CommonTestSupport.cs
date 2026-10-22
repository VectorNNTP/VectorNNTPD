using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.TestDoubles
{
    /// <summary>
    /// Creates a unique temporary directory and deletes it on dispose.
    /// </summary>
    internal sealed class TempStateDir : IDisposable
    {
        /// <summary>
        /// Directory created for one test, under the process temp path and named with a new GUID.
        /// </summary>
        /// <value>The full path of the directory this instance creates and later deletes.</value>
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "vectornntp-common-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Creates <see cref="TempStateDir.Path"/> on disk.
        /// </summary>
        public TempStateDir() => Directory.CreateDirectory(Path);

        /// <summary>
        /// Deletes <see cref="TempStateDir.Path"/> recursively when it still exists.
        /// </summary>
        /// <remarks>
        /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> from the delete are ignored.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// In-memory <see cref="ILocalIpAddressAssignee"/> with a fixed address list or an assign-all mode.
    /// </summary>
    internal sealed class FakeLocalAssignee : ILocalIpAddressAssignee
    {
        /// <summary>
        /// When <see langword="true"/>, <see cref="IsLocallyAssigned"/> accepts every non-null address.
        /// </summary>
        private readonly bool _assignAll;

        /// <summary>
        /// Addresses returned by <see cref="GetAssignedUnicastAddresses"/>.
        /// </summary>
        private readonly IPAddress[] _assigned;

        /// <summary>
        /// Stores either no addresses or the fixed pair <c>198.18.0.10</c> and <c>2001:db8::10</c>.
        /// </summary>
        /// <param name="assignAll">
        /// <see langword="true"/> to treat every non-null address as local and return the fixed pair;
        /// <see langword="false"/> to return an empty list and match nothing.
        /// </param>
        public FakeLocalAssignee(bool assignAll)
        {
            _assignAll = assignAll;
            _assigned = assignAll
                ? [IPAddress.Parse("198.18.0.10"), IPAddress.Parse("2001:db8::10")]
                : [];
        }

        /// <summary>
        /// Stores <paramref name="assigned"/> and matches only those addresses.
        /// </summary>
        /// <param name="assigned">Addresses <see cref="IsLocallyAssigned"/> accepts. Assign-all mode stays off.</param>
        public FakeLocalAssignee(params IPAddress[] assigned)
        {
            _assignAll = false;
            _assigned = assigned;
        }

        /// <summary>
        /// Returns whether <paramref name="address"/> is treated as locally assigned.
        /// </summary>
        /// <param name="address">Address to test.</param>
        /// <returns>
        /// <see langword="true"/> when this instance was constructed with assign-all, or when
        /// <paramref name="address"/> is in <see cref="_assigned"/> under <see cref="IPAddress"/> equality.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
        public bool IsLocallyAssigned(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return _assignAll || _assigned.Contains(address);
        }

        /// <summary>
        /// Returns the stored address array.
        /// </summary>
        /// <returns>The same <see cref="IPAddress"/> array stored at construction. The array is not copied.</returns>
        public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() => _assigned;
    }

    /// <summary>
    /// Builds self-signed PKCS#12 certificate material for ACME tests.
    /// </summary>
    internal static class TestCertificateFactory
    {
        /// <summary>
        /// PKCS#12 password passed to PFX export. The value is a test fixture, not a deployment secret.
        /// </summary>
        public const string Password = "unit-test-pfx-password-not-secret";

        /// <summary>
        /// Creates a self-signed leaf whose subject uses the first DNS name and whose SANs list every name.
        /// </summary>
        /// <param name="dnsNames">DNS names written as subject alternative names. The first name is the certificate subject CN.</param>
        /// <param name="notAfter">Leaf not-after instant. The certificate uses its UTC date and time.</param>
        /// <param name="notBefore">
        /// Leaf not-before instant. When <see langword="null"/>, the value is one day before the call, in UTC.
        /// </param>
        /// <returns>
        /// Password-protected PFX bytes with no intermediates, plus the DNS names lowercased and ordered,
        /// and the not-before and not-after instants used to create the leaf. The temporary key and certificate are disposed before return.
        /// </returns>
        public static CertificateMaterial CreateMaterial(
            IReadOnlyList<string> dnsNames,
            DateTimeOffset notAfter,
            DateTimeOffset? notBefore = null)
        {
            var before = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + dnsNames[0],
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                sanBuilder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
            var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], Password);
            return new CertificateMaterial(
                pfx,
                dnsNames.Select(static n => n.ToLowerInvariant()).OrderBy(static n => n).ToArray(),
                before,
                notAfter);
        }
    }

    /// <summary>
    /// In-memory <see cref="ICloudflareDnsClient"/> that filters by name and type and ignores the zone id.
    /// </summary>
    internal sealed class InMemoryCloudflareDnsClient : ICloudflareDnsClient
    {
        /// <summary>
        /// Lock taken around reads and writes of <see cref="_records"/>. Call counters are updated outside this lock.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Stored DNS records. Callers receive clones from <see cref="Clone"/>.
        /// </summary>
        private readonly List<CloudflareDnsRecord> _records = [];

        /// <summary>
        /// Next numeric suffix for a created record id. The first created id is <c>rec-1</c>.
        /// </summary>
        private int _nextId = 1;

        /// <summary>
        /// How many times <see cref="CreateRecordAsync"/> has been entered, including calls that then cancel or fail.
        /// </summary>
        /// <value>The number of create entries observed so far.</value>
        public int CreateCallCount { get; private set; }

        /// <summary>
        /// How many times <see cref="UpdateRecordAsync"/> has been entered, including calls that then cancel or fail.
        /// </summary>
        /// <value>The number of update entries observed so far.</value>
        public int UpdateCallCount { get; private set; }

        /// <summary>
        /// How many times <see cref="DeleteRecordAsync"/> has been entered, including calls that then cancel.
        /// </summary>
        /// <value>The number of delete entries observed so far.</value>
        public int DeleteCallCount { get; private set; }

        /// <summary>
        /// Optional callback awaited by <see cref="CreateRecordAsync"/> after the cancellation check and before a configured create failure.
        /// </summary>
        /// <value>The callback, or <see langword="null"/> when create does not await one.</value>
        public Func<CancellationToken, Task>? OnMutate { get; set; }

        /// <summary>
        /// Exception <see cref="CreateRecordAsync"/> throws after <see cref="OnMutate"/> when this is non-null.
        /// </summary>
        /// <value>The exception to throw from create, or <see langword="null"/> when create stores a record.</value>
        public Exception? CreateException { get; set; }

        /// <summary>
        /// Returns clones of the records currently stored.
        /// </summary>
        /// <returns>A new array of clones. The stored list is unchanged.</returns>
        public IReadOnlyList<CloudflareDnsRecord> Snapshot()
        {
            lock (_sync)
            {
                return _records.Select(Clone).ToArray();
            }
        }

        /// <summary>
        /// Replaces the stored records with clones of <paramref name="records"/>.
        /// </summary>
        /// <param name="records">Records to copy in. <see cref="_nextId"/> and the call counters are left unchanged.</param>
        /// <remarks><see cref="Clone"/> does not copy <see cref="CloudflareDnsRecord.ZoneId"/>.</remarks>
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

        /// <summary>
        /// Returns stored records whose type and normalized name match.
        /// </summary>
        /// <param name="zoneId">Accepted for the interface. This double does not filter on it.</param>
        /// <param name="fqdn">DNS name. Comparison trims, removes a trailing dot, and ignores case.</param>
        /// <param name="type">Record type compared with <see cref="StringComparison.OrdinalIgnoreCase"/>.</param>
        /// <param name="cancellationToken">Checked before the stored list is read.</param>
        /// <returns>Clones of the matching records, or an empty array when none match.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string zoneId,
            string fqdn,
            string type,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
                return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(
                    _records
                        .Where(r =>
                            string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                        .Select(Clone)
                        .ToArray());
            }
        }

        /// <summary>
        /// Returns every stored record whose normalized name matches, regardless of type.
        /// </summary>
        /// <param name="zoneId">Accepted for the interface. This double does not filter on it.</param>
        /// <param name="fqdn">DNS name. Comparison trims, removes a trailing dot, and ignores case.</param>
        /// <param name="cancellationToken">Checked before the stored list is read.</param>
        /// <returns>Clones of the matching records, or an empty array when none match.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
                return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(
                    _records
                        .Where(r => string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                        .Select(Clone)
                        .ToArray());
            }
        }

        /// <summary>
        /// Appends a record copied from <paramref name="request"/>, or fails when a create fault is configured.
        /// </summary>
        /// <param name="zoneId">Accepted for the interface. The stored record does not receive it.</param>
        /// <param name="request">Type, name, content, TTL, and proxied flag copied onto the new record.</param>
        /// <param name="cancellationToken">Checked after <see cref="CreateCallCount"/> increments. Also passed to <see cref="OnMutate"/>.</param>
        /// <returns>A clone of the stored record. Its id is <c>rec-</c> plus the next number, starting at 1.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the callback or insert.</exception>
        /// <exception cref="Exception"><see cref="CreateException"/> is non-null after <see cref="OnMutate"/> completes.</exception>
        /// <remarks>
        /// When <see cref="OnMutate"/> is set, it is awaited before the create fault is thrown. An exception from that callback propagates.
        /// The call count increments even when the method then cancels or throws.
        /// </remarks>
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

            if (CreateException is not null)
            {
                throw CreateException;
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
                return Clone(record);
            }
        }

        /// <summary>
        /// Overwrites the type, name, content, TTL, and proxied flag of the stored record with <paramref name="recordId"/>.
        /// </summary>
        /// <param name="zoneId">Accepted for the interface. It is not written onto the record.</param>
        /// <param name="recordId">Identifier compared to <see cref="CloudflareDnsRecord.Id"/>.</param>
        /// <param name="request">Replacement type, name, content, TTL, and proxied flag. The stored id is kept.</param>
        /// <param name="cancellationToken">Checked after <see cref="UpdateCallCount"/> increments and before the list is updated.</param>
        /// <returns>A clone of the updated stored record.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        /// <exception cref="InvalidOperationException">No stored record has <paramref name="recordId"/>.</exception>
        /// <remarks>This method does not call <see cref="OnMutate"/> and does not throw <see cref="CreateException"/>.</remarks>
        public Task<CloudflareDnsRecord> UpdateRecordAsync(
            string zoneId,
            string recordId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var existing = _records.First(r => r.Id == recordId);
                existing.Type = request.Type;
                existing.Name = request.Name;
                existing.Content = request.Content;
                existing.Ttl = request.Ttl;
                existing.Proxied = request.Proxied;
                return Task.FromResult(Clone(existing));
            }
        }

        /// <summary>
        /// Removes every stored record whose id equals <paramref name="recordId"/>.
        /// </summary>
        /// <param name="zoneId">Accepted for the interface. It is not used to select records.</param>
        /// <param name="recordId">Identifier compared to <see cref="CloudflareDnsRecord.Id"/>. A missing id removes nothing.</param>
        /// <param name="cancellationToken">Checked after <see cref="DeleteCallCount"/> increments and before the list is changed.</param>
        /// <returns>A completed task after the removal.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken)
        {
            DeleteCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _records.RemoveAll(r => r.Id == recordId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Copies the id, type, name, content, TTL, and proxied flag into a new record.
        /// </summary>
        /// <param name="record">Source record. It is not retained.</param>
        /// <returns>A new record. <see cref="CloudflareDnsRecord.ZoneId"/> is left unset.</returns>
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

    /// <summary>
    /// In-memory <see cref="IAuthoritativeTxtResolver"/> that stores one TXT string per name.
    /// </summary>
    internal sealed class ImmediateTxtResolver : IAuthoritativeTxtResolver
    {
        /// <summary>
        /// TXT values keyed by owner name using <see cref="StringComparer.OrdinalIgnoreCase"/>.
        /// </summary>
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores <paramref name="value"/> for <paramref name="name"/>, replacing any previous value for that name.
        /// </summary>
        /// <param name="name">TXT owner name. Lookup uses ordinal case-insensitive comparison.</param>
        /// <param name="value">Single TXT string returned for <paramref name="name"/>.</param>
        public void Set(string name, string value) => _values[name] = value;

        /// <summary>
        /// Returns the one stored TXT string for <paramref name="name"/>, or an empty list when the name is absent.
        /// </summary>
        /// <param name="name">TXT owner name compared with <see cref="StringComparer.OrdinalIgnoreCase"/>.</param>
        /// <param name="cancellationToken">Checked before the dictionary is read.</param>
        /// <returns>A one-element list, or an empty list when <paramref name="name"/> was not stored.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already canceled.</exception>
        public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(
                _values.TryGetValue(name, out var value) ? [value] : []);
        }
    }
}
