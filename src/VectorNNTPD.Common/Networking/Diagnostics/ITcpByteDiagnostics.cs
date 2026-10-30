namespace VectorNNTPD.Common.Networking.Diagnostics
{
    /// <summary>
    /// Optional TCP byte observability for duplex pipe connections.
    /// </summary>
    public interface ITcpByteDiagnostics
    {
        /// <summary>Gets whether probes should record bytes.</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Adds application-layer bytes read from an upstream connection (plain TCP payload,
        /// or TLS-decrypted octets). No-op when disabled.
        /// </summary>
        /// <param name="bytes">Byte count to record.</param>
        void RecordTcpBytes(int bytes);
    }
}
