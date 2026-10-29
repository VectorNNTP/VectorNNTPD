using System.IO.Pipelines;
using System.Net;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.SessionState.RateLimiting;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>
    /// Byte-oriented NNTP connection transport exposed to a future session/protocol layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Input"/> carries octets received from the peer (after TLS decryption when applicable).
    /// <see cref="Output"/> carries octets to send to the peer (before TLS encryption when applicable).
    /// </para>
    /// <para>
    /// No NNTP greeting or command semantics are implemented. The transport does not keep an unused
    /// connection alive beyond normal TCP/TLS lifetime and pipeline backpressure.
    /// </para>
    /// <para>
    /// <see cref="UpgradeToTlsAsync"/> upgrades an established plaintext connection to TLS on the same
    /// TCP socket. <see cref="UpgradeToDeflateAsync"/> activates bidirectional raw DEFLATE (RFC 8054)
    /// above the current byte stream (plain or TLS). The NNTP STARTTLS and COMPRESS commands live in the
    /// session layer and invoke these transport upgrades when appropriate.
    /// </para>
    /// </remarks>
    public interface INntpConnection : IAsyncDisposable
    {
        /// <summary>
        /// Wire-level outbound rate cap, or <see langword="null"/> when the connection
        /// does not expose a limiter (test doubles).
        /// </summary>
        IOutboundRateCap? OutboundRate => null;

        /// <summary>Gets octets received from the peer (after TLS decryption / DEFLATE inflate when applicable).</summary>
        PipeReader Input { get; }

        /// <summary>Gets the writer used by the application to send octets to the network.</summary>
        PipeWriter Output { get; }

        /// <summary>Gets the remote TCP peer endpoint when known.</summary>
        EndPoint? RemoteEndPoint { get; }

        /// <summary>Gets the local endpoint when known.</summary>
        EndPoint? LocalEndPoint { get; }

        /// <summary>
        /// Gets the immutable effective client identity established at connection initialization.
        /// </summary>
        ConnectionClientIdentity ClientIdentity { get; }

        /// <summary>Gets a value indicating whether the connection is TLS-protected.</summary>
        bool IsTls { get; }

        /// <summary>
        /// When TLS is active, returns the negotiated protocol and cipher suite labels for logging.
        /// </summary>
        /// <param name="tlsVersion">Operational TLS version label (for example <c>TLSv1.3</c>).</param>
        /// <param name="cipher">Negotiated cipher suite name from <see cref="System.Net.Security.SslStream"/>.</param>
        /// <returns><see langword="true"/> when TLS parameters are available.</returns>
        bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher);

        /// <summary>Gets a value indicating whether bidirectional raw DEFLATE is active.</summary>
        bool IsCompressed { get; }

        /// <summary>Gets a token that is cancelled when the connection transport is shutting down.</summary>
        CancellationToken ConnectionClosed { get; }

        /// <summary>
        /// Gets a value indicating whether <see cref="CompleteAsync"/> has been entered (transport is terminal).
        /// </summary>
        bool IsCompleted { get; }

        /// <summary>
        /// Completes the transport (stops pumps, closes the socket). Safe to call multiple times.
        /// </summary>
        /// <param name="exception">Optional exception indicating abortive completion.</param>
        Task CompleteAsync(Exception? exception = null);

        /// <summary>
        /// Gets a monotonic generation bumped each time the send pump becomes idle (awaiting more Output).
        /// </summary>
        /// <remarks>
        /// For STARTTLS: <see cref="PauseReadsAsync"/> first, capture this version, write <c>382</c>, then
        /// <see cref="WaitForOutboundDeliveryAsync"/> until the generation advances.
        /// </remarks>
        long OutboundIdleVersion { get; }

        /// <summary>
        /// Pauses application socket reads while still allowing writes (STARTTLS handoff ownership).
        /// </summary>
        /// <remarks>
        /// After this returns, successful receive completions are retained for the TLS upgrade prefix
        /// instead of being committed to <see cref="Input"/>. Writes remain admitted so a <c>382</c>
        /// response can still reach the peer. Must be called <em>before</em> writing <c>382</c>.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task PauseReadsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Waits until the send pump has become idle after <paramref name="outboundIdleVersionBeforeFlush"/>.
        /// </summary>
        /// <param name="outboundIdleVersionBeforeFlush">
        /// <see cref="OutboundIdleVersion"/> captured before flushing pre-upgrade plaintext to <see cref="Output"/>.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Pauses application reads, then waits until the send pump has become idle after
        /// <paramref name="outboundIdleVersionBeforeFlush"/>.
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="PauseReadsAsync"/> before writing <c>382</c>, then
        /// <see cref="WaitForOutboundDeliveryAsync"/>. This combined helper is for callers that already
        /// wrote the pre-upgrade response under an existing pause, or that pause immediately after write
        /// when no peer ClientHello can race (test helpers). STARTTLS must pause <em>before</em> <c>382</c>.
        /// </remarks>
        Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Performs an in-place server TLS handshake on the existing TCP socket.
        /// </summary>
        /// <param name="certificateProvider">Provider used to acquire a connection-lifetime certificate lease.</param>
        /// <param name="cancellationToken">Token that cancels the handshake (and aborts the connection on failure).</param>
        /// <remarks>
        /// <para>
        /// Preconditions: the connection must be plaintext and not DEFLATE-compressed; the caller must not
        /// hold outstanding <see cref="Input"/> reads or <see cref="Output"/> writes; any plaintext that must
        /// be visible to the peer before TLS (for example a STARTTLS <c>382</c> response) must already be
        /// flushed to <see cref="Output"/> (STARTTLS pauses reads <em>before</em> writing <c>382</c>).
        /// The upgrade quiesces writes before inspecting <see cref="Input"/>; any application plaintext
        /// that still remains in <see cref="Input"/> is a precondition failure (connection stays plaintext).
        /// Pipelined NNTP after STARTTLS must be discarded by the session (RFC 8143) before calling this
        /// method. Reads may already be paused by <see cref="PauseReadsAsync"/>.
        /// </para>
        /// <para>
        /// On success, subsequent <see cref="Input"/>/<see cref="Output"/> traffic is TLS application data
        /// on the same socket. <see cref="ClientIdentity"/> is unchanged. On failure, the connection is
        /// completed and does not fall back to plaintext.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Already TLS, already compressed, upgrade in progress, or unconsumed plaintext remains in
        /// <see cref="Input"/> (precondition failure does not complete the connection).
        /// </exception>
        /// <exception cref="ObjectDisposedException">The connection is closed or disposed.</exception>
        Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Activates bidirectional raw DEFLATE on the existing connection byte stream.
        /// </summary>
        /// <param name="cancellationToken">Token that cancels activation (and aborts the connection on failure).</param>
        /// <remarks>
        /// <para>
        /// Layering (RFC 8054): DEFLATE sits above TLS when both are active —
        /// <c>NNTP → DEFLATE → TLS → TCP</c>. TLS must be negotiated first when both are used; this method
        /// rejects activation when DEFLATE is already active, and <see cref="UpgradeToTlsAsync"/> rejects
        /// TLS after DEFLATE.
        /// </para>
        /// <para>
        /// Preconditions: DEFLATE not already active; no outstanding <see cref="Input"/> reads /
        /// <see cref="Output"/> writes; application data that belongs before compression must already be
        /// consumed from <see cref="Input"/>; any bytes that must reach the peer uncompressed (for example
        /// a future <c>206</c> COMPRESS response) must already be flushed to <see cref="Output"/>.
        /// </para>
        /// <para>
        /// On success, subsequent pipe traffic is compressed in both directions on the same socket and
        /// pipes. <see cref="ClientIdentity"/> is unchanged. Post-quiescence failure completes the
        /// connection with no uncompressed fallback.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Already compressed, upgrade in progress, or unconsumed application data remains in
        /// <see cref="Input"/> (precondition failure does not complete the connection).
        /// </exception>
        /// <exception cref="ObjectDisposedException">The connection is closed or disposed.</exception>
        Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default);
    }
}
