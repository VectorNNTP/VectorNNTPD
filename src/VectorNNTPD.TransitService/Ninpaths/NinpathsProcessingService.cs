using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Ninpaths
{
    /// <summary>
    /// Background ninpaths worker. Consumes completed Path-survey files without
    /// blocking rotation or NNTP request processing.
    /// </summary>
    /// <remarks>
    /// The pending queue holds at most <see cref="NinpathsConstants.PendingFileCapacity"/>
    /// completed files (not Path records). Worker exceptions are logged and do not
    /// fault <see cref="Execution"/>. The source file is streamed; raw Path lines
    /// are not retained.
    /// </remarks>
    public sealed class NinpathsProcessingService : IApplicationService
    {
        private readonly Channel<NinpathsCompletedFile> _queue;
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly IEmailService _email;
        private readonly IOptions<NntpdOptions> _nntpdOptions;
        private readonly IOptions<EmailOptions> _emailOptions;
        private readonly TimeProvider _time;
        private readonly ILogger<NinpathsProcessingService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new ninpaths processing service.</summary>
        public NinpathsProcessingService(
            IEmailService email,
            IOptions<NntpdOptions> nntpdOptions,
            IOptions<EmailOptions> emailOptions,
            ILogger<NinpathsProcessingService> logger,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(email);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(emailOptions);
            ArgumentNullException.ThrowIfNull(logger);
            _email = email;
            _nntpdOptions = nntpdOptions;
            _emailOptions = emailOptions;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _queue = Channel.CreateBounded<NinpathsCompletedFile>(
                new BoundedChannelOptions(NinpathsConstants.PendingFileCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
        }

        /// <inheritdoc />
        public string Name => "NinpathsProcessing";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Gets the number of completed files waiting for the worker.</summary>
        internal int PendingCount => _queue.Reader.Count;

        /// <summary>
        /// Enqueues an already-open completed file. Returns <see langword="false"/>
        /// on duplicate path or a full queue (the caller must dispose <paramref name="file"/>).
        /// </summary>
        internal bool TryEnqueue(NinpathsCompletedFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            var key = System.IO.Path.GetFullPath(file.Path);
            if (!_seen.TryAdd(key, 0))
            {
                NinpathsLogMessages.Duplicate(_logger, file.Path);
                return false;
            }

            if (_queue.Writer.TryWrite(file))
            {
                return true;
            }

            _seen.TryRemove(key, out _);
            NinpathsLogMessages.QueueFull(_logger, file.Path);
            return false;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            NinpathsLogMessages.Started(_logger);
            _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _queue.Writer.TryComplete();
            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            var execution = _execution;
            if (execution is not null)
            {
                try
                {
                    await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    NinpathsLogMessages.WorkerFailed(_logger, ex);
                }
            }

            DrainPending();
            NinpathsLogMessages.Stopped(_logger);
            _runCts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var file in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    using (file)
                    {
                        try
                        {
                            await ProcessAsync(file, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                        catch (Exception ex)
                        {
                            NinpathsLogMessages.ProcessingFailed(_logger, ex, file.Path);
                        }
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                NinpathsLogMessages.WorkerFailed(_logger, ex);
            }
        }

        private async Task ProcessAsync(NinpathsCompletedFile file, CancellationToken cancellationToken)
        {
            var nntpd = _nntpdOptions.Value;
            var recipients = NinpathsTop1000.GetRecipients(nntpd);
            if (recipients.Length == 0)
            {
                NinpathsLogMessages.Disabled(_logger, file.Path);
                return;
            }

            NinpathsLogMessages.Processing(_logger, file.Path);
            var stats = NinpathsLogReader.Read(file.Stream);
            if (stats.TotalArticles == 0)
            {
                NinpathsLogMessages.EmptyDump(_logger, file.Path);
                return;
            }

            var (start, end, average) = NinpathsReportPeriod.Resolve(file.Path, _time);
            var report = NinpathsReportFormatter.Format(stats, start, end, average);
            if (report.Length == 0)
            {
                NinpathsLogMessages.EmptyDump(_logger, file.Path);
                return;
            }

            if (!EmailOptionsValidator.TryValidateMailbox(_emailOptions.Value.DefaultFrom, out var from)
                && !EmailOptionsValidator.TryValidateMailbox(_emailOptions.Value.EnvelopeSender, out from))
            {
                NinpathsLogMessages.MissingFrom(_logger, file.Path);
                return;
            }

            var message = NinpathsReportMail.TryCreate(report, nntpd.Fqdn, from, recipients);
            if (message is null)
            {
                return;
            }

            var result = await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case EmailEnqueueStatus.Accepted:
                    NinpathsLogMessages.EmailAccepted(_logger, file.Path, recipients.Length, report.Length);
                    break;
                case EmailEnqueueStatus.Disabled:
                    NinpathsLogMessages.EmailDisabled(_logger, file.Path);
                    break;
                default:
                    NinpathsLogMessages.EmailFailed(
                        _logger,
                        file.Path,
                        result.Status.ToString(),
                        result.Detail);
                    break;
            }
        }

        private void DrainPending()
        {
            while (_queue.Reader.TryRead(out var leftover))
            {
                leftover.Dispose();
            }
        }
    }
}
