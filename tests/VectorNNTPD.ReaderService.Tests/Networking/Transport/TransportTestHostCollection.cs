namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Serializes tests that bind ephemeral loopback sockets via <see cref="TransportTestHost"/>.
/// </summary>
[CollectionDefinition(nameof(TransportTestHostCollection), DisableParallelization = true)]
public sealed class TransportTestHostCollection;
