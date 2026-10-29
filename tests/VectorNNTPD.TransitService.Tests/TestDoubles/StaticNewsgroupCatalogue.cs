using VectorNNTPD.TransitService.Newsgroups;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>Test catalogue that publishes snapshots without MySQL.</summary>
internal sealed class StaticNewsgroupCatalogue : INewsgroupCatalogue
{
    private NewsgroupSnapshot _current;

    public StaticNewsgroupCatalogue(NewsgroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = snapshot;
    }

    public int CurrentReadCount { get; private set; }

    public NewsgroupSnapshot Current
    {
        get
        {
            CurrentReadCount++;
            return Volatile.Read(ref _current);
        }
    }

    public void Publish(NewsgroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref _current, snapshot);
    }
}
