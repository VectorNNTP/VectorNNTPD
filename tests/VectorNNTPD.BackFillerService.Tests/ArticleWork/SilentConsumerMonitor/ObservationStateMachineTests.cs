using VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Regression for a single <see cref="ObservationKey"/>: open-window timing, continued-open ticks, resolve-window recovery,
    /// and Unknown signals that freeze an open alert without opening or resolving prematurely.
    /// </summary>
    public sealed class ObservationStateMachineTests
    {
        /// <summary>
        /// Active condition must persist for the full open window before <see cref="ObservationTransition.Opened"/>;
        /// subsequent Active ticks while open yield <see cref="ObservationTransition.Continued"/>.
        /// </summary>
        [Fact]
        public void Opens_after_window_once()
        {
            var key = new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ProcessingStall, "q");
            var machine = new ObservationStateMachine(key, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
            var t0 = DateTimeOffset.Parse("2026-10-25T20:00:00Z");
            Assert.Equal(ObservationTransition.None, machine.Apply(ConditionSignal.Active, t0).Transition);
            Assert.Equal(ObservationTransition.Opened, machine.Apply(ConditionSignal.Active, t0.AddSeconds(30)).Transition);
            Assert.Equal(ObservationTransition.Continued, machine.Apply(ConditionSignal.Active, t0.AddSeconds(40)).Transition);
        }

        /// <summary>
        /// After open, <see cref="ConditionSignal.Unknown"/> produces no transition and leaves phase Open;
        /// Inactive must run through the resolve window before <see cref="ObservationTransition.Resolved"/>.
        /// </summary>
        [Fact]
        public void Unknown_does_not_open_or_resolve()
        {
            // Contract: Unknown freezes an open alert; it is neither recovery nor a new open.
            var key = new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ProcessingStall, "q");
            var machine = new ObservationStateMachine(key, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            var t0 = DateTimeOffset.Parse("2026-10-25T20:00:00Z");
            Assert.Equal(ObservationTransition.None, machine.Apply(ConditionSignal.Active, t0).Transition);
            Assert.Equal(ObservationTransition.Opened, machine.Apply(ConditionSignal.Active, t0.AddSeconds(1)).Transition);
            Assert.Equal(ObservationTransition.None, machine.Apply(ConditionSignal.Unknown, t0.AddSeconds(2)).Transition);
            Assert.Equal(ObservationPhase.Open, machine.Phase);
            Assert.Equal(ObservationTransition.Continued, machine.Apply(ConditionSignal.Inactive, t0.AddSeconds(2)).Transition);
            Assert.Equal(ObservationTransition.Resolved, machine.Apply(ConditionSignal.Inactive, t0.AddSeconds(3)).Transition);
        }

        /// <summary>
        /// Unknown during Clear-phase Active accumulation must not count toward the open window
        /// (Active bookends with a long Unknown gap must not Open).
        /// </summary>
        [Fact]
        public void Unknown_freezes_active_open_persistence_while_clear()
        {
            var key = new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.DispatchStall, "q");
            var open = TimeSpan.FromSeconds(90);
            var machine = new ObservationStateMachine(key, open, TimeSpan.FromSeconds(20));
            var t0 = DateTimeOffset.Parse("2026-10-26T15:00:00Z");

            Assert.Equal(ObservationTransition.None, machine.Apply(ConditionSignal.Active, t0).Transition);
            var unknown = machine.Apply(ConditionSignal.Unknown, t0.AddSeconds(60));
            Assert.Equal(ObservationTransition.None, unknown.Transition);
            Assert.Equal(ObservationPhase.Clear, unknown.Phase);
            Assert.Equal(TimeSpan.Zero, unknown.Persistence);

            // Wall clock from first Active would be 90s, but Unknown froze the clock — still Clear.
            var stillClear = machine.Apply(ConditionSignal.Active, t0.AddSeconds(90));
            Assert.Equal(ObservationTransition.None, stillClear.Transition);
            Assert.Equal(ObservationPhase.Clear, stillClear.Phase);
            Assert.True(stillClear.Persistence < open);

            Assert.Equal(
                ObservationTransition.Opened,
                machine.Apply(ConditionSignal.Active, t0.AddSeconds(90) + open).Transition);
        }

        /// <summary>
        /// Unknown during Open-phase Inactive accumulation must not count toward the resolve window.
        /// </summary>
        [Fact]
        public void Unknown_freezes_inactive_resolve_persistence_while_open()
        {
            var key = new ObservationKey(ObservationScope.SharedQueue, SilentConsumerCondition.ConsumerLoss, "q");
            var resolve = TimeSpan.FromSeconds(20);
            var machine = new ObservationStateMachine(key, TimeSpan.FromSeconds(1), resolve);
            var t0 = DateTimeOffset.Parse("2026-10-26T15:00:00Z");

            Assert.Equal(ObservationTransition.None, machine.Apply(ConditionSignal.Active, t0).Transition);
            Assert.Equal(ObservationTransition.Opened, machine.Apply(ConditionSignal.Active, t0.AddSeconds(1)).Transition);
            Assert.Equal(ObservationTransition.Continued, machine.Apply(ConditionSignal.Inactive, t0.AddSeconds(2)).Transition);

            var unknown = machine.Apply(ConditionSignal.Unknown, t0.AddSeconds(2) + resolve);
            Assert.Equal(ObservationTransition.None, unknown.Transition);
            Assert.Equal(ObservationPhase.Open, unknown.Phase);
            Assert.True(unknown.Persistence < resolve);

            // Same wall gap after first Inactive would resolve without freeze; with freeze still Open.
            var stillOpen = machine.Apply(ConditionSignal.Inactive, t0.AddSeconds(2) + resolve);
            Assert.Equal(ObservationTransition.Continued, stillOpen.Transition);
            Assert.Equal(ObservationPhase.Open, stillOpen.Phase);

            Assert.Equal(
                ObservationTransition.Resolved,
                machine.Apply(ConditionSignal.Inactive, t0.AddSeconds(2) + resolve + resolve).Transition);
        }
    }
}
