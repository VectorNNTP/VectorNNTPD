using System.Collections.Concurrent;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Article-work handler whose outcome, error, and thrown exception are set by the test.
    /// A queued <see cref="ArticleWorkControlStage"/> is consumed first. Otherwise <see cref="Gate"/> blocks before the configured result.
    /// </summary>
    internal sealed class ControllableArticleWorkHandler : IArticleWorkHandler
    {
        /// <summary>Default handler outcome when no stage is queued and nothing is thrown.</summary>
        public ArticleWorkOutcome Outcome { get; set; } = ArticleWorkOutcome.ProviderFailure;

        /// <summary>Error text returned with the default outcome when no stage is queued and <see cref="Throw"/> is null.</summary>
        public string? Error { get; set; }

        /// <summary>FQDN copied into the default result. Queued stages use their own FQDN.</summary>
        public string? Fqdn { get; set; }

        /// <summary>Listener port copied into the default result. Queued stages use their own port.</summary>
        public int? ListenerPort { get; set; }

        /// <summary>Article id copied into the default result. Queued stages use their own id.</summary>
        public ArticleId? ArticleId { get; set; }

        /// <summary>Exception thrown after <see cref="Gate"/> on the default path. Null returns <see cref="Outcome"/>.</summary>
        public Exception? Throw { get; set; }

        /// <summary>When set, the default path waits on this source after signalling <see cref="Started"/>. A queued stage uses its own gate.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Completed when the default path begins, before <see cref="Gate"/>. Not signalled for a queued stage.</summary>
        public TaskCompletionSource? Started { get; set; }

        /// <summary>Scripted invocations consumed before the default outcome. Each stage is used once.</summary>
        public ConcurrentQueue<ArticleWorkControlStage> Stages { get; } = new();

        /// <summary>How many times <see cref="HandleAsync"/> has been entered.</summary>
        public int HandleCount { get; private set; }

        /// <summary>Most recent item passed to <see cref="HandleAsync"/>.</summary>
        public ArticleWorkItem? LastItem { get; private set; }

        /// <summary>
        /// Consumes one queued stage, or waits on <see cref="Gate"/> and returns the default outcome.
        /// Cancellation after the wait is thrown and does not return a result.
        /// </summary>
        /// <param name="item">Work item recorded in <see cref="LastItem"/>.</param>
        /// <param name="cancellationToken">Cancels a gate wait and is checked again before the result is returned.</param>
        public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
            ArticleWorkItem item,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            LastItem = item;
            HandleCount++;
            if (Stages.TryDequeue(out var stage))
            {
                stage.Started.TrySetResult();
                if (stage.Gate is not null)
                {
                    await stage.Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (stage.Throw is not null)
                {
                    throw stage.Throw;
                }

                return new ArticleWorkHandlerResult(
                    stage.Outcome,
                    stage.Error,
                    Fqdn: stage.Fqdn,
                    ListenerPort: stage.ListenerPort,
                    ArticleId: stage.ArticleId);
            }

            Started?.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            return new ArticleWorkHandlerResult(Outcome, Error, Fqdn: Fqdn, ListenerPort: ListenerPort, ArticleId: ArticleId);
        }
    }

    /// <summary>
    /// One scripted <see cref="ControllableArticleWorkHandler"/> invocation, consumed before the handler's default outcome.
    /// </summary>
    /// <param name="Started">Completed when this stage begins, before <paramref name="Gate"/>.</param>
    /// <param name="Gate">Optional wait before the stage result. Null does not block.</param>
    /// <param name="Outcome">Result returned when <paramref name="Throw"/> is null.</param>
    /// <param name="Error">Error text returned with <paramref name="Outcome"/>.</param>
    /// <param name="Fqdn">Success FQDN for this stage. Null omits it.</param>
    /// <param name="ListenerPort">Success listener port for this stage. Null omits it.</param>
    /// <param name="Throw">Exception thrown after the gate. Null returns the outcome.</param>
    /// <param name="ArticleId">Success article id for this stage. Null omits it.</param>
    internal sealed record ArticleWorkControlStage(
        TaskCompletionSource Started,
        TaskCompletionSource? Gate,
        ArticleWorkOutcome Outcome,
        string? Error = null,
        string? Fqdn = null,
        int? ListenerPort = null,
        Exception? Throw = null,
        ArticleId? ArticleId = null);
}
