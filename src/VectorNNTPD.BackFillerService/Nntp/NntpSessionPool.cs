using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Bounded per-provider NNTP session pool. One lease owns one session.
    /// </summary>
    /// <remarks>
    /// Idle sessions are reused by <see cref="AcquireAsync"/>. DATE keepalive runs only while a session sits idle and <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
    /// A background fill replaces retired sessions up to the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// A session remains in <see cref="_idle"/> only while it is also in <see cref="_live"/> and waiting to be leased.
    /// When DATE keepalive is enabled, that idle entry and its registration are published under the same lock.
    /// Retirement, dequeue, and lease return move that membership under <see cref="_idleGate"/> before any await, so a retired session is not kept until a later acquire.
    /// A drain generation completes only when the lease count that belongs to that generation reaches zero. A return captures that generation under <see cref="_permitGate"/> and completes it after the lock is released, so a later zero-to-one transition is a different source.
    /// </remarks>
    internal sealed class NntpSessionPool : IAsyncDisposable
    {
        /// <summary>Provider snapshot. Replaced by <see cref="BindProvider"/>; not a frozen constructor copy.</summary>
        private BackFillerProviderDefinition _provider;

        /// <summary>Timeouts and buffers copied into every session.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Factory used by <see cref="CreateReadySessionAsync"/>.</summary>
        private readonly INntpTransportFactory _transport;

        /// <summary>
        /// Application-wide establishment concurrency gate shared by every pool.
        /// Held only around <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </summary>
        private readonly ProviderSessionEstablishmentGate _establishment;

        /// <summary>Pool logger. Also passed to each <see cref="NntpProviderSession"/>.</summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Clock for DATE keepalive delays and the periodic replenish wait.
        /// Production uses <see cref="TimeProvider.System"/>, which keeps <see cref="ReplenishInterval"/>.
        /// </summary>
        private readonly TimeProvider _time;

        /// <summary>
        /// Absolute budget for one <see cref="DisposeAsync"/> call. Defaults to two seconds.
        /// It starts when that call creates its <see cref="NntpShutdownDeadline"/> and covers the keepalive wait, the lease wait, and every graceful QUIT.
        /// It is not granted again for each of those phases or for each session.
        /// </summary>
        private readonly TimeSpan _shutdownGrace;

        /// <summary>
        /// Wakeup signal for <see cref="AcquirePermitAsync"/> waiters. The count is not the pool capacity.
        /// <see cref="ReleasePermit"/> releases one count only when <see cref="_permitWaiters"/> is positive.
        /// A cancelled waiter that drains that count posts it again while another waiter is still counted,
        /// so an available permit cannot be left with no wakeup.
        /// </summary>
        // codeql[cs/missed-using-statement] Owned for the pool lifetime. A using at this declaration would release it when the constructor returns. Shutdown disposes it once, after retirement, and a second dispose returns before that finally.
        private readonly SemaphoreSlim _leases = new(0);

        /// <summary>
        /// Synchronizes <see cref="_availablePermits"/>, <see cref="_permitReservations"/>, <see cref="_permitWaiters"/>,
        /// the issue-slot check on <see cref="_activeLeases"/>, and replacement or capture of <see cref="_drained"/>.
        /// Not held across await. Callers do not take <see cref="_idleGate"/> or <see cref="_fillGate"/> while holding it.
        /// </summary>
        private readonly Lock _permitGate = new();

        /// <summary>
        /// Permits <see cref="AcquirePermitAsync"/> may grant immediately.
        /// Under <see cref="_permitGate"/>, this plus <see cref="_permitReservations"/> equals <see cref="BackFillerProviderDefinition.MaxSessions"/>
        /// when reservations do not exceed that max. After a shrink below the reservation count, this is zero until returns catch up.
        /// </summary>
        private int _availablePermits;

        /// <summary>
        /// Acquires that passed <see cref="AcquirePermitAsync"/> and have not yet called <see cref="ReleasePermit"/>.
        /// Includes in-flight acquires and issued leases. Independent of permits removed by shrink.
        /// </summary>
        private int _permitReservations;

        /// <summary>Callers blocked in <see cref="_leases"/> inside <see cref="AcquirePermitAsync"/>.</summary>
        private int _permitWaiters;

        /// <summary>DATE loop for each idle session that has keepalive enabled. Absent while the session is leased.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, KeepAliveRegistration> _keepAlives = new();

        /// <summary>
        /// Every DATE loop that has started and not yet exited, including a registration already removed from <see cref="_keepAlives"/>.
        /// <see cref="AwaitKeepAlivesAsync"/> waits on these tasks. Membership is not guarded by <see cref="_idleGate"/>.
        /// </summary>
        private readonly ConcurrentDictionary<KeepAliveRegistration, byte> _keepAliveLifetimes = new();

        /// <summary>
        /// Test gate invoked after <see cref="TryAdmitIdle"/> has published idle membership and its DATE registration,
        /// and before <see cref="StopKeepAliveUnlessIdleAsync"/> decides whether that publication is still current.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task>? _afterKeepAlivePublished;

        /// <summary>
        /// Test observer invoked under <see cref="_idleGate"/> after an idle membership and its DATE registration
        /// are both published. Arguments are the idle count and the keepalive count at that point.
        /// Null in production. The delegate must not wait, take <see cref="_idleGate"/>, or call
        /// <see cref="IdleSessionCount"/>.
        /// </summary>
        private Action<int, int>? _idleKeepAliveLinearized;

        /// <summary>
        /// Test gate invoked after a not-idle decision has detached its registration and before that registration is cancelled.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/> and must not be used to look up a newer registration.
        /// </summary>
        private Func<Task>? _keepAliveStopGate;

        /// <summary>
        /// Test gate invoked when a DATE loop is leaving, before it removes only its own registration.
        /// The argument is that loop's task. Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task, Task>? _keepAliveLoopExitGate;

        /// <summary>
        /// Test gate invoked after disposal has snapshotted running DATE loops and before it waits for them.
        /// The argument is that snapshot. Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<IReadOnlyList<Task>, Task>? _keepAliveDrainSnapshot;

        /// <summary>
        /// Test gate invoked after admission has armed the first DATE delay and the loop has observed publication,
        /// before that loop waits. Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task>? _beforeFirstKeepAliveDelay;

        /// <summary>
        /// Test gate invoked after <see cref="CreateReadySessionAsync"/> returns and before <see cref="TryIssueReusable"/>.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/> and outside <see cref="_fillGate"/>'s release.
        /// </summary>
        private Func<Task>? _beforeCreatedSessionIssue;

        /// <summary>
        /// Test gate invoked after a permit is reserved and before <see cref="TryReserveIssueSlot"/>.
        /// Null in production. The delegate runs outside <see cref="_permitGate"/> and <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task>? _beforePermitIssuance;

        /// <summary>
        /// Test gate invoked after a retired session has left the live set and before that session is disposed.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/> and must not be required for accounting to stay committed.
        /// </summary>
        private Func<Task>? _beforeDetachedSessionDispose;

        /// <summary>
        /// Test gate invoked after a DATE loop passes its cancellation check and before it reads <see cref="NntpProviderSession.IsReusable"/>.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task>? _beforeKeepAliveReusabilityCheck;

        /// <summary>
        /// Test gate invoked after a DATE loop has decided to retire and before <see cref="RetireSessionAsync"/> runs for that registration.
        /// The argument is that loop's task. Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task, Task>? _beforeKeepAliveRetirement;

        /// <summary>
        /// Test gate invoked after dispose has snapshotted <see cref="_drained"/> and before that task is awaited.
        /// The argument is the snapshotted generation. Null in production. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        private Func<Task, Task>? _beforeLeaseDrainWait;

        /// <summary>
        /// Test gate invoked when shutdown cancellation has returned and permit waiters are still inside <see cref="AcquirePermitAsync"/>.
        /// Null in production. The delegate runs before those waiters are drained and before <see cref="_leases"/> is disposed.
        /// </summary>
        private Func<Task>? _beforePermitWaiterDrain;

        /// <summary>
        /// Test gate invoked after a permit waiter is counted and before it waits on <see cref="_leases"/>.
        /// Null in production. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        private Func<Task>? _beforePermitWait;

        /// <summary>
        /// Test gate invoked after a permit wait has failed and before that waiter drains a raced release.
        /// Null in production. The waiter is still counted. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        private Func<Task>? _beforeCancelledPermitDrain;

        /// <summary>Permit waiters still inside <see cref="AcquirePermitAsync"/> when semaphores are about to be disposed. Zero when the drain finished.</summary>
        private int _permitWaitersAtSemaphoreDispose;

        /// <summary>
        /// Test gate invoked after the lease drain wait returns and before remaining sessions are retired.
        /// Null in production. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        private Func<Task>? _afterDrainObserved;

        /// <summary>
        /// FIFO of sessions waiting for <see cref="AcquireAsync"/>.
        /// Guarded by <see cref="_idleGate"/> with <see cref="_idleNodes"/> and <see cref="_issued"/>.
        /// Count is the number of live idle sessions, not a history of retired ones.
        /// </summary>
        private readonly LinkedList<NntpProviderSession> _idle = new();

        /// <summary>
        /// Nodes in <see cref="_idle"/>, for removal of one retired session.
        /// Guarded by <see cref="_idleGate"/>. A session is entered here only while it is also in <see cref="_live"/>.
        /// </summary>
        private readonly Dictionary<NntpProviderSession, LinkedListNode<NntpProviderSession>> _idleNodes = new();

        /// <summary>
        /// Sessions removed from <see cref="_idle"/> and handed to a lease by <see cref="TryIssueReusable"/>.
        /// Guarded by <see cref="_idleGate"/>. An idle-keeper retirement does not dispose these.
        /// <see cref="ReturnAsync"/> and pool disposal clear the mark and then enqueue or retire.
        /// </summary>
        private readonly HashSet<NntpProviderSession> _issued = new();

        /// <summary>
        /// Serializes idle membership, issue, and the live-removal half of retirement.
        /// Not held across awaits, keepalive callbacks, or subscriber notification.
        /// </summary>
        private readonly Lock _idleGate = new();

        /// <summary>Sessions created and not yet retired. The byte value is unused.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, byte> _live = new();

        /// <summary>Connection numbers currently allocated. Guarded by <see cref="_slotGate"/>.</summary>
        private readonly HashSet<int> _usedSlots = [];

        /// <summary>
        /// Non-recursive lock for <see cref="_usedSlots"/>.
        /// No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _slotGate = new();

        /// <summary>Cancelled at the start of dispose. Linked into acquire waits and DATE loops.</summary>
        // codeql[cs/missed-using-statement] Owned for the pool lifetime and linked into acquire and DATE. A using at this declaration would cancel those waits when the constructor returns. Shutdown disposes it once, after retirement.
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Serializes session creation and <see cref="ShrinkToBoundAsync"/>.</summary>
        // codeql[cs/missed-using-statement] Owned for the pool lifetime and held across session creation. A using at this declaration would release it when the constructor returns. Shutdown disposes it once, after retirement.
        private readonly SemaphoreSlim _fillGate = new(1, 1);

        /// <summary>
        /// Sessions counted against the current max.
        /// Incremented before a connection number is reserved and decremented exactly once when that attempt is abandoned or the live session is retired.
        /// </summary>
        private int _created;

        /// <summary>
        /// Leases whose issue slot <see cref="TryReserveIssueSlot"/> has reserved and <see cref="CompleteIssuedLeaseAsync"/> has not released.
        /// Transitions are made under <see cref="_permitGate"/> together with <see cref="_drained"/>.
        /// </summary>
        private int _activeLeases;

        /// <summary>
        /// Zero until dispose begins. The first dispose sets this under <see cref="_idleGate"/> before it snapshots <see cref="_drained"/> under <see cref="_permitGate"/>.
        /// </summary>
        private int _disposed;

        /// <summary>Zero until <see cref="StartPeriodicReplenish"/> publishes <see cref="_replenishTask"/>.</summary>
        private int _replenishStarted;

        /// <summary>
        /// Periodic deficit supervisor. Null before the first start.
        /// It stays incomplete across failed fill attempts and completes when shutdown stops the loop.
        /// </summary>
        private Task? _replenishTask;

        /// <summary>No caller is inside <see cref="NotifyActiveSessionCountChanged"/>.</summary>
        private const int CapacityNotificationIdle = 0;

        /// <summary>One caller is invoking <see cref="ActiveSessionCountChanged"/>.</summary>
        private const int CapacityNotificationActive = 1;

        /// <summary>
        /// A notification is in progress and a later state change has requested another delivery.
        /// The active caller replays after the current callback returns.
        /// </summary>
        private const int CapacityNotificationPending = 2;

        /// <summary>
        /// <see cref="CapacityNotificationIdle"/>, <see cref="CapacityNotificationActive"/>, or
        /// <see cref="CapacityNotificationPending"/>. Coordinated with <see cref="Interlocked"/> so a callback
        /// does not run nested, and a state change during that callback is still delivered afterward.
        /// </summary>
        private int _capacityNotification;

        /// <summary>
        /// Completion of the current lease generation. Starts completed because the pool has no leases.
        /// <see cref="TryReserveIssueSlot"/> replaces it when the count moves from zero to one.
        /// <see cref="CompleteIssuedLeaseAsync"/> captures this reference when the count moves to zero and completes that instance after releasing <see cref="_permitGate"/>.
        /// A later generation is a different instance and is not completed by the return that ended the previous one.
        /// </summary>
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Test gate invoked after a return has captured the drain generation that reached zero leases and before that generation is completed.
        /// Null in production. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        private Func<Task>? _beforeDrainGenerationCompleted;

        /// <summary>Delay between periodic deficit fills toward <see cref="BackFillerProviderDefinition.MaxSessions"/>.</summary>
        private static readonly TimeSpan ReplenishInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Creates a pool whose lease permits and connection-number ceiling start at <see cref="BackFillerProviderDefinition.MaxSessions"/> on <paramref name="provider"/>.
        /// </summary>
        /// <param name="provider">
        /// Provider identity. <see cref="BackFillerProviderDefinition.MaxSessions"/> must be at least 1.
        /// <see cref="BackFillerProviderDefinition.MinSessions"/> must be from 0 through that max. It is not used to size the pool.
        /// </param>
        /// <param name="options">Timeouts and buffers copied into each session.</param>
        /// <param name="transport">Opens sockets for new sessions.</param>
        /// <param name="logger">Pool and session logger.</param>
        /// <param name="establishment">
        /// Application-wide gate shared by every pool. Held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <param name="shutdownGrace">
        /// Absolute budget for <see cref="DisposeAsync"/>, from the start of that call through session retirement.
        /// Two seconds when null. A replacement drain captures the same length once, after its lease wait, for every remaining session.
        /// A registry shutdown passes its own deadline into <see cref="DrainAndDisposeAsync(CancellationToken, NntpShutdownDeadline)"/>
        /// and does not add this value again.
        /// </param>
        /// <param name="timeProvider">Clock for DATE delays and replenish waits. <see cref="TimeProvider.System"/> when null.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="provider"/>, <paramref name="options"/>, <paramref name="transport"/>,
        /// <paramref name="logger"/>, or <paramref name="establishment"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">The session bounds on <paramref name="provider"/> are outside the constructor checks.</exception>
        internal NntpSessionPool(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            INntpTransportFactory transport,
            ILogger logger,
            ProviderSessionEstablishmentGate establishment,
            TimeSpan? shutdownGrace = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(establishment);
            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            if (provider.MinSessions < 0 || provider.MinSessions > provider.MaxSessions)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MinSessions must be between 0 and MaxSessions.");
            }

            _provider = provider;
            _options = options;
            _transport = transport;
            _establishment = establishment;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2);
            _availablePermits = provider.MaxSessions;
            _drained.TrySetResult();
        }

        /// <summary>Gets <see cref="BackFillerProviderDefinition.Backbone"/> from the bound provider snapshot.</summary>
        internal string Backbone => _provider.Backbone;

        /// <summary>Gets the provider snapshot this pool currently serves.</summary>
        /// <remarks>Updated by <see cref="BindProvider"/>.</remarks>
        internal BackFillerProviderDefinition Provider => _provider;

        /// <summary>Gets the number of session objects still in the live set, including sessions that are connecting and not yet retired.</summary>
        internal int LiveSessionCount => _live.Count;

        /// <summary>
        /// Gets the number of ACTIVE sessions: attached sessions that have completed connect
        /// (<see cref="NntpSessionState.Ready"/> or <see cref="NntpSessionState.Busy"/>).
        /// Connecting, failed, and retired sessions are not counted.
        /// </summary>
        internal int ActiveSessionCount
        {
            get
            {
                return _live.Keys.Count(session => session.State is NntpSessionState.Ready or NntpSessionState.Busy);
            }
        }

        /// <summary>Outstanding <see cref="NntpSessionLease"/> instances that have not yet been disposed.</summary>
        internal int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

        /// <summary>
        /// Sessions currently waiting in <see cref="_idle"/>.
        /// Retired sessions are not included. The count is therefore bounded by <see cref="LiveSessionCount"/>.
        /// </summary>
        internal int IdleSessionCount
        {
            get
            {
                lock (_idleGate)
                {
                    return _idle.Count;
                }
            }
        }

        /// <summary>
        /// DATE registrations currently published. A loop that has been superseded does not count once its own registration was removed.
        /// </summary>
        internal int ActiveKeepAliveCount => _keepAlives.Count;

        /// <summary>
        /// Pauses the idle-admission path after DATE keepalive is published and before the membership check that may detach it.
        /// Null unless a test is holding that window. Not used by production callers.
        /// </summary>
        internal Func<Task>? AfterKeepAlivePublished
        {
            get => _afterKeepAlivePublished;
            set => _afterKeepAlivePublished = value;
        }

        /// <summary>
        /// Observes the idle-queue and DATE-registration counts at the moment both become visible to acquisition.
        /// Null unless a test is checking that linearization. The delegate runs under <see cref="_idleGate"/>
        /// and must not wait or take that lock.
        /// </summary>
        internal Action<int, int>? IdleKeepAliveLinearized
        {
            get => _idleKeepAliveLinearized;
            set => _idleKeepAliveLinearized = value;
        }

        /// <summary>
        /// Pauses cancellation of the registration detached by a not-idle keepalive decision.
        /// Null unless a test is holding that window. The paused caller cancels only the registration it already detached.
        /// </summary>
        internal Func<Task>? KeepAliveStopGate
        {
            get => _keepAliveStopGate;
            set => _keepAliveStopGate = value;
        }

        /// <summary>
        /// Pauses a DATE loop after it has been cancelled and before it removes its own registration.
        /// The argument is that loop task. Null unless a test is holding that window.
        /// The removal matches the loop's registration, not the session key alone.
        /// </summary>
        internal Func<Task, Task>? KeepAliveLoopExitGate
        {
            get => _keepAliveLoopExitGate;
            set => _keepAliveLoopExitGate = value;
        }

        /// <summary>
        /// Pauses disposal after it has snapshotted running DATE loops, including superseded ones, and before it waits for them.
        /// The argument is that snapshot. Null unless a test is holding that window.
        /// The delegate runs outside <see cref="_idleGate"/> and must not be required for a keepalive task to exit.
        /// </summary>
        internal Func<IReadOnlyList<Task>, Task>? KeepAliveDrainSnapshot
        {
            get => _keepAliveDrainSnapshot;
            set => _keepAliveDrainSnapshot = value;
        }

        /// <summary>
        /// Pauses a DATE loop after its first delay was armed on the admission thread and before the loop waits for that delay.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_idleGate"/>.
        /// </summary>
        internal Func<Task>? BeforeFirstKeepAliveDelay
        {
            get => _beforeFirstKeepAliveDelay;
            set => _beforeFirstKeepAliveDelay = value;
        }

        /// <summary>
        /// Pauses acquisition after a new session is ready and before <see cref="TryIssueReusable"/> accepts it.
        /// Null unless a test is holding that window. A session retired during the pause is not issued.
        /// </summary>
        internal Func<Task>? BeforeCreatedSessionIssue
        {
            get => _beforeCreatedSessionIssue;
            set => _beforeCreatedSessionIssue = value;
        }

        /// <summary>
        /// Pauses an acquire after it reserved a permit and before it claims an issue slot.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task>? BeforePermitIssuance
        {
            get => _beforePermitIssuance;
            set => _beforePermitIssuance = value;
        }

        /// <summary>
        /// Pauses a return after it has selected the drain generation that reached zero leases and before that generation is completed.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task>? BeforeDrainGenerationCompleted
        {
            get => _beforeDrainGenerationCompleted;
            set => _beforeDrainGenerationCompleted = value;
        }

        /// <summary>The drain task for the lease generation that is current. Read under <see cref="_permitGate"/>.</summary>
        internal Task CurrentDrain
        {
            get
            {
                lock (_permitGate)
                {
                    return _drained.Task;
                }
            }
        }

        /// <summary>Permits that can be granted immediately. Read under <see cref="_permitGate"/>.</summary>
        internal int AvailablePermitCount
        {
            get
            {
                lock (_permitGate)
                {
                    return _availablePermits;
                }
            }
        }

        /// <summary>Callers blocked inside <see cref="AcquirePermitAsync"/>. Read under <see cref="_permitGate"/>.</summary>
        internal int PermitWaiterCount
        {
            get
            {
                lock (_permitGate)
                {
                    return _permitWaiters;
                }
            }
        }

        /// <summary>Reservations held by acquires that have not called <see cref="ReleasePermit"/>. Read under <see cref="_permitGate"/>.</summary>
        internal int PermitReservationCount
        {
            get
            {
                lock (_permitGate)
                {
                    return _permitReservations;
                }
            }
        }

        /// <summary>
        /// Pauses a DATE loop after its cancellation check and before <see cref="NntpProviderSession.IsReusable"/>.
        /// Null unless a test is holding that window. Not used by production callers.
        /// </summary>
        internal Func<Task>? BeforeKeepAliveReusabilityCheck
        {
            get => _beforeKeepAliveReusabilityCheck;
            set => _beforeKeepAliveReusabilityCheck = value;
        }

        /// <summary>
        /// Pauses retirement after the session has left <see cref="_live"/> and before <see cref="DisposeDetachedSessionAsync"/>.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_idleGate"/>.
        /// Accounting for the retired session is already committed. Replacement fill runs after the pause and the dispose.
        /// </summary>
        internal Func<Task>? BeforeDetachedSessionDispose
        {
            get => _beforeDetachedSessionDispose;
            set => _beforeDetachedSessionDispose = value;
        }

        /// <summary>
        /// Pauses a DATE loop after it has decided to retire and before that retirement runs.
        /// The argument is that loop's task. Null unless a test is holding that window.
        /// A superseded registration that resumes here must not retire the session or cancel a newer registration.
        /// </summary>
        internal Func<Task, Task>? BeforeKeepAliveRetirement
        {
            get => _beforeKeepAliveRetirement;
            set => _beforeKeepAliveRetirement = value;
        }

        /// <summary>
        /// Pauses dispose after it has selected the lease generation to wait for and before that wait.
        /// The argument is that generation. Null unless a test is holding that window. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task, Task>? BeforeLeaseDrainWait
        {
            get => _beforeLeaseDrainWait;
            set => _beforeLeaseDrainWait = value;
        }

        /// <summary>
        /// Pauses dispose after shutdown cancellation while a permit waiter is still blocked and before that waiter is drained.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task>? BeforePermitWaiterDrain
        {
            get => _beforePermitWaiterDrain;
            set => _beforePermitWaiterDrain = value;
        }

        /// <summary>
        /// Pauses a permit waiter after it is counted and before <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>.
        /// Null unless a test is holding that window. The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task>? BeforePermitWait
        {
            get => _beforePermitWait;
            set => _beforePermitWait = value;
        }

        /// <summary>
        /// Pauses a failed permit wait after that waiter has left the semaphore and before it drains a raced count.
        /// The waiter is still included in <see cref="PermitWaiterCount"/>. Null unless a test is holding that window.
        /// The delegate runs outside <see cref="_permitGate"/>.
        /// </summary>
        internal Func<Task>? BeforeCancelledPermitDrain
        {
            get => _beforeCancelledPermitDrain;
            set => _beforeCancelledPermitDrain = value;
        }

        /// <summary>
        /// Permit waiters remaining when dispose is about to drop <see cref="_leases"/>.
        /// Zero after the drain has seen every waiter leave <see cref="AcquirePermitAsync"/>.
        /// </summary>
        internal int PermitWaitersAtSemaphoreDispose => _permitWaitersAtSemaphoreDispose;

        /// <summary>
        /// Pauses dispose after the outstanding-lease wait has returned and before remaining sessions are retired.
        /// Null unless a test is holding that window. Issuance after this point must already have been rejected.
        /// </summary>
        internal Func<Task>? AfterDrainObserved
        {
            get => _afterDrainObserved;
            set => _afterDrainObserved = value;
        }

        /// <summary>Raised after the active session count may have changed.</summary>
        /// <remarks>
        /// Invoked synchronously on the caller that changed membership or finished a successful connect.
        /// Subscribers read <see cref="ActiveSessionCount"/> themselves. A callback is not nested inside another
        /// callback. A state change that requests notification while a callback is running is delivered again
        /// after that callback returns, so a retirement callback cannot hide a replacement session that became ready.
        /// A subscriber exception during successful publication fails that publication: the pool retires the session
        /// and the caller does not receive it. A subscriber exception during retirement is contained after the
        /// reservation is released, so disposal still runs and the reservation is not released twice.
        /// </remarks>
        internal event Action? ActiveSessionCountChanged;

        /// <summary>
        /// Connects the desired <see cref="BackFillerProviderDefinition.MaxSessions"/> slots.
        /// Does not require Article Work. Failed attempts do not fail the call.
        /// </summary>
        /// <param name="cancellationToken">Cancels in-flight connects. Cancellation fails this call. A connect failure does not.</param>
        /// <exception cref="ObjectDisposedException">The pool is already disposed.</exception>
        /// <remarks>
        /// Starts the periodic replenish loop once. Missing slots are scheduled concurrently, but
        /// <see cref="ProviderSessionEstablishmentGate"/> bounds how many
        /// <see cref="NntpProviderSession.ConnectAsync"/> calls run at once across all pools.
        /// </remarks>
        internal async Task EnsureDesiredSessionsAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            StartPeriodicReplenish();
            var missing = Math.Max(0, _provider.MaxSessions - _live.Count);
            if (missing == 0)
            {
                NotifyActiveSessionCountChanged();
                return;
            }

            var attempts = new Task[missing];
            for (var i = 0; i < missing; i++)
            {
                attempts[i] = TryCreateIdleSessionAsync(cancellationToken);
            }

            await Task.WhenAll(attempts).ConfigureAwait(false);
            NotifyActiveSessionCountChanged();
        }

        /// <summary>Acquires an exclusive session lease, reusing an idle session or opening one up to the bound max.</summary>
        /// <param name="cancellationToken">Cancels the permit wait and connect. Pool shutdown cancels the same wait.</param>
        /// <returns>A lease that owns one ready session. Disposing the lease returns or retires it.</returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed before the permit is taken or before a session is issued.</exception>
        /// <exception cref="OperationCanceledException">The caller token or <see cref="_shutdown"/> cancels the wait.</exception>
        /// <exception cref="InvalidOperationException">The created-session count is already at <see cref="BackFillerProviderDefinition.MaxSessions"/>.</exception>
        /// <exception cref="NntpProviderConnectException">
        /// The new session did not become <see cref="NntpSessionState.Ready"/>. It is retired without replenish.
        /// </exception>
        /// <remarks>
        /// Each idle candidate dequeued before the fill gate is held, if it is <see cref="NntpSessionState.Busy"/>, waits on that session's command gate until the in-flight DATE or ARTICLE releases it, then is kept only when <see cref="NntpProviderSession.IsReusable"/>.
        /// An idle candidate taken later, while the fill gate is held, is retired as soon as it is not reusable, including when it is still busy.
        /// A failed acquire releases its reservation. A successful acquire holds that reservation until <see cref="ReturnAsync"/>.
        /// When the current max cannot accept another issued lease, the session is returned or retired and the reservation is released before this method waits again.
        /// A session taken from <see cref="_idle"/> is issued or retired before the permit is released.
        /// Cancellation while that session is still busy retires it, because the DATE loop will not retire a session that has already left the idle list.
        /// A publication failure inside <see cref="CreateReadySessionAsync"/> retires the new session before this method returns, so the caller does not receive a lease.
        /// The caller token is linked with <see cref="_shutdown"/> only when a wait can block.
        /// An immediate permit and a ready idle session do not create that source.
        /// When the caller token cannot be cancelled, or it is already <see cref="_shutdown"/>'s token, the wait uses <see cref="_shutdown"/> directly.
        /// Production article retrieval passes a distinct work token, so a blocked acquire still links the two.
        /// </remarks>
        internal async Task<NntpSessionLease> AcquireAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            using var linked = new PermitWaitLink();
            while (true)
            {
                if (!TryTakeAvailablePermit())
                {
                    await AcquirePermitAsync(linked.Token(cancellationToken, _shutdown.Token)).ConfigureAwait(false);
                }

                var permitHeld = true;
                NntpProviderSession? taken = null;
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                    var retryWithoutPermit = false;
                    while (TryTakeIdleSession(out var keepalive) is { } idle)
                    {
                        taken = idle;
                        CancelKeepAliveRegistration(keepalive);
                        if (idle.State == NntpSessionState.Busy)
                        {
                            await WaitUntilNotBusyAsync(idle, linked.Token(cancellationToken, _shutdown.Token)).ConfigureAwait(false);
                        }

                        var issued = await TryIssueHeldSessionAsync(idle).ConfigureAwait(false);
                        if (issued.Lease is not null)
                        {
                            return issued.Lease;
                        }

                        if (issued.RejectedForCapacity)
                        {
                            permitHeld = false;
                            taken = null;
                            retryWithoutPermit = true;
                            break;
                        }

                        await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                        taken = null;
                    }

                    if (retryWithoutPermit)
                    {
                        continue;
                    }

                    await _fillGate.WaitAsync(linked.Token(cancellationToken, _shutdown.Token)).ConfigureAwait(false);
                    try
                    {
                        while (TryTakeIdleSession(out var keepalive) is { } idle)
                        {
                            taken = idle;
                            CancelKeepAliveRegistration(keepalive);
                            var issued = await TryIssueHeldSessionAsync(idle).ConfigureAwait(false);
                            if (issued.Lease is not null)
                            {
                                return issued.Lease;
                            }

                            if (issued.RejectedForCapacity)
                            {
                                permitHeld = false;
                                taken = null;
                                retryWithoutPermit = true;
                                break;
                            }

                            await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                            taken = null;
                        }

                        if (!retryWithoutPermit)
                        {
                            if (Volatile.Read(ref _created) >= _provider.MaxSessions)
                            {
                                throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
                            }

                            var created = await CreateReadySessionAsync(linked.Token(cancellationToken, _shutdown.Token)).ConfigureAwait(false);
                            taken = created;
                            if (_beforeCreatedSessionIssue is { } beforeIssue)
                            {
                                await beforeIssue().ConfigureAwait(false);
                            }

                            var issued = await TryIssueHeldSessionAsync(created).ConfigureAwait(false);
                            if (issued.Lease is not null)
                            {
                                return issued.Lease;
                            }

                            if (issued.RejectedForCapacity)
                            {
                                permitHeld = false;
                                taken = null;
                                retryWithoutPermit = true;
                            }
                            else
                            {
                                await RetireSessionAsync(created, "created session was not reusable").ConfigureAwait(false);
                                taken = null;
                                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                                throw new InvalidOperationException("NNTP session was retired before it could be issued.");
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            _fillGate.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            ShutdownObservations.AlreadyDisposed(_logger, "fill gate release");
                        }
                    }

                    if (retryWithoutPermit)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    try
                    {
                        if (taken is not null)
                        {
                            var connectionNumber = taken.ConnectionNumber;
                            try
                            {
                                // The DATE loop retires only a session that is still idle. This one was already removed.
                                await RetireSessionAsync(taken, "idle session was not reusable").ConfigureAwait(false);
                            }
                            catch (Exception cleanup) when (IsExpectedSessionCleanupFailure(cleanup))
                            {
                                TryLogSessionEstablishmentFailed(connectionNumber, cleanup.GetType().Name);
                            }
                        }
                    }
                    finally
                    {
                        if (permitHeld)
                        {
                            ReleasePermit();
                        }
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Disposes the caller-and-shutdown link created only when <see cref="AcquireAsync"/> may block.
        /// </summary>
        /// <remarks>
        /// Stored by value in the acquire state machine, so the fast path does not allocate a holder.
        /// The linked source is created on the first blocking wait. This value does not dispose <see cref="_shutdown"/>.
        /// A <see langword="using"/> variable cannot be assigned after it is declared, which is why the source lives inside this value.
        /// </remarks>
        private struct PermitWaitLink : IDisposable
        {
            private CancellationTokenSource? _linked;

            /// <summary>Returns <paramref name="shutdown"/> when the caller token does not need a separate link. Otherwise returns the linked token.</summary>
            /// <param name="caller">Token passed to <see cref="AcquireAsync"/>.</param>
            /// <param name="shutdown">Pool shutdown token. Not owned by this holder.</param>
            /// <returns>The token the blocking wait should observe.</returns>
            internal CancellationToken Token(CancellationToken caller, CancellationToken shutdown)
            {
                if (!caller.CanBeCanceled || caller == shutdown)
                {
                    return shutdown;
                }

                _linked ??= CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
                return _linked.Token;
            }

            /// <summary>Disposes the linked source when one was created.</summary>
            public void Dispose()
            {
                _linked?.Dispose();
                _linked = null;
            }
        }

        /// <summary>
        /// Ends one lease. A reusable session returns to the idle queue unless the pool is shutting down or the live count is above the bound max.
        /// </summary>
        /// <param name="session">Session previously issued by this pool.</param>
        /// <param name="retire"><see langword="true"/> disposes <paramref name="session"/> instead of idling it.</param>
        /// <remarks>
        /// The lease count is decremented even when retirement fails. <see cref="ReleasePermit"/> returns the reservation to <see cref="_availablePermits"/> only while reservations would stay below the current max.
        /// A return that finds the live count above that max retires the session and consumes the reservation so a shrunk pool does not hand the slot out again.
        /// An idle return starts DATE keepalive when <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
        /// </remarks>
        internal async Task ReturnAsync(NntpProviderSession session, bool retire)
        {
            try
            {
                if (retire || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
                {
                    await RetireSessionAsync(session, retire ? "lease requested retirement" : "pool is shutting down")
                        .ConfigureAwait(false);
                }
                else
                {
                    switch (TryAdmitIdle(session, enforceBound: true))
                    {
                        case IdleAdmission.Admitted:
                            await PublishKeepAliveAsync(session).ConfigureAwait(false);
                            break;
                        case IdleAdmission.OverBound:
                            await RetireSessionAsync(
                                    session,
                                    "max sessions shrink",
                                    replenish: false,
                                    liveAlreadyDetached: true)
                                .ConfigureAwait(false);
                            break;
                        default:
                            await RetireSessionAsync(session, "session is no longer reusable").ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                try
                {
                    await CompleteIssuedLeaseAsync().ConfigureAwait(false);
                }
                finally
                {
                    ReleasePermit();
                }
            }
        }

        /// <summary>
        /// Updates the bound provider snapshot. Used when only <see cref="BackFillerProviderDefinition.MaxSessions"/> shrinks.
        /// </summary>
        /// <param name="provider">The new snapshot with the same connection identity.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="provider"/> fails <see cref="BackFillerProviderDefinition.HasSameConnectionIdentity"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="BackFillerProviderDefinition.MaxSessions"/> is below 1.</exception>
        /// <remarks>
        /// Does not retire sessions, change permits, or reconnect.
        /// <see cref="ShrinkToBoundAsync"/> removes excess idle sessions and permits.
        /// <see cref="GrowToBound"/> restores permits after a higher max is bound.
        /// </remarks>
        internal void BindProvider(BackFillerProviderDefinition provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!provider.HasSameConnectionIdentity(_provider))
            {
                throw new ArgumentException("Provider connection identity does not match this pool.", nameof(provider));
            }

            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            _provider = provider;
        }

        /// <summary>
        /// Retires idle sessions above the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// Leased sessions above the bound retire when returned. Does not reconnect retained sessions.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait for <see cref="_fillGate"/>. Idle retirement after the gate is acquired does not observe this token.</param>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <remarks>
        /// Idle retirement happens before <see cref="AlignAvailablePermits"/>. That alignment uses
        /// <see cref="_permitReservations"/>, not previously removed permits, and does not hold <see cref="_fillGate"/> across the permit lock.
        /// Reservations already above the new bound stay with their acquires until <see cref="ReleasePermit"/>.
        /// </remarks>
        internal async Task ShrinkToBoundAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bound = _provider.MaxSessions;
                while (_live.Count > bound && TryTakeIdleSession(out var keepalive) is { } idle)
                {
                    CancelKeepAliveRegistration(keepalive);
                    await RetireSessionAsync(idle, "max sessions shrink", replenish: false).ConfigureAwait(false);
                }
            }
            finally
            {
                _fillGate.Release();
            }

            AlignAvailablePermits();
            NotifyActiveSessionCountChanged();
        }

        /// <summary>
        /// Restores acquire permits up to the bound <see cref="BackFillerProviderDefinition.MaxSessions"/> after <see cref="BindProvider"/>.
        /// Does not connect sessions. The caller fills any missing idle sessions.
        /// </summary>
        /// <remarks>
        /// <see cref="AlignAvailablePermits"/> adds only the permits the current reservation count can spare.
        /// Waiters blocked in <see cref="AcquirePermitAsync"/> are woken for those new permits. The permit lock is not held across an await.
        /// </remarks>
        internal void GrowToBound()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            AlignAvailablePermits();
        }

        /// <summary>
        /// Stops new leases, waits for outstanding leases to return, then retires remaining sessions.
        /// Used when the control plane replaces this pool. Does not force-close a leased session unless
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">
        /// Bounds the wait for outstanding leases. When it is cancelled, the wait ends and remaining sessions are still retired.
        /// It does not replace the retirement deadline, and it is not a registry shutdown deadline.
        /// </param>
        /// <returns>The dispose task.</returns>
        /// <remarks>
        /// The lease wait uses only <paramref name="cancellationToken"/>. It does not apply <see cref="_shutdownGrace"/>,
        /// so a caller that has not cancelled can wait for leased work to return.
        /// After that wait, every remaining session shares one retirement deadline of <see cref="_shutdownGrace"/>.
        /// That deadline is not the deadline captured by an unrelated registry <see cref="NntpProviderRegistry.StopAsync"/> or <see cref="NntpProviderRegistry.DisposeAsync"/>.
        /// </remarks>
        internal Task DrainAndDisposeAsync(CancellationToken cancellationToken) =>
            DrainAndDisposeAsync(cancellationToken, NntpShutdownDeadline.None);

        /// <summary>
        /// Stops new leases, waits for outstanding leases, then retires remaining sessions under <paramref name="deadline"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// Ends the lease wait when cancelled, including when <paramref name="deadline"/> still has time left.
        /// The same token cancels a QUIT write that has already started. It is not applied to ARTICLE or DATE commands.
        /// </param>
        /// <param name="deadline">
        /// Absolute instant shared with every session retired by this call.
        /// <see cref="NntpShutdownDeadline.None"/> means this call is a replacement drain: the lease wait stays on
        /// <paramref name="cancellationToken"/>, and retirement then captures one <see cref="_shutdownGrace"/> deadline for every session.
        /// </param>
        /// <returns>The dispose task.</returns>
        /// <remarks>
        /// Registry stop and dispose create one deadline and pass it here so this pool does not start another full grace.
        /// A cancelled wait or an expired deadline still retires the remaining sessions. An expired deadline skips graceful QUIT.
        /// </remarks>
        internal Task DrainAndDisposeAsync(CancellationToken cancellationToken, NntpShutdownDeadline deadline) =>
            DisposeCoreAsync(forceAfterGrace: false, cancellationToken, deadline);

        /// <summary>
        /// Stops new leases, then retires every remaining session under one deadline of <see cref="_shutdownGrace"/>.
        /// </summary>
        /// <returns>A task that completes after remaining sessions have been retired or their cleanup fault has been observed.</returns>
        /// <remarks>
        /// The deadline is captured at this call and released when the method returns. Keepalive drain, lease drain, and QUIT all use its shared token.
        /// A grace timeout does not abandon remaining sessions. Sessions that cannot start QUIT before the deadline aborts the transport.
        /// A second call returns immediately. An unexpected retirement fault is thrown after the other sessions have been attempted
        /// and after the pool semaphores are disposed.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            using var deadline = NntpShutdownDeadline.FromBudget(_shutdownGrace);
            await DisposeCoreAsync(forceAfterGrace: true, CancellationToken.None, deadline).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancels shutdown, stops replenish and keepalive, waits for leases, then retires every session still tracked.
        /// </summary>
        /// <param name="forceAfterGrace">
        /// <see langword="true"/> when the caller is <see cref="DisposeAsync"/> and <paramref name="deadline"/> was created from <see cref="_shutdownGrace"/>.
        /// <see langword="false"/> when the caller supplied <paramref name="cancellationToken"/> and, for registry shutdown, <paramref name="deadline"/>.
        /// </param>
        /// <param name="cancellationToken">
        /// Ends the lease wait and an in-flight QUIT when cancelled.
        /// <see cref="DisposeAsync"/> passes <see cref="CancellationToken.None"/> because its deadline token is the grace signal.
        /// A bounded deadline ends the lease wait on that shared token as well. Replacement with an unbounded deadline waits only on this token.
        /// </param>
        /// <param name="deadline">
        /// Absolute shutdown instant. <see cref="NntpShutdownDeadline.None"/> is a replacement drain.
        /// Retirement then captures one <see cref="_shutdownGrace"/> deadline instead of giving each session its own QUIT allowance.
        /// </param>
        /// <remarks>
        /// The disposed flag is published under <see cref="_idleGate"/> before <see cref="_drained"/> is snapshotted under <see cref="_permitGate"/>.
        /// <see cref="TryReserveIssueSlot"/> cannot open a later generation after that snapshot, and the wait uses the captured generation.
        /// <see cref="BeforeLeaseDrainWait"/> observes that captured task and runs outside the lock. A second dispose returns before the first wait finishes.
        /// Timeout and cancellation of the lease wait are swallowed, and retirement still runs.
        /// A bounded wait uses <see cref="NntpShutdownDeadline.Cancellation"/>. Retirement then sees that same token and does not start another QUIT.
        /// <see cref="SuperviseReplenishAsync"/> logs an unexpected fill failure and keeps the replenish task alive until shutdown.
        /// A failure of that supervisor task itself is logged here and does not skip retirement.
        /// The replenish await is cancelled by the pool shutdown token before it is awaited.
        /// <see cref="RunReplenishAsync"/> observes that token on its delay and on the fill-gate wait, so the await ends when those calls observe the cancel.
        /// The task is not abandoned, and it is not given a second copy of <see cref="_shutdownGrace"/>.
        /// Every DATE loop that has started, including one already removed from <see cref="_keepAlives"/>, is waited only for the time left
        /// on <paramref name="deadline"/>, or for <see cref="_shutdownGrace"/> when that deadline is unbounded, and then abandoned if it is still running.
        /// That wait does not hold <see cref="_idleGate"/>.
        /// Retirement uses <see cref="NntpShutdownWork"/> so one stalled QUIT does not keep every other session from starting.
        /// When <paramref name="deadline"/> is bounded, every session receives it. When it is unbounded, retirement captures one
        /// <see cref="_shutdownGrace"/> instant and every session receives that instant.
        /// Permit waiters are drained before the pool semaphores are disposed. Disposing a <see cref="SemaphoreSlim"/> does not complete a
        /// queued <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>, and shutdown cancellation can still have that wait's continuation
        /// queued when <see cref="CancellationTokenSource.CancelAsync"/> returns. Disposing <see cref="_leases"/> in that window leaves the
        /// acquire blocked on the orphaned waiter. The drain yields until <see cref="_permitWaiters"/> is zero, then disposes the semaphores.
        /// If that does not happen within <see cref="_shutdownGrace"/>, one release is issued per remaining waiter so the wait can observe
        /// <see cref="_disposed"/> and leave without a permit. Semaphores stay undisposed when a waiter is still inside the wait after that release.
        /// </remarks>
        private async Task DisposeCoreAsync(
            bool forceAfterGrace,
            CancellationToken cancellationToken,
            NntpShutdownDeadline deadline)
        {
            TaskCompletionSource drained;
            lock (_idleGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }
            }

            lock (_permitGate)
            {
                drained = _drained;
            }

            await _shutdown.CancelAsync().ConfigureAwait(false);
            var replenish = _replenishTask;
            if (replenish is not null)
            {
                try
                {
                    await replenish.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ShutdownObservations.Canceled(_logger, "session replenish");
                }
                // codeql[cs/catch-of-all-exceptions] Keepalive and replenishment failures must retire or log the session and must not escape the background task.
                catch (Exception ex)
                {
                    NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
                }
            }

            await AwaitKeepAlivesAsync(deadline, cancellationToken).ConfigureAwait(false);
            if (_beforeLeaseDrainWait is { } beforeDrain)
            {
                await beforeDrain(drained.Task).ConfigureAwait(false);
            }

            try
            {
                if (deadline.IsBounded || !forceAfterGrace)
                {
                    await deadline.WaitAsync(drained.Task, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await drained.Task.WaitAsync(_shutdownGrace, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                ShutdownObservations.TimedOut(_logger, "pool dispose grace");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                ShutdownObservations.TimedOut(_logger, "pool dispose grace");
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "pool dispose grace");
            }

            if (_afterDrainObserved is { } afterDrain)
            {
                await afterDrain().ConfigureAwait(false);
            }

            try
            {
                if (!deadline.IsBounded)
                {
                    using var retirementDeadline = NntpShutdownDeadline.FromBudget(_shutdownGrace);
                    await RetireRemainingSessionsAsync(retirementDeadline, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await RetireRemainingSessionsAsync(deadline, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                var permitWaitersDrained = await DrainPermitWaitersBeforeDisposeAsync().ConfigureAwait(false);
                if (permitWaitersDrained)
                {
                    _leases.Dispose();
                    _fillGate.Dispose();
                    _shutdown.Dispose();
                }
                else
                {
                    ShutdownObservations.TimedOut(_logger, "permit waiter drain");
                }
            }
        }

        /// <summary>
        /// Waits until every <see cref="AcquirePermitAsync"/> waiter has left <see cref="_leases"/> before that semaphore is disposed.
        /// </summary>
        /// <returns><see langword="false"/> when a waiter is still counted after one wakeup and another <see cref="_shutdownGrace"/>.</returns>
        /// <remarks>
        /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> queues a continuation that removes the waiter after cancellation.
        /// <see cref="CancellationTokenSource.CancelAsync"/> can return before that continuation runs.
        /// Disposing the semaphore then drops the queue without completing the waiter, and the continuation waits forever on the unfinished node.
        /// This method yields until <see cref="_permitWaiters"/> is zero so that continuation runs first.
        /// A waiter that is still counted when <see cref="_shutdownGrace"/> elapses is woken with <see cref="SemaphoreSlim.Release(int)"/>.
        /// The grant path sees <see cref="_disposed"/> and does not take the permit. The semaphore is left alive if the waiter is still inside the wait.
        /// </remarks>
        private async Task<bool> DrainPermitWaitersBeforeDisposeAsync()
        {
            int blocked;
            lock (_permitGate)
            {
                blocked = _permitWaiters;
            }

            if (blocked > 0 && _beforePermitWaiterDrain is { } beforeDrain)
            {
                await beforeDrain().ConfigureAwait(false);
            }

            var started = Environment.TickCount64;
            var graceMs = Math.Max(0L, (long)_shutdownGrace.TotalMilliseconds);
            var woke = false;
            while (true)
            {
                lock (_permitGate)
                {
                    if (_permitWaiters == 0)
                    {
                        _permitWaitersAtSemaphoreDispose = 0;
                        return true;
                    }
                }

                if (Environment.TickCount64 - started >= graceMs)
                {
                    if (woke)
                    {
                        lock (_permitGate)
                        {
                            _permitWaitersAtSemaphoreDispose = _permitWaiters;
                        }

                        return false;
                    }

                    woke = true;
                    WakePermitWaiters();
                    started = Environment.TickCount64;
                }

                await Task.Yield();
            }
        }

        /// <summary>Releases one <see cref="_leases"/> count per permit waiter so a wait that missed shutdown cancellation can leave.</summary>
        private void WakePermitWaiters()
        {
            lock (_permitGate)
            {
                var waiters = _permitWaiters;
                if (waiters <= 0)
                {
                    return;
                }

                try
                {
                    _leases.Release(waiters);
                }
                catch (ObjectDisposedException)
                {
                    ShutdownObservations.AlreadyDisposed(_logger, "lease permit release");
                }
            }
        }

        /// <summary>
        /// Retires every session still in <see cref="_live"/> under one <paramref name="deadline"/>.
        /// </summary>
        /// <param name="deadline">Passed to each session dispose. Not recreated per session.</param>
        /// <param name="cancellationToken">Passed to each QUIT write. Already-cancelled callers skip graceful QUIT.</param>
        /// <remarks>
        /// The snapshot is taken after the lease wait, so a session issued before disposal is included.
        /// <see cref="RetireSessionAsync"/> removes idle membership. This method does not request replenishment.
        /// One session's cleanup fault does not skip the others. <see cref="NntpShutdownWork.RunBoundedAsync"/> throws after the set is attempted.
        /// </remarks>
        private Task RetireRemainingSessionsAsync(NntpShutdownDeadline deadline, CancellationToken cancellationToken)
        {
            var sessions = new List<NntpProviderSession>(_live.Count);
            foreach (var session in _live.Keys)
            {
                sessions.Add(session);
            }

            return NntpShutdownWork.RunBoundedAsync(
                sessions,
                session => RetireSessionAsync(
                    session,
                    "pool dispose",
                    replenish: false,
                    deadline: deadline,
                    cancellationToken: cancellationToken));
        }

        /// <summary>Returns a lease for a slot already reserved by <see cref="TryReserveIssueSlot"/>.</summary>
        /// <param name="session">Ready session the lease will own.</param>
        /// <returns>A lease whose disposal calls <see cref="ReturnAsync"/>.</returns>
        private NntpSessionLease Issue(NntpProviderSession session) => new(this, session);

        /// <summary>
        /// Takes one immediately available permit. Does not wait and does not observe cancellation.
        /// </summary>
        /// <returns><see langword="true"/> when a reservation was taken.</returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <remarks>
        /// Matches the fast path of <see cref="AcquirePermitAsync"/>. A miss leaves the counts unchanged so the waiting path can increment <see cref="_permitWaiters"/> under the same lock as its decision to wait.
        /// </remarks>
        private bool TryTakeAvailablePermit()
        {
            lock (_permitGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                if (_availablePermits <= 0)
                {
                    return false;
                }

                _availablePermits--;
                _permitReservations++;
                return true;
            }
        }

        /// <summary>
        /// Grants one acquire reservation from <see cref="_availablePermits"/>, or waits until <see cref="ReleasePermit"/> wakes this caller.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait. Cancellation does not leave a reservation or a waiter count.</param>
        /// <exception cref="ObjectDisposedException">The pool is disposed before a reservation is granted.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled while waiting.</exception>
        /// <remarks>
        /// The fast path takes <see cref="_permitGate"/> only long enough to decrement <see cref="_availablePermits"/> and increment <see cref="_permitReservations"/>.
        /// The wait itself does not hold that lock.
        /// A release is paired with a counted waiter. If that waiter fails and <see cref="SemaphoreSlim.Wait(int)"/> drains the release,
        /// the same count is posted again while <see cref="_permitWaiters"/> is still positive. It is not posted when no waiter remains,
        /// because the permit stays in <see cref="_availablePermits"/> for the next fast path.
        /// </remarks>
        private async Task AcquirePermitAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_permitGate)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                    if (_availablePermits > 0)
                    {
                        _availablePermits--;
                        _permitReservations++;
                        return;
                    }

                    _permitWaiters++;
                }

                try
                {
                    if (_beforePermitWait is { } beforeWait)
                    {
                        await beforeWait().ConfigureAwait(false);
                    }

                    await _leases.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    try
                    {
                        if (_beforeCancelledPermitDrain is { } beforeDrain)
                        {
                            await beforeDrain().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        lock (_permitGate)
                        {
                            _permitWaiters--;
                            var consumedRelease = false;
                            try
                            {
                                consumedRelease = _leases.Wait(0);
                            }
                            catch (ObjectDisposedException)
                            {
                                ShutdownObservations.AlreadyDisposed(_logger, "lease permit wait");
                            }

                            if (consumedRelease && _permitWaiters > 0)
                            {
                                try
                                {
                                    _leases.Release();
                                }
                                catch (ObjectDisposedException)
                                {
                                    ShutdownObservations.AlreadyDisposed(_logger, "lease permit release");
                                }
                            }
                        }
                    }

                    throw;
                }

                var granted = false;
                lock (_permitGate)
                {
                    _permitWaiters--;
                    if (Volatile.Read(ref _disposed) == 0 && _availablePermits > 0)
                    {
                        _availablePermits--;
                        _permitReservations++;
                        granted = true;
                    }
                }

                if (granted)
                {
                    return;
                }

                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            }
        }

        /// <summary>
        /// Drops one <see cref="_permitReservations"/> entry. Restores an available permit when the remaining reservations are below the current max.
        /// </summary>
        /// <remarks>
        /// A reservation above the current max is discarded. That is how a shrink stays in force after an in-flight acquire ends.
        /// When a permit is restored and waiters exist, one <see cref="_leases"/> signal is released. The lock is not held across an await.
        /// A disposed semaphore is logged and ignored.
        /// </remarks>
        private void ReleasePermit()
        {
            lock (_permitGate)
            {
                _permitReservations--;
                if (_permitReservations >= _provider.MaxSessions)
                {
                    return;
                }

                _availablePermits++;
                if (_permitWaiters <= 0)
                {
                    return;
                }

                try
                {
                    _leases.Release();
                }
                catch (ObjectDisposedException)
                {
                    ShutdownObservations.AlreadyDisposed(_logger, "lease permit release");
                }
            }
        }

        /// <summary>
        /// Sets <see cref="_availablePermits"/> to the current max minus <see cref="_permitReservations"/>, never below zero.
        /// </summary>
        /// <remarks>
        /// Previously removed permits are not counted as reservations. Growth adds the difference and wakes that many waiters.
        /// Shrink drops extra available permits without touching reservations that are already above the max.
        /// The caller must not hold <see cref="_permitGate"/>, <see cref="_idleGate"/>, or <see cref="_fillGate"/>.
        /// </remarks>
        private void AlignAvailablePermits()
        {
            lock (_permitGate)
            {
                var desired = Math.Max(0, _provider.MaxSessions - _permitReservations);
                if (_availablePermits == desired)
                {
                    return;
                }

                if (_availablePermits > desired)
                {
                    _availablePermits = desired;
                    return;
                }

                var add = desired - _availablePermits;
                _availablePermits = desired;
                var wake = Math.Min(add, _permitWaiters);
                if (wake <= 0)
                {
                    return;
                }

                try
                {
                    _leases.Release(wake);
                }
                catch (ObjectDisposedException)
                {
                    ShutdownObservations.AlreadyDisposed(_logger, "lease permit release");
                }
            }
        }

        /// <summary>
        /// Reserves an issued-lease slot when <see cref="_activeLeases"/> is still below the current max.
        /// The zero-to-one transition replaces <see cref="_drained"/> before the lock is released.
        /// </summary>
        /// <returns><see langword="false"/> when another issuance would exceed the current max or the pool is disposed.</returns>
        /// <remarks>
        /// The disposed check, the count change, and any replacement of <see cref="_drained"/> share <see cref="_permitGate"/>.
        /// A return that captured the previous generation cannot complete the source installed here.
        /// This method does not take <see cref="_idleGate"/>.
        /// </remarks>
        private bool TryReserveIssueSlot()
        {
            lock (_permitGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _activeLeases >= _provider.MaxSessions)
                {
                    return false;
                }

                if (Interlocked.Increment(ref _activeLeases) == 1)
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return true;
            }
        }

        /// <summary>
        /// Releases one issue slot. When the count reaches zero, completes the generation captured under <see cref="_permitGate"/>, not a generation installed later.
        /// </summary>
        /// <remarks>
        /// The count change and the capture share the lock. <see cref="TaskCompletionSource.TrySetResult"/> runs after the lock is released.
        /// Continuations use <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> and do not re-enter <see cref="_permitGate"/> on this stack.
        /// <see cref="BeforeDrainGenerationCompleted"/> is null in production and also runs outside the lock.
        /// </remarks>
        private Task CompleteIssuedLeaseAsync()
        {
            TaskCompletionSource? completed;
            lock (_permitGate)
            {
                completed = Interlocked.Decrement(ref _activeLeases) <= 0
                    ? _drained
                    : null;
            }

            if (completed is null)
            {
                return Task.CompletedTask;
            }

            if (_beforeDrainGenerationCompleted is { } gate)
            {
                return CompleteCapturedDrainAsync(completed, gate);
            }

            completed.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs <paramref name="gate"/> and then completes <paramref name="completed"/>, which was captured for the generation that reached zero leases.
        /// </summary>
        /// <param name="completed">Drain source selected under <see cref="_permitGate"/> before this method runs.</param>
        /// <param name="gate">Test pause. It must not be the production path.</param>
        /// <returns>A task that completes after <paramref name="completed"/> has been signaled.</returns>
        /// <remarks>
        /// The signal is in a finally block so a fault from the gate still completes the captured generation.
        /// The caller does not hold <see cref="_permitGate"/>.
        /// </remarks>
        private static async Task CompleteCapturedDrainAsync(TaskCompletionSource completed, Func<Task> gate)
        {
            try
            {
                await gate().ConfigureAwait(false);
            }
            finally
            {
                completed.TrySetResult();
            }
        }

        /// <summary>
        /// Issues <paramref name="session"/> when the current max still has an issue slot.
        /// A rejected capacity attempt returns the session to the pool and releases this acquire's reservation.
        /// </summary>
        /// <param name="session">Session removed from <see cref="_idle"/> or just created. The caller holds one permit reservation.</param>
        /// <returns>
        /// A lease when issued. <see cref="HeldIssue.RejectedForCapacity"/> when the reservation was released and the caller must wait again.
        /// Otherwise the session was not reusable and the caller still holds the reservation.
        /// </returns>
        private async Task<HeldIssue> TryIssueHeldSessionAsync(NntpProviderSession session)
        {
            if (_beforePermitIssuance is { } gate)
            {
                await gate().ConfigureAwait(false);
            }

            if (!TryReserveIssueSlot())
            {
                await RestoreUnissuedSessionAsync(session).ConfigureAwait(false);
                ReleasePermit();
                return new HeldIssue(null, true);
            }

            var lease = TryIssueReusable(session);
            if (lease is not null)
            {
                return new HeldIssue(lease, false);
            }

            await CompleteIssuedLeaseAsync().ConfigureAwait(false);
            return new HeldIssue(null, false);
        }

        /// <summary>Returns an unissued session to idle, or retires it when the live count is above the bound max.</summary>
        /// <param name="session">Session this acquire removed from idle or created and then could not issue.</param>
        private async Task RestoreUnissuedSessionAsync(NntpProviderSession session)
        {
            switch (TryAdmitIdle(session, enforceBound: true))
            {
                case IdleAdmission.Admitted:
                    await PublishKeepAliveAsync(session).ConfigureAwait(false);
                    break;
                case IdleAdmission.OverBound:
                    await RetireSessionAsync(
                            session,
                            "max sessions shrink",
                            replenish: false,
                            liveAlreadyDetached: true)
                        .ConfigureAwait(false);
                    break;
                default:
                    await RetireSessionAsync(session, "session is no longer reusable").ConfigureAwait(false);
                    break;
            }
        }

        /// <summary>Result of <see cref="TryIssueHeldSessionAsync"/>.</summary>
        /// <param name="Lease">The issued lease, or null when nothing was issued.</param>
        /// <param name="RejectedForCapacity">True when the reservation was released because the current max has no issue slot.</param>
        private readonly record struct HeldIssue(NntpSessionLease? Lease, bool RejectedForCapacity);

        /// <summary>Allocates a connection number, connects a session, and returns it only when it is <see cref="NntpSessionState.Ready"/>.</summary>
        /// <param name="cancellationToken">
        /// Cancels the establishment-gate wait and <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <returns>The ready session. It is in <see cref="_live"/> and is not yet idle.</returns>
        /// <exception cref="InvalidOperationException">
        /// <see cref="_created"/> would exceed <see cref="BackFillerProviderDefinition.MaxSessions"/>, no connection number is free,
        /// or a subscriber retired the session during publication before this method returned it.
        /// </exception>
        /// <exception cref="NntpProviderConnectException">
        /// Connect returned a failure, the session is not ready, or the transport threw <see cref="SocketException"/>.
        /// The created-count, connection number, and live membership for this attempt are released before this exception propagates.
        /// </exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> cancels the gate wait or connect. The same reservation is released.</exception>
        /// <remarks>
        /// The application-wide <see cref="_establishment"/> gate is held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/> (TCP through Ready). Slot allocation and
        /// session construction run before the wait so cancelled waiters do not leave half-open sockets.
        /// Every unsuccessful path after the <see cref="_created"/> increment releases that reservation exactly once.
        /// <see cref="RetireSessionAsync"/> does the release when the session was inserted into <see cref="_live"/>;
        /// a concurrent retire or dispose that already removed it does not decrement again.
        /// A reservation that never reached <see cref="_live"/> is released by <see cref="ReleaseUnregisteredReservation"/>.
        /// <see cref="NntpProviderSession.ConnectAsync"/> classifies <see cref="SocketException"/> as a provider failure.
        /// A socket exception that still escapes is translated to <see cref="NntpProviderConnectException"/> here so
        /// replenishment treats it as a failed attempt. The gate is released once, and only after this attempt acquired it.
        /// This method owns the reservation until it returns the session. The completion log and
        /// <see cref="ActiveSessionCountChanged"/> run before that return. If either throws, or a subscriber retires
        /// the session before the return, the session is retired when this method still owns it and the exception
        /// propagates. The caller does not receive the session. Expected disposal failures during that rollback are
        /// logged and do not replace the original exception. Accounting is committed before those observers run, so a
        /// later observer failure cannot skip the release or apply it twice.
        /// </remarks>
        private async Task<NntpProviderSession> CreateReadySessionAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _created) > _provider.MaxSessions)
            {
                Interlocked.Decrement(ref _created);
                throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
            }

            int connectionNumber;
            try
            {
                connectionNumber = AllocateConnectionNumber();
            }
            catch
            {
                Interlocked.Decrement(ref _created);
                throw;
            }

            NntpProviderSession session;
            try
            {
                session = new NntpProviderSession(_provider, _options, _logger, connectionNumber);
            }
            catch
            {
                ReleaseUnregisteredReservation(connectionNumber);
                throw;
            }

            var registered = false;
            var gateHeld = false;
            try
            {
                _live[session] = 0;
                registered = true;

                NntpLogMessages.SessionEstablishmentWaiting(_logger, _provider.Backbone, connectionNumber);
                await _establishment.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateHeld = true;

                NntpLogMessages.SessionEstablishmentStarted(_logger, _provider.Backbone, connectionNumber);
                var failure = await session.ConnectAsync(_transport, cancellationToken).ConfigureAwait(false);
                if (failure is null && session.State == NntpSessionState.Ready)
                {
                    NntpLogMessages.SessionEstablishmentCompleted(_logger, _provider.Backbone, connectionNumber);
                    NotifyActiveSessionCountChanged();
                    if (session.State == NntpSessionState.Ready && _live.ContainsKey(session))
                    {
                        return session;
                    }

                    throw new InvalidOperationException("NNTP session was retired during publication.");
                }

                if (failure?.Kind == ArticleRetrievalKind.Cancelled)
                {
                    TryLogSessionEstablishmentCancelled(connectionNumber);
                }
                else
                {
                    TryLogSessionEstablishmentFailed(connectionNumber, failure?.Reason ?? "connect failed");
                }

                throw new NntpProviderConnectException(
                    failure?.Kind ?? ArticleRetrievalKind.ProviderFailure,
                    failure?.StatusCode,
                    failure?.Reason ?? "NNTP connect failed.");
            }
            // codeql[cs/catch-of-all-exceptions] Failed creation must release its own reservation for every exception type and must not leave the session in the live set.
            catch (Exception ex)
            {
                if (registered && ex is not NntpProviderConnectException)
                {
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        TryLogSessionEstablishmentCancelled(connectionNumber);
                    }
                    else
                    {
                        TryLogSessionEstablishmentFailed(connectionNumber, ex.GetType().Name);
                    }
                }

                try
                {
                    await AbandonFailedCreationAsync(
                            session,
                            connectionNumber,
                            registered,
                            CreationFailureReason(ex, cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (Exception cleanup) when (IsExpectedSessionCleanupFailure(cleanup))
                {
                    TryLogSessionEstablishmentFailed(connectionNumber, cleanup.GetType().Name);
                }

                if (ex is SocketException)
                {
                    throw new NntpProviderConnectException(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        ex.GetType().Name);
                }

                throw;
            }
            finally
            {
                if (gateHeld)
                {
                    _establishment.Release();
                }
            }
        }

        /// <summary>
        /// Releases the capacity reserved for a session that did not become ready.
        /// </summary>
        /// <param name="session">Session constructed for this attempt.</param>
        /// <param name="connectionNumber">Slot reserved by <see cref="AllocateConnectionNumber"/>.</param>
        /// <param name="registered">
        /// <see langword="true"/> when <paramref name="session"/> was inserted into <see cref="_live"/>.
        /// </param>
        /// <param name="reason">Retirement text used when <paramref name="registered"/> is <see langword="true"/>.</param>
        /// <remarks>
        /// A registered session is removed through <see cref="RetireSessionAsync"/>, which decrements <see cref="_created"/>
        /// and frees the connection number only when this call wins <c>TryRemove</c>.
        /// A concurrent retire or dispose that already removed the session leaves the counters unchanged.
        /// An unregistered reservation is released by <see cref="ReleaseUnregisteredReservation"/> before the session is disposed.
        /// Expected disposal failures are logged and do not propagate. Replenishment is not requested.
        /// The periodic loop retries on its existing interval.
        /// </remarks>
        private async Task AbandonFailedCreationAsync(
            NntpProviderSession session,
            int connectionNumber,
            bool registered,
            string reason)
        {
            if (registered)
            {
                await RetireSessionAsync(session, reason, replenish: false).ConfigureAwait(false);
                return;
            }

            ReleaseUnregisteredReservation(connectionNumber);
            await DisposeDetachedSessionAsync(
                session,
                connectionNumber,
                NntpShutdownDeadline.None,
                CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Chooses the retirement text for a creation attempt that threw.</summary>
        /// <param name="failure">Exception that aborted the attempt.</param>
        /// <param name="cancellationToken">Token supplied to <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>
        /// The connect-failure message when <paramref name="failure"/> is <see cref="NntpProviderConnectException"/>.
        /// <c>NNTP connect was cancelled</c> when <paramref name="cancellationToken"/> cancelled an <see cref="OperationCanceledException"/>.
        /// Otherwise the exception type name, which matches <see cref="NntpProviderSession.ConnectAsync"/> failure reasons.
        /// </returns>
        private static string CreationFailureReason(Exception failure, CancellationToken cancellationToken)
        {
            if (failure is NntpProviderConnectException)
            {
                return failure.Message;
            }

            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return "NNTP connect was cancelled";
            }

            return failure.GetType().Name;
        }

        /// <summary>
        /// Returns a connection number and created-count reservation that never entered <see cref="_live"/>.
        /// </summary>
        /// <param name="connectionNumber">Slot from <see cref="AllocateConnectionNumber"/> for this attempt.</param>
        /// <remarks>
        /// Used only before live registration. After registration, <see cref="RetireSessionAsync"/> owns the same release.
        /// Calling both for one attempt frees another session's number or drops <see cref="_created"/> below the live set.
        /// </remarks>
        private void ReleaseUnregisteredReservation(int connectionNumber)
        {
            ReleaseConnectionNumber(connectionNumber);
            Interlocked.Decrement(ref _created);
        }

        /// <summary>
        /// Result of trying to place a live session into <see cref="_idle"/> under <see cref="_idleGate"/>.
        /// </summary>
        private enum IdleAdmission
        {
            /// <summary>The session is in <see cref="_idle"/> and may start DATE keepalive.</summary>
            Admitted,

            /// <summary>
            /// The live count was above the bound max. The session was detached from <see cref="_live"/> under
            /// <see cref="_idleGate"/> so concurrent admits observe the reduced count; the caller finishes disposal.
            /// </summary>
            OverBound,

            /// <summary>The session is already retired or not reusable. The caller retires it; a second retire is a no-op.</summary>
            NotReusable,
        }

        /// <summary>
        /// Removes the oldest idle session from <see cref="_idle"/> and detaches the DATE registration published for that membership.
        /// The caller cancels <paramref name="keepalive"/> after releasing <see cref="_idleGate"/>. Keepalive retirement will not dispose the session after this returns.
        /// </summary>
        /// <param name="keepalive">
        /// The registration that was current for this idle membership, or <see langword="null"/> when the list is empty or no loop was published.
        /// The caller must cancel this instance and must not look the session up again.
        /// </param>
        /// <returns>The session that left the idle list, or <see langword="null"/> when the list is empty.</returns>
        private NntpProviderSession? TryTakeIdleSession(out KeepAliveRegistration? keepalive)
        {
            lock (_idleGate)
            {
                var first = _idle.First;
                if (first is null)
                {
                    keepalive = null;
                    return null;
                }

                var session = first.Value;
                _idle.RemoveFirst();
                _ = _idleNodes.Remove(session);
                _ = _keepAlives.TryRemove(session, out keepalive);
                return session;
            }
        }

        /// <summary>
        /// Issues <paramref name="session"/> when shutdown has not started and the session is still live and reusable.
        /// The shutdown check, the live check, and the <see cref="_issued"/> insert share <see cref="_idleGate"/>.
        /// </summary>
        /// <param name="session">Live session removed from <see cref="_idle"/>, or a session just returned by <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>The new lease, or <see langword="null"/> when the caller must retire <paramref name="session"/>.</returns>
        private NntpSessionLease? TryIssueReusable(NntpProviderSession session)
        {
            lock (_idleGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_live.ContainsKey(session) || !session.IsReusable)
                {
                    return null;
                }

                _ = _issued.Add(session);
                return Issue(session);
            }
        }

        /// <summary>
        /// Puts <paramref name="session"/> at the tail of <see cref="_idle"/> when retirement has not already won.
        /// </summary>
        /// <param name="session">Session that was leased or just created.</param>
        /// <param name="enforceBound">
        /// <see langword="true"/> refuses a reusable session while <see cref="_live"/> is above the bound max.
        /// Creation passes <see langword="false"/> because the new session is already within the bound.
        /// </param>
        /// <returns>Whether the session is now idle, must be retired for shrink, or is not reusable.</returns>
        /// <remarks>
        /// Clears <see cref="_issued"/> for this session. A retirement that already removed it from <see cref="_live"/>
        /// cannot be undone by a late return. When DATE keepalive is enabled, the idle entry and that registration are
        /// published before the lock is released, so acquisition cannot observe one without the other.
        /// The DATE task is started before the lock and is cancelled if admission does not publish it.
        /// A published registration arms its first idle timer on this thread after the lock is released and before
        /// <see cref="KeepAliveRegistration.Publication"/> is signaled, so the timer exists before this method returns.
        /// A failure to arm that timer is stored for the loop and still releases the publication gate.
        /// The lock is not held across that task, the timer, or any await.
        /// </remarks>
        private IdleAdmission TryAdmitIdle(NntpProviderSession session, bool enforceBound)
        {
            var registration = CreateKeepAliveRegistration(session);
            var published = false;
            KeepAliveRegistration? overBoundKeepalive = null;
            IdleAdmission admission;
            try
            {
                lock (_idleGate)
                {
                    _ = _issued.Remove(session);
                    if (!_live.ContainsKey(session) || !session.IsReusable)
                    {
                        admission = IdleAdmission.NotReusable;
                    }
                    else if (enforceBound && _live.Count > _provider.MaxSessions)
                    {
                        // Detach under this lock so a concurrent return cannot also observe OverBound
                        // against the same elevated live count and retire a second session.
                        // Only OverBound when TryRemove wins — callers pass liveAlreadyDetached and must not
                        // dispose/decrement unless this session actually left _live here.
                        if (_idleNodes.Remove(session, out var overBoundNode))
                        {
                            _idle.Remove(overBoundNode);
                        }

                        if (_live.TryRemove(session, out _))
                        {
                            _ = _keepAlives.TryRemove(session, out overBoundKeepalive);
                            admission = IdleAdmission.OverBound;
                        }
                        else
                        {
                            admission = IdleAdmission.NotReusable;
                        }
                    }
                    else
                    {
                        if (!_idleNodes.ContainsKey(session))
                        {
                            var node = _idle.AddLast(session);
                            _idleNodes.Add(session, node);
                        }

                        if (registration is not null)
                        {
                            published = _keepAlives.TryAdd(session, registration);
                            registration.WasPublished = published;
                        }

                        admission = IdleAdmission.Admitted;
                        _idleKeepAliveLinearized?.Invoke(_idle.Count, _keepAlives.Count);
                    }
                }
            }
            finally
            {
                try
                {
                    if (registration is not null && published)
                    {
                        try
                        {
                            registration.ArmedDelay = Task.Delay(
                                TimeSpan.FromSeconds(_provider.KeepAliveSeconds),
                                _time,
                                registration.Cts.Token);
                        }
                        catch (Exception ex) when (!IsProcessIntegrityFailure(ex))
                        {
                            registration.DelayStartFailure = ex;
                        }
                    }
                }
                finally
                {
                    registration?.Publication.TrySetResult();
                    if (registration is not null && !published)
                    {
                        CancelKeepAliveRegistration(registration);
                    }

                    CancelKeepAliveRegistration(overBoundKeepalive);
                }
            }

            return admission;
        }

        /// <summary>
        /// Drops the DATE registration <see cref="TryAdmitIdle"/> just published when the session has already left the idle list.
        /// </summary>
        /// <param name="session">Session <see cref="TryAdmitIdle"/> just accepted.</param>
        /// <remarks>
        /// Idle membership and the registration were published together. <see cref="AfterKeepAlivePublished"/> and
        /// <see cref="KeepAliveStopGate"/> run outside <see cref="_idleGate"/>. Neither gate is set in production,
        /// so this method does not wait before the membership recheck.
        /// </remarks>
        private async Task PublishKeepAliveAsync(NntpProviderSession session)
        {
            if (_afterKeepAlivePublished is { } published)
            {
                await published().ConfigureAwait(false);
            }

            await StopKeepAliveUnlessIdleAsync(session).ConfigureAwait(false);
        }

        /// <summary>
        /// Detaches the DATE registration observed while <paramref name="session"/> is not idle, then cancels that registration after releasing <see cref="_idleGate"/>.
        /// </summary>
        /// <param name="session">Session that was just admitted and then may have been taken.</param>
        /// <remarks>
        /// The idle check and <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/> share one lock hold.
        /// A registration published for a later idle membership is not the one removed here, and it is not cancelled after the lock is released.
        /// <see cref="KeepAliveStopGate"/> runs only after that detach, and only outside the lock.
        /// </remarks>
        private async Task StopKeepAliveUnlessIdleAsync(NntpProviderSession session)
        {
            KeepAliveRegistration? detached = null;
            lock (_idleGate)
            {
                if (_idleNodes.ContainsKey(session))
                {
                    return;
                }

                _ = _keepAlives.TryRemove(session, out detached);
            }

            if (_keepAliveStopGate is { } gate)
            {
                await gate().ConfigureAwait(false);
            }

            CancelKeepAliveRegistration(detached);
        }

        /// <summary>Starts a DATE loop for <paramref name="session"/> when keepalive is enabled and the pool is not shutting down.</summary>
        /// <param name="session">Session that <see cref="TryAdmitIdle"/> may place in <see cref="_idle"/>.</param>
        /// <returns>
        /// The unpublished registration, or <see langword="null"/> when no loop should run.
        /// <see cref="TryAdmitIdle"/> publishes it only with the idle entry, and cancels it otherwise.
        /// The loop waits on <see cref="KeepAliveRegistration.Publication"/> before it waits or retires.
        /// A timer failure stored by <see cref="TryAdmitIdle"/> is thrown after that decision, so retirement still sees whether this registration was published.
        /// </returns>
        /// <remarks>
        /// The loop task is stored and recorded in <see cref="_keepAliveLifetimes"/> before <see cref="TryAdmitIdle"/> takes <see cref="_idleGate"/>.
        /// The loop waits for publication before it can exit, so that record exists before the loop can remove it.
        /// This method does not add the registration to <see cref="_keepAlives"/> and does not inspect idle membership.
        /// </remarks>
        private KeepAliveRegistration? CreateKeepAliveRegistration(NntpProviderSession session)
        {
            if (!_provider.DateKeepAliveEnabled || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return null;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var registration = new KeepAliveRegistration(cts);
            registration.Task = RunKeepAliveAsync(session, registration);
            _keepAliveLifetimes.TryAdd(registration, 0);
            return registration;
        }

        /// <summary>Cancels one DATE registration. Does not look up the session and does not wait for the loop to exit.</summary>
        /// <param name="registration">Registration detached for the membership decision that is ending. Null does nothing.</param>
        /// <remarks>
        /// Cancelling a source that the loop has already disposed is logged and ignored.
        /// A newer registration for the same session is a different instance and is left running.
        /// </remarks>
        private void CancelKeepAliveRegistration(KeepAliveRegistration? registration)
        {
            if (registration is null)
            {
                return;
            }

            try
            {
                registration.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                ShutdownObservations.AlreadyDisposed(_logger, "DATE keepalive stop");
            }
        }

        /// <summary>
        /// Returns the idle delay armed by <see cref="TryAdmitIdle"/> the first time, and a new delay for each later interval.
        /// </summary>
        /// <param name="registration">Published registration whose <see cref="KeepAliveRegistration.ArmedDelay"/> is consumed once.</param>
        /// <param name="interval">Configured idle interval used when <paramref name="registration"/> has no armed delay left.</param>
        /// <param name="cancellationToken">Cancels a delay created here. The armed delay already carries the registration token.</param>
        /// <returns>The delay the DATE loop waits on before the next DATE.</returns>
        /// <remarks>
        /// The first timer is created on the admission thread so a caller that advances a manual clock after admission observes that timer.
        /// This method does not take <see cref="_idleGate"/>.
        /// </remarks>
        private Task NextKeepAliveDelay(
            KeepAliveRegistration registration,
            TimeSpan interval,
            CancellationToken cancellationToken)
        {
            var armed = registration.ArmedDelay;
            if (armed is not null)
            {
                registration.ArmedDelay = null;
                return armed;
            }

            return Task.Delay(interval, _time, cancellationToken);
        }

        /// <summary>
        /// Waits <see cref="BackFillerProviderDefinition.KeepAliveSeconds"/>, then sends DATE without waiting for a busy article command.
        /// </summary>
        /// <param name="session">Idle session this loop owns.</param>
        /// <param name="registration">Registration created for this loop. Exit removes this instance only.</param>
        /// <remarks>
        /// A non-reusable session or a failed DATE retires <paramref name="session"/> only when <paramref name="registration"/> is still the published keepalive and the session is still idle.
        /// A replacement published after this loop decided to retire is left running. A dequeue that already removed the session keeps it and retires it itself when it is not reusable.
        /// <see cref="NntpProviderSession.SendDateKeepAliveAsync"/> is called with <see cref="_shutdown"/> and without waiting for the busy lock, so an in-use session skips DATE and stays leased.
        /// Cancellation of <paramref name="registration"/> ends the loop without retirement. Any other exception retires the session.
        /// A published loop logs that cancellation when its delay throws <see cref="OperationCanceledException"/> and when the loop observes the cancelled token before or between waits. An unpublished registration stays silent.
        /// <see cref="NntpProviderSession.SendDateKeepAliveAsync"/> catches cancellation itself. After DATE has been written that returns <see langword="false"/> and this loop retires the session instead of taking the cancellation log.
        /// The loop waits until <see cref="TryAdmitIdle"/> has decided publication. Its first wait is the timer armed on that admission thread. Publication resumes the loop asynchronously, so shutdown can cancel the token before that continuation runs.
        /// Exit removes <paramref name="registration"/> only when the dictionary still maps the session to that instance, and then disposes its source.
        /// A replacement published after this loop was superseded stays in the dictionary.
        /// <see cref="KeepAliveLoopExitGate"/> runs before that removal and outside <see cref="_idleGate"/>. It is null in production.
        /// </remarks>
        private async Task RunKeepAliveAsync(NntpProviderSession session, KeepAliveRegistration registration)
        {
            var cts = registration.Cts;
            var token = cts.Token;
            var interval = TimeSpan.FromSeconds(_provider.KeepAliveSeconds);
            using (cts)
                try
                {
                    await registration.Publication.Task.ConfigureAwait(false);
                    if (!registration.WasPublished)
                    {
                        return;
                    }

                    if (registration.DelayStartFailure is not null)
                    {
                        throw registration.DelayStartFailure;
                    }

                    if (_beforeFirstKeepAliveDelay is { } beforeDelay)
                    {
                        await beforeDelay().ConfigureAwait(false);
                    }

                    while (!token.IsCancellationRequested)
                    {
                        await NextKeepAliveDelay(registration, interval, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (_beforeKeepAliveReusabilityCheck is { } reusability)
                        {
                            await reusability().ConfigureAwait(false);
                        }

                        if (!session.IsReusable)
                        {
                            await RetireFromKeepAliveAsync(session, registration, "keepalive found a non-reusable session")
                                .ConfigureAwait(false);
                            return;
                        }

                        if (!await session.SendDateKeepAliveAsync(_shutdown.Token).ConfigureAwait(false))
                        {
                            await RetireFromKeepAliveAsync(session, registration, "DATE keepalive failed")
                                .ConfigureAwait(false);
                            return;
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        // Publication resumes asynchronously. Shutdown can cancel this token before the continuation
                        // reaches the delay, so the loop observes the flag instead of an OperationCanceledException.
                        ShutdownObservations.Canceled(_logger, "DATE keepalive");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ShutdownObservations.Canceled(_logger, "DATE keepalive");
                }
                // codeql[cs/catch-of-all-exceptions] Keepalive and replenishment failures must retire or log the session and must not escape the background task.
                catch (Exception)
                {
                    // The loop still owns the session only while its registration is the published one and the session remains idle.
                    // A dequeue that already took it, or a replacement keepalive, retires or keeps it.
                    await RetireFromKeepAliveAsync(session, registration, "DATE keepalive failed").ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (_keepAliveLoopExitGate is { } gate)
                        {
                            await gate(registration.Task).ConfigureAwait(false);
                        }

                        _ = _keepAlives.TryRemove(new KeyValuePair<NntpProviderSession, KeepAliveRegistration>(session, registration));
                    }
                    finally
                    {
                        _keepAliveLifetimes.TryRemove(registration, out _);
                    }
                }
        }

        /// <summary>
        /// Retires <paramref name="session"/> for <paramref name="registration"/> after the optional retirement gate.
        /// A registration that is no longer the published keepalive returns without removing idle membership or cancelling the replacement.
        /// </summary>
        /// <param name="session">Session the DATE loop observed.</param>
        /// <param name="registration">Registration that decided retirement was needed. Only this instance may retire.</param>
        /// <param name="reason">Text passed to <see cref="RetireSessionAsync"/>.</param>
        /// <remarks>
        /// <see cref="BeforeKeepAliveRetirement"/> runs outside <see cref="_idleGate"/> and is null in production.
        /// The registration check itself is inside <see cref="RetireSessionAsync"/>.
        /// </remarks>
        private async Task RetireFromKeepAliveAsync(
            NntpProviderSession session,
            KeepAliveRegistration registration,
            string reason)
        {
            if (_beforeKeepAliveRetirement is { } gate)
            {
                await gate(registration.Task).ConfigureAwait(false);
            }

            await RetireSessionAsync(
                    session,
                    reason,
                    onlyIfIdle: true,
                    actingRegistration: registration)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for DATE loops that have started and not yet exited, including registrations no longer in <see cref="_keepAlives"/>.
        /// </summary>
        /// <param name="deadline">
        /// When bounded, the wait uses <see cref="NntpShutdownDeadline.Cancellation"/> rather than a fresh timer.
        /// </param>
        /// <param name="cancellationToken">Ends the wait when the caller already cancelled shutdown. It does not cancel the DATE command token.</param>
        /// <remarks>
        /// The snapshot is taken without <see cref="_idleGate"/>. Timeout and cancellation are swallowed.
        /// A loop still running after the budget is abandoned so disposal can retire sessions.
        /// An unbounded deadline keeps the historical <see cref="_shutdownGrace"/> cap used by replacement drain.
        /// </remarks>
        private async Task AwaitKeepAlivesAsync(NntpShutdownDeadline deadline, CancellationToken cancellationToken)
        {
            var tasks = _keepAliveLifetimes.Keys.Select(static registration => registration.Task).ToArray();
            if (_keepAliveDrainSnapshot is { } snapshot)
            {
                await snapshot(tasks).ConfigureAwait(false);
            }

            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                if (deadline.IsBounded)
                {
                    await deadline.WaitAsync(Task.WhenAll(tasks), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAll(tasks).WaitAsync(_shutdownGrace, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                ShutdownObservations.TimedOut(_logger, "DATE keepalive drain");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                ShutdownObservations.TimedOut(_logger, "DATE keepalive drain");
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "DATE keepalive drain");
            }
        }

        /// <summary>
        /// Stops keepalive, frees the connection number, disposes <paramref name="session"/>, and optionally asks the pool to replace it.
        /// </summary>
        /// <param name="session">Live session to drop. A session that is already absent is left alone.</param>
        /// <param name="reason">Text passed to <see cref="NntpLogMessages.SessionRetired"/>.</param>
        /// <param name="replenish"><see langword="true"/> schedules <see cref="RequestReplenish"/> after a session was actually removed.</param>
        /// <param name="onlyIfIdle">
        /// <see langword="true"/> when the caller is the DATE loop. The session is disposed only if it is still in
        /// <see cref="_idle"/>. A dequeue or lease that already took it keeps ownership and is not disposed here.
        /// </param>
        /// <param name="actingRegistration">
        /// DATE registration that initiated this retirement. When set, retirement proceeds only when this instance is still
        /// the published keepalive, or it was never published and no other registration is current.
        /// A registration that was published and then removed, or that lost to a newer registration, returns before idle
        /// membership or that newer registration is touched. Null for callers that are not a DATE loop.
        /// </param>
        /// <param name="deadline">
        /// Shutdown instant forwarded to session dispose. The default is <see cref="NntpShutdownDeadline.None"/>,
        /// which keeps the session's own QUIT allowance for retirement outside a shared shutdown.
        /// </param>
        /// <param name="cancellationToken">
        /// Caller cancellation forwarded only to the session QUIT write. The default does not cancel that write early.
        /// </param>
        /// <param name="liveAlreadyDetached">
        /// <see langword="true"/> when <see cref="TryAdmitIdle"/> already removed this session from <see cref="_live"/>
        /// for an <see cref="IdleAdmission.OverBound"/> decision. Skips the membership lock and finishes disposal only.
        /// </param>
        /// <remarks>
        /// <c>TryRemove</c> wins the decrement and connection-number release. A second retire, dispose, or creation rollback
        /// observes the loss and does not change the counters again. Idle membership is removed under <see cref="_idleGate"/>
        /// before those releases, and before the retirement log, the active-count notification, and disposal. A retired
        /// session therefore does not stay reachable from <see cref="_idle"/> until a later <see cref="AcquireAsync"/>.
        /// An issued session stays in <see cref="_live"/> when <paramref name="onlyIfIdle"/> is set, so <see cref="ReturnAsync"/>
        /// is the one retire or re-enqueue. Observer failures are contained by <see cref="SignalSessionRetired"/>
        /// so they cannot skip disposal, decrement twice, or recurse through another retire. Expected disposal failures
        /// are logged. Any other disposal exception still requests replenishment when <paramref name="replenish"/> is
        /// <see langword="true"/>, then propagates.
        /// </remarks>
        private async Task RetireSessionAsync(
            NntpProviderSession session,
            string reason,
            bool replenish = true,
            bool onlyIfIdle = false,
            KeepAliveRegistration? actingRegistration = null,
            NntpShutdownDeadline deadline = default,
            CancellationToken cancellationToken = default,
            bool liveAlreadyDetached = false)
        {
            var won = false;
            KeepAliveRegistration? keepalive = null;
            if (liveAlreadyDetached)
            {
                // TryAdmitIdle(OverBound) already removed this session from live/idle under _idleGate.
                won = true;
            }
            else
            {
                lock (_idleGate)
                {
                    if (actingRegistration is not null)
                    {
                        if (_keepAlives.TryGetValue(session, out var active))
                        {
                            if (!ReferenceEquals(active, actingRegistration))
                            {
                                return;
                            }
                        }
                        else if (actingRegistration.WasPublished)
                        {
                            return;
                        }
                    }

                    if (onlyIfIdle && !_idleNodes.ContainsKey(session))
                    {
                        return;
                    }

                    if (_issued.Contains(session))
                    {
                        if (onlyIfIdle)
                        {
                            return;
                        }

                        _ = _issued.Remove(session);
                    }

                    if (_idleNodes.Remove(session, out var node))
                    {
                        _idle.Remove(node);
                    }

                    won = _live.TryRemove(session, out _);
                    if (won)
                    {
                        _ = _keepAlives.TryRemove(session, out keepalive);
                    }
                }
            }

            if (!won)
            {
                return;
            }

            CancelKeepAliveRegistration(keepalive);
            Interlocked.Decrement(ref _created);
            ReleaseConnectionNumber(session.ConnectionNumber);
            SignalSessionRetired(session, reason);
            try
            {
                if (_beforeDetachedSessionDispose is { } beforeDispose)
                {
                    await beforeDispose().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await DisposeDetachedSessionAsync(session, session.ConnectionNumber, deadline, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (replenish)
                    {
                        RequestReplenish();
                    }
                }
            }
        }

        /// <summary>
        /// Logs retirement and notifies <see cref="ActiveSessionCountChanged"/> after the session has left <see cref="_live"/>.
        /// </summary>
        /// <param name="session">Session already removed from <see cref="_live"/>. Its connection number is used only in a diagnostic.</param>
        /// <param name="reason">Text passed to <see cref="NntpLogMessages.SessionRetired"/>.</param>
        /// <remarks>
        /// Both calls are observers. Ordinary failures are contained so <see cref="RetireSessionAsync"/> still disposes
        /// the session once and does not enter retirement again. Process-integrity failures propagate.
        /// <see cref="BeforeDetachedSessionDispose"/> runs after this notification and before that dispose. A fault from the gate still disposes the session.
        /// A logger that fails is not asked to record its own failure. A subscriber failure is recorded when the logger still works.
        /// </remarks>
        private void SignalSessionRetired(NntpProviderSession session, string reason)
        {
            try
            {
                NntpLogMessages.SessionRetired(_logger, _provider.Backbone, reason);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // Accounting is already committed. The failing logger cannot record this diagnostic. Disposal still runs.
            }

            try
            {
                NotifyActiveSessionCountChanged();
            }
            catch (Exception observer) when (!IsProcessIntegrityFailure(observer))
            {
                TryLogSessionEstablishmentFailed(session.ConnectionNumber, observer.GetType().Name);
            }
        }

        /// <summary>
        /// Disposes a session whose created-count and connection number have already been released.
        /// </summary>
        /// <param name="session">Detached session.</param>
        /// <param name="connectionNumber">Connection number already returned to the free set. Used only in the cleanup diagnostic.</param>
        /// <param name="deadline">Shared shutdown instant. Forwarded unchanged so this session does not receive a new QUIT budget.</param>
        /// <param name="cancellationToken">Caller cancellation forwarded only to the QUIT write.</param>
        /// <remarks>
        /// <see cref="OperationCanceledException"/>, <see cref="SocketException"/>, <see cref="IOException"/>, and
        /// <see cref="ObjectDisposedException"/> are logged and do not propagate.
        /// <see cref="ObjectDisposedException"/> is expected when pool dispose and session dispose close the same
        /// transport, and <see cref="CancelKeepAliveRegistration"/> already treats it as shutdown. Other exceptions propagate so a
        /// programming error is not hidden. This method does not change pool accounting.
        /// </remarks>
        private async Task DisposeDetachedSessionAsync(
            NntpProviderSession session,
            int connectionNumber,
            NntpShutdownDeadline deadline,
            CancellationToken cancellationToken)
        {
            try
            {
                await session.DisposeForShutdownAsync(deadline, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (IsExpectedSessionCleanupFailure(cleanup))
            {
                TryLogSessionEstablishmentFailed(connectionNumber, cleanup.GetType().Name);
            }
        }

        /// <summary>Reports whether <paramref name="exception"/> is an expected failure while disposing a detached session.</summary>
        /// <param name="exception">Exception thrown by session disposal or another cleanup call on that session.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="OperationCanceledException"/>, <see cref="SocketException"/>,
        /// <see cref="IOException"/>, and <see cref="ObjectDisposedException"/>.
        /// </returns>
        /// <remarks>
        /// <see cref="ObjectDisposedException"/> is included because concurrent pool and session disposal close the same
        /// transport and cancellation source. Programming errors are excluded so they still propagate after the reservation
        /// has been released.
        /// </remarks>
        private static bool IsExpectedSessionCleanupFailure(Exception exception) =>
            exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException;

        /// <summary>Starts <see cref="SuperviseReplenishAsync"/> once for this pool.</summary>
        /// <remarks>A later fill failure does not start a second supervisor. The running supervisor waits one interval and tries again.</remarks>
        private void StartPeriodicReplenish()
        {
            if (Interlocked.Exchange(ref _replenishStarted, 1) == 1)
            {
                return;
            }

            _replenishTask = SuperviseReplenishAsync(_shutdown.Token);
        }

        /// <summary>Starts the periodic loop if needed and runs one observed fill unless the pool is disposed or shutting down.</summary>
        /// <remarks>The observed fill is not awaited. Its failures are logged by <see cref="FillDeficitObservedAsync"/>.</remarks>
        private void RequestReplenish()
        {
            if (Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            StartPeriodicReplenish();
            _ = FillDeficitObservedAsync(_shutdown.Token);
        }

        /// <summary>Fills the deficit once every <see cref="ReplenishInterval"/> until <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation propagates to <see cref="SuperviseReplenishAsync"/>.</param>
        /// <remarks>
        /// Connect failures do not reach this method. <see cref="TryCreateIdleSessionAsync"/> consumes
        /// <see cref="NntpProviderConnectException"/> and <see cref="InvalidOperationException"/>, and
        /// <see cref="CreateReadySessionAsync"/> translates an escaping <see cref="SocketException"/> into
        /// <see cref="NntpProviderConnectException"/> before it leaves. Those attempts end the current round only.
        /// Any other exception, including shutdown cancellation, propagates to <see cref="SuperviseReplenishAsync"/>.
        /// The wait is <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="_time"/>.
        /// <see cref="TimeProvider.System"/> keeps <see cref="ReplenishInterval"/>.
        /// </remarks>
        private async Task RunReplenishAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ReplenishInterval, _time, cancellationToken).ConfigureAwait(false);
                await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Restarts <see cref="RunReplenishAsync"/> after an unexpected fill failure and stops it on shutdown.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation returns without another pass.</param>
        /// <remarks>
        /// <see cref="NntpProviderConnectException"/> and <see cref="SocketException"/> do not escape
        /// <see cref="FillDeficitAsync"/>: the session classifies a socket failure, and
        /// <see cref="TryCreateIdleSessionAsync"/> consumes the resulting connect exception and
        /// <see cref="InvalidOperationException"/>. Catching those types here would not run.
        /// Any other non-process-integrity exception is an open set. It is logged at warning and the next pass
        /// begins with another <see cref="ReplenishInterval"/> wait inside <see cref="RunReplenishAsync"/>, so the
        /// retry is not immediate and the pool is not left underfilled. Process-integrity failures fault this task.
        /// <see cref="DisposeCoreAsync"/> logs that fault and still retires sessions. Shutdown returns without another pass.
        /// </remarks>
        private async Task SuperviseReplenishAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunReplenishAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ShutdownObservations.Canceled(_logger, "session fill");
                    return;
                }
                catch (Exception ex) when (!IsProcessIntegrityFailure(ex))
                {
                    NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
                }
            }
        }

        /// <summary>
        /// Runs one unawaited fill started by <see cref="RequestReplenish"/>.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token forwarded to the fill.</param>
        /// <remarks>
        /// Nothing awaits this task. Shutdown cancellation is logged and is not a replenishment failure.
        /// Any other non-process-integrity exception is logged and does not replace an exception already thrown to an
        /// awaited caller. Process-integrity failures fault this unawaited task instead of being discarded.
        /// </remarks>
        private async Task FillDeficitObservedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ShutdownObservations.Canceled(_logger, "observed session fill");
            }
            // codeql[cs/catch-of-all-exceptions] This unawaited fill has no caller. Non-process-integrity failures are logged so the task does not become unobserved. Process-integrity failures are excluded by the filter.
            catch (Exception ex) when (!IsProcessIntegrityFailure(ex))
            {
                NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
            }
        }

        /// <summary>
        /// Creates idle sessions until the live count reaches the bound max or one create fails.
        /// A connect failure ends this round. <see cref="SuperviseReplenishAsync"/> waits <see cref="ReplenishInterval"/> before the next pass.
        /// </summary>
        /// <param name="cancellationToken">Cancels the fill-gate wait and session creation. A disposed pool returns without creating.</param>
        private async Task FillDeficitAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (!cancellationToken.IsCancellationRequested
                       && Volatile.Read(ref _disposed) == 0
                       && _live.Count < _provider.MaxSessions)
                {
                    if (!await TryCreateIdleSessionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _fillGate.Release();
            }
        }

        /// <summary>Connects one session and queues it when it becomes ready.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>
        /// <see langword="true"/> when an idle session was queued.
        /// <see langword="false"/> when connect failed, including a <see cref="SocketException"/> reported as <see cref="NntpProviderConnectException"/>, or the pool is already at its max.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        /// <remarks>
        /// <see cref="NntpProviderConnectException"/> and <see cref="InvalidOperationException"/> end the attempt and return
        /// <see langword="false"/>. <see cref="SocketException"/> is translated before it leaves
        /// <see cref="CreateReadySessionAsync"/>, so it does not reach <see cref="RunReplenishAsync"/>.
        /// Any other exception propagates to <see cref="SuperviseReplenishAsync"/>, which logs it and waits one
        /// <see cref="ReplenishInterval"/> before the next pass. <see cref="FillDeficitObservedAsync"/> logs the same
        /// exception for an unawaited fill.
        /// </remarks>
        private async Task<bool> TryCreateIdleSessionAsync(CancellationToken cancellationToken)
        {
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                var session = await CreateReadySessionAsync(cancellationToken).ConfigureAwait(false);
                if (TryAdmitIdle(session, enforceBound: false) != IdleAdmission.Admitted)
                {
                    await RetireSessionAsync(session, "session is no longer reusable").ConfigureAwait(false);
                    return false;
                }

                await PublishKeepAliveAsync(session).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NntpProviderConnectException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Invokes <see cref="ActiveSessionCountChanged"/> synchronously for the latest pool state.
        /// </summary>
        /// <remarks>
        /// One callback runs at a time, so a subscriber cannot re-enter notification on the same stack.
        /// A caller that arrives during that callback records a pending delivery and returns. The active caller
        /// checks for that pending delivery after the callback returns, including when the callback throws, and
        /// invokes subscribers again. The pending flag is cleared only by the active caller, and the idle transition
        /// loses to a concurrent pending mark, so an update that arrives as the callback finishes is replayed or
        /// starts a new delivery. Subscribers are not invoked while this method holds a pool lock.
        /// An ordinary subscriber exception is replayed past, then rethrown, so publication can still retire the
        /// session. Process-integrity failures propagate without a replay.
        /// </remarks>
        /// <exception cref="Exception">The first ordinary subscriber exception from this delivery, after any pending replay.</exception>
        private void NotifyActiveSessionCountChanged()
        {
            if (!TryBecomeCapacityNotifier())
            {
                return;
            }

            Exception? firstObserverFailure = null;
            var released = false;
            try
            {
                while (true)
                {
                    try
                    {
                        ActiveSessionCountChanged?.Invoke();
                    }
                    catch (Exception observer) when (!IsProcessIntegrityFailure(observer))
                    {
                        firstObserverFailure ??= observer;
                    }

                    if (Interlocked.CompareExchange(
                            ref _capacityNotification,
                            CapacityNotificationActive,
                            CapacityNotificationPending) == CapacityNotificationPending)
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(
                            ref _capacityNotification,
                            CapacityNotificationIdle,
                            CapacityNotificationActive) == CapacityNotificationActive)
                    {
                        released = true;
                        break;
                    }
                }
            }
            finally
            {
                if (!released)
                {
                    Interlocked.Exchange(ref _capacityNotification, CapacityNotificationIdle);
                }
            }

            if (firstObserverFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstObserverFailure).Throw();
            }
        }

        /// <summary>
        /// Becomes the single caller running <see cref="NotifyActiveSessionCountChanged"/>, or records a pending delivery.
        /// </summary>
        /// <returns><see langword="true"/> when the caller must invoke subscribers. <see langword="false"/> when another caller will deliver this update.</returns>
        private bool TryBecomeCapacityNotifier()
        {
            while (true)
            {
                var state = Volatile.Read(ref _capacityNotification);
                if (state == CapacityNotificationIdle
                    && Interlocked.CompareExchange(
                        ref _capacityNotification,
                        CapacityNotificationActive,
                        CapacityNotificationIdle) == CapacityNotificationIdle)
                {
                    return true;
                }

                if (state == CapacityNotificationPending)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _capacityNotification,
                        CapacityNotificationPending,
                        CapacityNotificationActive) == CapacityNotificationActive)
                {
                    return false;
                }
            }
        }

        /// <summary>Writes a cancelled-establishment diagnostic. A logger failure is ignored.</summary>
        /// <param name="connectionNumber">Connection number for the attempt being abandoned.</param>
        /// <remarks>
        /// Called before reservation release. <see cref="ILogger.Log{TState}"/> has no closed exception set.
        /// Ordinary logger failures are ignored so they cannot skip that release or replace the creation exception.
        /// Process-integrity failures propagate. A second log would call the same failing logger.
        /// </remarks>
        private void TryLogSessionEstablishmentCancelled(int connectionNumber)
        {
            try
            {
                NntpLogMessages.SessionEstablishmentCancelled(_logger, _provider.Backbone, connectionNumber);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // The failing logger cannot record this diagnostic.
            }
        }

        /// <summary>Writes an establishment-failure diagnostic. A logger failure is ignored.</summary>
        /// <param name="connectionNumber">Connection number for the attempt. Already released when this records cleanup.</param>
        /// <param name="reason">Exception type name or connect-failure text. No credentials or socket message.</param>
        /// <remarks>
        /// Used both before rollback and after accounting is committed. <see cref="ILogger.Log{TState}"/> has no closed
        /// exception set. Ordinary logger failures are ignored so the original establishment outcome is preserved and
        /// disposal can continue. Process-integrity failures propagate. A second log would call the same failing logger.
        /// </remarks>
        private void TryLogSessionEstablishmentFailed(int connectionNumber, string reason)
        {
            try
            {
                NntpLogMessages.SessionEstablishmentFailed(_logger, _provider.Backbone, connectionNumber, reason);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // The failing logger cannot record this diagnostic.
            }
        }

        /// <summary>Reports whether <paramref name="exception"/> must leave an observer or supervisor boundary.</summary>
        /// <param name="exception">Failure from a subscriber, logger, or replenishment pass.</param>
        /// <returns>The same value as <see cref="ExceptionHelpers.IsFatal"/>.</returns>
        /// <remarks>
        /// Delegates to the shared process-integrity policy so pool observers and RabbitMQ notification
        /// boundaries cannot drift apart.
        /// </remarks>
        private static bool IsProcessIntegrityFailure(Exception exception) =>
            ExceptionHelpers.IsFatal(exception);

        /// <summary>Reserves the lowest unused connection number from 1 through the current max.</summary>
        /// <returns>The reserved one-based slot.</returns>
        /// <exception cref="InvalidOperationException">Every slot in that range is already reserved.</exception>
        private int AllocateConnectionNumber()
        {
            lock (_slotGate)
            {
                var limit = _provider.MaxSessions;
                for (var slot = 1; slot <= limit; slot++)
                {
                    if (_usedSlots.Add(slot))
                    {
                        return slot;
                    }
                }
            }

            throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
        }

        /// <summary>Returns <paramref name="connectionNumber"/> to the free set. A number that is not reserved is ignored.</summary>
        /// <param name="connectionNumber">Slot previously returned by <see cref="AllocateConnectionNumber"/>.</param>
        private void ReleaseConnectionNumber(int connectionNumber)
        {
            lock (_slotGate)
            {
                _usedSlots.Remove(connectionNumber);
            }
        }

        /// <summary>
        /// Waits until the DATE or ARTICLE command that made <paramref name="session"/> busy releases the command gate.
        /// </summary>
        /// <param name="session">Session just taken from the idle queue.</param>
        /// <param name="cancellationToken">Cancels the wait. It does not cancel the command.</param>
        /// <returns>A completed task when <paramref name="session"/> is not busy. Otherwise the command-gate wait.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled while the gate is still held.</exception>
        /// <remarks>
        /// The caller has already detached the idle keepalive registration. The in-flight DATE still runs on the pool shutdown token and holds <see cref="NntpProviderSession"/>'s command gate until its response is consumed or the attempt fails.
        /// This method does not poll <see cref="NntpSessionState"/>. A later acquire attempt reads <see cref="NntpProviderSession.IsReusable"/> after the wait.
        /// </remarks>
        private static Task WaitUntilNotBusyAsync(NntpProviderSession session, CancellationToken cancellationToken)
        {
            if (session.State != NntpSessionState.Busy)
            {
                return Task.CompletedTask;
            }

            return session.WaitForCurrentCommandAsync(cancellationToken);
        }

        /// <summary>Cancellation and task for one idle session's DATE loop.</summary>
        /// <param name="cts">Linked to pool shutdown. Disposed when <see cref="RunKeepAliveAsync"/> exits.</param>
        private sealed class KeepAliveRegistration(CancellationTokenSource cts)
        {
            /// <summary>Cancels the DATE loop. Cancelling does not dispose this source.</summary>
            internal CancellationTokenSource Cts { get; } = cts;

            /// <summary>
            /// Loop task. Starts as <see cref="Task.CompletedTask"/> and is replaced before the registration is published.
            /// </summary>
            internal Task Task { get; set; } = Task.CompletedTask;

            /// <summary>
            /// Completed by <see cref="TryAdmitIdle"/> after it decides whether this registration is published.
            /// The loop waits here before its first delay. Runs continuations asynchronously so that decision can finish
            /// without re-entering <see cref="_idleGate"/>.
            /// </summary>
            internal TaskCompletionSource Publication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// Set under <see cref="_idleGate"/> when this instance is added to <see cref="_keepAlives"/>.
            /// A retirement whose registration was published and is no longer the dictionary value does not own the session.
            /// A loop that fails before publication leaves this false and may still retire an idle session.
            /// </summary>
            internal bool WasPublished { get; set; }

            /// <summary>
            /// First idle interval, created on the admission thread after this registration is published and before
            /// <see cref="Publication"/> is signaled. Null when admission did not publish this registration.
            /// The loop consumes it once so a later interval is not this same task.
            /// </summary>
            internal Task? ArmedDelay { get; set; }

            /// <summary>
            /// Failure from creating <see cref="ArmedDelay"/> on the admission thread.
            /// The loop throws it after publication so the session is retired without leaving this task blocked.
            /// </summary>
            internal Exception? DelayStartFailure { get; set; }
        }
    }

    /// <summary>Connect-time failure that preserves retrieval classification.</summary>
    internal sealed class NntpProviderConnectException : InvalidOperationException
    {
        /// <summary>Wraps a failed connect so callers map the failure to <see cref="ArticleRetrievalKind"/> without parsing the message.</summary>
        /// <param name="kind">Retrieval classification copied from the failed connect.</param>
        /// <param name="statusCode">NNTP status when the server produced one; otherwise null.</param>
        /// <param name="reason">Exception message. Diagnostic text from the session.</param>
        internal NntpProviderConnectException(ArticleRetrievalKind kind, int? statusCode, string reason)
            : base(reason)
        {
            Kind = kind;
            StatusCode = statusCode;
        }

        /// <summary>Article Work retrieval kind assigned when connect failed before Ready.</summary>
        internal ArticleRetrievalKind Kind { get; }

        /// <summary>Three-digit NNTP response code when the server returned one during connect; otherwise null.</summary>
        internal int? StatusCode { get; }
    }
}
