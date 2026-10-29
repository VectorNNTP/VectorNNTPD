namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>
/// Serializes tests that mutate the process-wide <c>Serilog.Log.Logger</c> static.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerilogCollection
{
    public const string Name = "Serilog";
}
