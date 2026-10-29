namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// Serializes tests that configure the process-wide <c>NntpCommandLoggers</c> factory
/// so recording loggers are not cleared by concurrent catalog creation.
/// </summary>
[CollectionDefinition(nameof(NntpCommandLoggerCollection), DisableParallelization = true)]
public sealed class NntpCommandLoggerCollection;
