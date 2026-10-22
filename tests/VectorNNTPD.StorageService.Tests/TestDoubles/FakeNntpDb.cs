using MySqlConnector;
using VectorNNTPD.Common.NntpDb;

namespace VectorNNTPD.StorageService.Tests.TestDoubles
{

    /// <summary>
    /// Opens an in-memory NntpDB session that returns one <c>nntpsharedconfig</c> row.
    /// </summary>
    internal sealed class FakeNntpDbConnectionFactory : INntpDbConnectionFactory
    {
        internal const string ConnectionString =
            "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz";

        public FakeNntpDbConnectionFactory()
            : this(SharedRow())
        {
        }

        public FakeNntpDbConnectionFactory(NntpSharedConfigurationCandidate row) => Row = row;

        public NntpSharedConfigurationCandidate Row { get; set; }

        public int OpenCount { get; private set; }

        public Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<INntpDbSession>(new FakeNntpDbSession(this));
        }

        internal static NntpSharedConfigurationCandidate SharedRow(
            string dnsSuffix = "usenet.ninja",
            string zoneId = "0123456789abcdef0123456789abcdef",
            string directoryUrl = "https://acme-staging-v02.api.letsencrypt.org/directory") =>
            new(
                MaxArticleBytes: 0,
                SiteName: null,
                PrometheusUrl: "http://unused.example/metrics",
                AcmeDirectoryUrl: directoryUrl,
                AcmeRenewalThresholdDays: 21,
                CloudFlareZoneId: zoneId,
                DnsSuffix: dnsSuffix,
                AcmeAccount: NntpSharedConfigurationColumns.PlaceholderAcmeAccount,
                AcmeCertificatePassword: NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword,
                CloudFlareApiKey: NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey,
                RabbitMqPassword: NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword,
                RabbitMqUsername: NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername);
    }

    internal sealed class FakeNntpDbSession : INntpDbSession, INntpSharedConfigurationRowSource
    {
        private readonly FakeNntpDbConnectionFactory _factory;

        public FakeNntpDbSession(FakeNntpDbConnectionFactory factory) => _factory = factory;

        public ValueTask<int> SelectOneAsync(CancellationToken cancellationToken) => new(1);

        public ValueTask<IReadOnlyList<NntpSharedConfigurationCandidate>> ReadCandidatesAsync(
            CancellationToken cancellationToken) =>
            new(new[] { _factory.Row });

        public MySqlCommand CreateCommand() =>
            throw new NotSupportedException("The fake session does not execute SQL.");

        public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fake session does not begin transactions.");

        public ValueTask<MySqlTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fake session does not begin transactions.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
