using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>Publishes a matrix policy snapshot for POST-boundary scenarios.</summary>
internal interface IPostFilterMatrixPolicy
{
    /// <summary>Gets how many times the live MySQL repository was loaded.</summary>
    int RepositoryLoadCount { get; }

    /// <summary>Gets whether published sources are <see cref="PostFilterPolicyService"/>.</summary>
    bool UsesPolicyService { get; }

    Task<IPostFilterPolicySource> PublishDisabledAsync();

    Task<IPostFilterPolicySource> PublishAsync(PostFilterOptions options);
}

/// <summary>Existing in-memory compile path used by the offline matrix.</summary>
internal sealed class InMemoryPostFilterMatrixPolicy : IPostFilterMatrixPolicy
{
    public int RepositoryLoadCount { get; private set; }

    public bool UsesPolicyService => false;

    public Task<IPostFilterPolicySource> PublishDisabledAsync() =>
        Task.FromResult<IPostFilterPolicySource>(
            new StaticPostFilterPolicySource(PostFilterPolicySnapshot.Disabled));

    public Task<IPostFilterPolicySource> PublishAsync(PostFilterOptions options)
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(options),
        };
        var record = repository.LoadAsync().AsTask().GetAwaiter().GetResult();
        RepositoryLoadCount += repository.LoadCount;
        var snapshot = PostFilterPolicyCompiler.Compile(record.Options, record.Revision);
        return Task.FromResult<IPostFilterPolicySource>(new StaticPostFilterPolicySource(snapshot));
    }
}

/// <summary>
/// Persists each matrix policy as a new NntpDB revision, then publishes it
/// through <see cref="MySqlPostFilterPolicyRepository"/> and
/// <see cref="PostFilterPolicyService"/>.
/// </summary>
internal sealed class MySqlPostFilterMatrixPolicy : IPostFilterMatrixPolicy, IAsyncDisposable
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;
    private readonly CountingPostFilterPolicyRepository _repository;
    private readonly List<PostFilterPolicyService> _services = [];

    public MySqlPostFilterMatrixPolicy(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Repository is null)
        {
            throw new InvalidOperationException("PostFilter MySQL fixture is not configured.");
        }

        _fixture = fixture;
        _repository = new CountingPostFilterPolicyRepository(fixture.Repository);
    }

    public int RepositoryLoadCount => _repository.LoadCount;

    public bool UsesPolicyService => true;

    public Task<IPostFilterPolicySource> PublishDisabledAsync() =>
        PublishAsync(new PostFilterOptions { Gate = PostFilterGateState.Disabled });

    public async Task<IPostFilterPolicySource> PublishAsync(PostFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var revision = _fixture.NextRevision();
        await _fixture.PublishOptionsAsync(revision, options);
        var service = new PostFilterPolicyService(
            _repository,
            NullLogger<PostFilterPolicyService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);
        _services.Add(service);
        Assert.Equal(revision, service.Current.Revision);
        Assert.Equal(options.Gate, service.Current.Gate);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
        }

        _services.Clear();
    }

    private sealed class CountingPostFilterPolicyRepository : IPostFilterPolicyRepository
    {
        private readonly IPostFilterPolicyRepository _inner;

        public CountingPostFilterPolicyRepository(IPostFilterPolicyRepository inner)
        {
            _inner = inner;
        }

        public int LoadCount { get; private set; }

        public ValueTask<PostFilterPolicyRecord> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            Assert.IsType<MySqlPostFilterPolicyRepository>(_inner);
            return _inner.LoadAsync(cancellationToken);
        }
    }
}
