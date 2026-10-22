using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Application service that ensures ACME account/certificate readiness when TLS is enabled,
    /// and periodically renews certificates while running.
    /// </summary>
    /// <remarks>
    /// When <see cref="AcmeCloudflareOptions.IsTlsListenerEnabled"/> is <see langword="false"/>, start is a no-op
    /// and no ACME network calls are made. Shutdown does not contact Let's Encrypt.
    /// After a usable PFX is ensured or renewed, an immutable TLS certificate context is published
    /// for the TLS listener (atomic swap; existing connections are unaffected).
    /// </remarks>
    public sealed class AcmeCertificateService : IAsyncDisposable
    {
        /// <summary>Background renewal check interval (default 6 hours).</summary>
        private static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromHours(6);

        /// <summary>Options read at start and when publishing a renewed PFX.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Creates the certificate manager only when TLS is enabled.</summary>
        private readonly AcmeComponentFactory _factory;

        /// <summary>Receives the PFX after a successful ensure or renewal.</summary>
        private readonly IAcmeCertificatePublisher _certificatePublisher;

        /// <summary>Set after the startup PFX is published. Not set again on later renewals.</summary>
        private readonly IAcmeCertificateReadiness _readiness;

        /// <summary>Logger for idle, provisioning, and renewal events.</summary>
        private readonly ILogger<AcmeCertificateService> _logger;

        /// <summary>Clock for the renewal delay and due checks. Production uses <see cref="TimeProvider.System"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>Optional application observer. Null skips watchdog notification.</summary>
        private readonly IAcmeCertificateObservation? _observation;

        /// <summary>Cancels <see cref="RunRenewalLoopAsync"/>. Disposed by <see cref="DisposeAsync"/>.</summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Completed renewal checks, including failed checks. Shutdown cancellation is excluded.</summary>
        private int _renewalChecksCompleted;

        /// <summary>How many renewal delays the loop has armed.</summary>
        private int _renewalWaitsStarted;

        /// <summary>Faults the next renewal iterations should throw before contacting the manager. Zero in production.</summary>
        private int _renewalFaultsToInject;

        /// <summary>
        /// When set by tests, completed after the next renewal check finishes (success or contained failure).
        /// Cleared when signaled so later checks do not complete a stale waiter.
        /// </summary>
        private TaskCompletionSource? _nextRenewalCheckSignal;

        /// <summary>Manager created during a TLS-enabled start. <see langword="null"/> when TLS is disabled or start has not created it.</summary>
        private CertificateManager? _manager;

        /// <summary>Renewal loop task. <see langword="null"/> until a TLS-enabled start publishes a certificate.</summary>
        private Task? _execution;

        /// <summary>Non-zero after the first <see cref="StartAsync"/> call. Later calls return immediately.</summary>
        private int _started;

        /// <summary>Non-zero after <see cref="DisposeAsync"/> has begun.</summary>
        private int _disposed;

        /// <summary>Stores the options, factory, publisher, readiness gate, and logger. Does not contact the CA.</summary>
        /// <param name="options">ACME options, including whether the TLS listener is enabled.</param>
        /// <param name="factory">Builds the certificate manager when TLS is enabled.</param>
        /// <param name="certificatePublisher">Publishes PFX bytes for the TLS listener.</param>
        /// <param name="readiness">Gate set after the first successful publish.</param>
        /// <param name="logger">Logger for this service.</param>
        /// <param name="time">Clock for renewal delays and due checks. Null uses <see cref="TimeProvider.System"/>.</param>
        /// <param name="observation">Optional certificate observer. Null leaves application health unchanged.</param>
        internal AcmeCertificateService(
            IOptions<AcmeCloudflareOptions> options,
            AcmeComponentFactory factory,
            IAcmeCertificatePublisher certificatePublisher,
            IAcmeCertificateReadiness readiness,
            ILogger<AcmeCertificateService> logger,
            TimeProvider? time = null,
            IAcmeCertificateObservation? observation = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(certificatePublisher);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _factory = factory;
            _certificatePublisher = certificatePublisher;
            _readiness = readiness;
            _logger = logger;
            _time = time ?? TimeProvider.System;
            _observation = observation;
        }

        /// <summary>Completed renewal checks, including failed checks that notified the observer. Shutdown cancellation is excluded.</summary>
        internal int RenewalChecksCompleted => Volatile.Read(ref _renewalChecksCompleted);

        /// <summary>How many renewal delays the loop has armed.</summary>
        internal int RenewalWaitsStarted => Volatile.Read(ref _renewalWaitsStarted);

        /// <summary>Faults the next renewal iterations should throw before contacting the manager. Zero in production.</summary>
        internal int RenewalFaultsToInject
        {
            get => Volatile.Read(ref _renewalFaultsToInject);
            set => Volatile.Write(ref _renewalFaultsToInject, value);
        }

        /// <summary>
        /// Arms <paramref name="signal"/> to complete when the next renewal check finishes.
        /// Replaces any previously armed signal without completing it.
        /// </summary>
        /// <param name="signal">Completion source observed by tests after advancing the renewal clock.</param>
        internal void NotifyOnNextRenewalCheckForTests(TaskCompletionSource signal)
        {
            ArgumentNullException.ThrowIfNull(signal);
            Volatile.Write(ref _nextRenewalCheckSignal, signal);
        }

        /// <summary>Stable service name <c>AcmeCertificate</c> read by the host application-service adapter.</summary>
        internal static string Name => "AcmeCertificate";

        /// <summary>
        /// Renewal-loop task after a successful TLS-enabled start.
        /// <see langword="null"/> when TLS is disabled, start has not finished, or start failed before the loop was assigned.
        /// </summary>
        internal Task? Execution => _execution;

        /// <summary>Gets whether the service initialized ACME components (for tests).</summary>
        internal bool AcmeInitialized => _manager is not null;

        /// <summary>
        /// When TLS is disabled, logs and returns without creating ACME state.
        /// Otherwise ensures a certificate, publishes the PFX, marks readiness, and starts the renewal loop.
        /// A second call returns without doing that work again.
        /// </summary>
        /// <param name="cancellationToken">Cancels certificate ensure. Cancellation is propagated. Other failures are logged and rethrown.</param>
        /// <returns>A task that completes when startup has finished or failed.</returns>
        /// <exception cref="AcmeConfigurationException">Thrown when TLS is enabled but the factory returns no manager.</exception>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            var options = _options.Value;
            if (!options.IsTlsListenerEnabled)
            {
                AcmeLogMessages.TlsDisabledIdle(_logger);
                return;
            }

            _manager = _factory.GetOrCreateManager()
                ?? throw new AcmeConfigurationException(
                    "manager_missing",
                    "TLS is enabled but CertificateManager could not be created");

            AcmeLogMessages.EnsuringCertificate(_logger, options.AcmeDirectoryUrl);

            try
            {
                var material = await _manager.EnsureCertificateAsync(cancellationToken).ConfigureAwait(false);
                _certificatePublisher.PublishFromPfx(material.PfxBytes, _manager.CurrentPfxPassword);
                _readiness.MarkReady();
                _observation?.OnObserved(_manager.EvaluateExisting(_time.GetUtcNow()), renewalReplacedCertificate: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AcmeLogMessages.ProvisioningFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
                throw;
            }

            _execution = RunRenewalLoopAsync(_runCts.Token);
        }

        /// <summary>
        /// Cancels the renewal loop and waits for it to finish.
        /// Does not contact the certificate authority. A missing loop task returns immediately.
        /// <see cref="OperationCanceledException"/> from the wait is ignored; other wait failures are logged.
        /// </summary>
        /// <param name="cancellationToken">Bounds the wait for the renewal loop. It does not cancel the loop; <see cref="_runCts"/> does that.</param>
        /// <returns>A task that completes when the wait finishes or there is no loop.</returns>
        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is null)
            {
                return;
            }

            try
            {
                await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel.
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                AcmeLogMessages.RenewalLoopStopError(_logger, ex);
            }
        }

        /// <summary>
        /// Cancels and disposes the renewal-loop token source. A second call does nothing.
        /// Does not wait for the loop and does not contact the certificate authority.
        /// </summary>
        /// <returns>A task that completes after the token source is disposed.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            _runCts.Dispose();
        }

        /// <summary>
        /// After <see cref="RenewalCheckInterval"/>, renews when due and publishes the new PFX.
        /// Each attempt reads <see cref="AcmeCloudflareOptions.AcmeCertificatePassword"/> once and uses that snapshot
        /// for issuance, persistence, and publish. A blank snapshot keeps the password that last saved the live PFX.
        /// The existing file is loaded with the password that saved it, not with a newer snapshot.
        /// Failures other than cancellation of <paramref name="cancellationToken"/> are logged and the loop continues.
        /// </summary>
        /// <param name="cancellationToken">Stops the loop. This is <see cref="_runCts"/>, not the start token.</param>
        /// <returns>A task that completes when the loop is cancelled or <see cref="_manager"/> is null.</returns>
        private async Task RunRenewalLoopAsync(CancellationToken cancellationToken)
        {
            if (_manager is null)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WaitRenewalIntervalAsync(cancellationToken).ConfigureAwait(false);
                    if (Volatile.Read(ref _renewalFaultsToInject) > 0
                        && Interlocked.Decrement(ref _renewalFaultsToInject) >= 0)
                    {
                        throw new InvalidOperationException("injected ACME renewal fault");
                    }

                    var now = _time.GetUtcNow();
                    var configuredPassword = _options.Value.AcmeCertificatePassword;
                    var issuePassword = string.IsNullOrWhiteSpace(configuredPassword) ? null : configuredPassword;
                    var renewed = await _manager.RenewIfDueAsync(cancellationToken, now, issuePassword).ConfigureAwait(false);
                    if (renewed)
                    {
                        var material = _manager.CurrentMaterial
                            ?? throw new AcmeCertificateException("no_certificate", "renewed without material");
                        var publishPassword = issuePassword ?? _manager.CurrentPfxPassword;
                        _certificatePublisher.PublishFromPfx(material.PfxBytes, publishPassword);
                        AcmeLogMessages.RenewalCompleted(_logger);
                    }

                    _observation?.OnObserved(_manager.EvaluateExisting(now), renewed);
                    NoteRenewalCheckCompleted();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                // codeql[cs/catch-of-all-exceptions] Certificate renewal must record every failure and continue the documented recovery path.
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    AcmeLogMessages.RenewalCheckFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
                    ObserveFailure();
                    NoteRenewalCheckCompleted();
                }
            }
        }

        /// <summary>Increments <see cref="_renewalChecksCompleted"/> and releases any armed test signal.</summary>
        private void NoteRenewalCheckCompleted()
        {
            Interlocked.Increment(ref _renewalChecksCompleted);
            Interlocked.Exchange(ref _nextRenewalCheckSignal, null)?.TrySetResult();
        }

        /// <summary>
        /// Arms a one-shot renewal timer on <see cref="_time"/>, then increments <see cref="_renewalWaitsStarted"/>.
        /// The counter advances only after the timer is registered so observers waiting on
        /// <see cref="RenewalWaitsStarted"/> can safely advance a manual clock without racing
        /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>'s internal timer creation.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait when the renewal loop is stopped.</param>
        /// <returns>A task that completes when the interval elapses or <paramref name="cancellationToken"/> is cancelled.</returns>
        private async Task WaitRenewalIntervalAsync(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            using ITimer timer = _time.CreateTimer(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                RenewalCheckInterval,
                Timeout.InfiniteTimeSpan);
            Interlocked.Increment(ref _renewalWaitsStarted);
            await completion.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// After a renewal exception, re-reads the certificate and notifies <see cref="_observation"/>.
        /// An unreadable store is reported through <see cref="IAcmeCertificateObservation.OnUnreadable"/>.
        /// Shutdown cancellation does not call this method.
        /// </summary>
        private void ObserveFailure()
        {
            if (_manager is null || _observation is null)
            {
                return;
            }

            try
            {
                _observation.OnObserved(_manager.EvaluateExisting(_time.GetUtcNow()), renewalReplacedCertificate: false);
            }
            // codeql[cs/catch-of-all-exceptions] An unreadable certificate must be reported without ending the renewal loop.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                _observation.OnUnreadable(ex);
            }
        }
    }
}
