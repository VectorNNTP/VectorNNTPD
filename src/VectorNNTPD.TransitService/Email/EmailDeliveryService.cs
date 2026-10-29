using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Email.Smtp;

namespace VectorNNTPD.TransitService.Email
{
    /// <summary>
    /// Background email worker. Scans the filesystem spool and talks SMTP.
    /// </summary>
    /// <remarks>
    /// Startup creates the spool directory and recovers leftover <c>.wrk</c> claims
    /// back to <c>.eml</c>. It does not open an SMTP connection and does not
    /// requeue <c>.delivered</c> or <c>failed/</c> files. Shutdown stops new
    /// submissions, allows the current SMTP operation to finish within
    /// <see cref="EmailSpoolOptions.ShutdownTimeout"/>, then leaves remaining
    /// pending <c>.eml</c> files on disk.
    /// </remarks>
    public sealed class EmailDeliveryService : IApplicationService
    {
        private readonly IEmailSpool _spool;
        private readonly ISmtpTransport _transport;
        private readonly IOptions<EmailOptions> _options;
        private readonly ILogger<EmailDeliveryService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private readonly Random _random = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new delivery service.</summary>
        public EmailDeliveryService(
            IEmailSpool spool,
            ISmtpTransport transport,
            IOptions<EmailOptions> options,
            ILogger<EmailDeliveryService> logger)
        {
            ArgumentNullException.ThrowIfNull(spool);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _spool = spool;
            _transport = transport;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "EmailDelivery";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            var options = _options.Value;
            EmailLogMessages.DeliveryStarting(
                _logger,
                options.Enabled,
                options.Smtp.Security.ToString());
            if (!options.Enabled)
            {
                return Task.CompletedTask;
            }

            _spool.EnsureDirectory();
            _spool.RecoverClaims();
            _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _spool.StopAccepting();
            var execution = _execution;
            if (execution is null)
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
                return;
            }

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var drain = _options.Value.Spool?.ShutdownTimeout ?? TimeSpan.FromSeconds(15);
            drainCts.CancelAfter(drain);
            try
            {
                await execution.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
            finally
            {
                EmailLogMessages.DeliveryStopped(_logger);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var smtp = _options.Value.Smtp ?? new SmtpOptions();
            var scan = _options.Value.Spool?.ScanInterval ?? TimeSpan.FromSeconds(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                EmailSpoolClaim? claim;
                try
                {
                    claim = _spool.TryClaimNext();
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    EmailLogMessages.DeliveryUnexpected(_logger, ex, 0, null);
                    claim = null;
                }

                if (claim is null)
                {
                    try
                    {
                        await _spool.WaitAsync(scan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    await DeliverAsync(claim, smtp, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _spool.Release(claim);
                    break;
                }
            }
        }

        private async Task DeliverAsync(EmailSpoolClaim claim, SmtpOptions smtp, CancellationToken cancellationToken)
        {
            var item = claim.Item;
            while (true)
            {
                try
                {
                    _ = await _transport.SendAsync(item, cancellationToken).ConfigureAwait(false);
                    _spool.Complete(claim);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _spool.Release(claim);
                    throw;
                }
                catch (SmtpException ex) when (ex.Kind == SmtpFailureKind.Cancelled)
                {
                    _spool.Release(claim);
                    throw;
                }
                catch (SmtpException ex)
                {
                    var retry = ex.IsTransient && item.Attempt < smtp.MaxAttempts;
                    if (!retry)
                    {
                        EmailLogMessages.DeliveryFailed(
                            _logger,
                            ex.Kind.ToString(),
                            ex.StatusCode,
                            item.Recipients.Count,
                            item.CorrelationId);
                        if (ex.IsTransient)
                        {
                            _spool.Defer(claim);
                        }
                        else
                        {
                            _spool.Quarantine(claim);
                        }

                        return;
                    }

                    EmailLogMessages.DeliveryRetry(
                        _logger,
                        item.Attempt,
                        smtp.MaxAttempts,
                        ex.Kind.ToString(),
                        ex.StatusCode);
                    var delay = EmailRetryDelay.Compute(smtp, item.Attempt, _random);
                    item.Attempt++;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    EmailLogMessages.DeliveryUnexpected(
                        _logger,
                        ex,
                        item.Recipients.Count,
                        item.CorrelationId);
                    _spool.Quarantine(claim);
                    return;
                }
            }
        }
    }
}
