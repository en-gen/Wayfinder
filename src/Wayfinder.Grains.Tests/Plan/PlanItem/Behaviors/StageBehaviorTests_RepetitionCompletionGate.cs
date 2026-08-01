using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // Issue #198 - Table 8.12 completion racing a determined-but-unspawned repetition.
    // ~~~~~
    // 8.6.4: a Stage or Task instance whose RepetitionRule re-evaluates TRUE determines a
    // repetition - a successor instance that MUST come into existence. The child records that
    // determination durably on its own journal (PlanItemStore.Apply(Repeated), raised by
    // BaseBehavior.TryRepeatOnCompleteOrTerminate and the entry-criterion path in
    // StageBehavior/TaskBehavior/MilestoneBehavior.HandleSentrySatisfied) and separately publishes
    // PlanItemRepetitionCriteriaMetEvent to its containing Stage. Those two facts travel by
    // different routes: the first is live state, the second is a stream message, and nothing
    // orders the stream message against the containing Stage's OWN Table 8.12 completion check.
    //
    // Before this fix the Stage could therefore complete in the gap, dropping the successor
    // outright and (before #178 refused the late spawn) producing Table 8.9's <impossible>
    // Completed-parent/live-child cell. The fix judges from the child's LIVE state, which cannot be
    // raced: a terminal child whose own snapshot says Repeated blocks its container's completion
    // until the container has durably recorded that it spawned the successor (#161's ChildRepeated)
    // or definitively refused it (RepetitionRequestSettled).
    //
    // These scenarios pin the gate deterministically at the unit layer - the end-to-end race is
    // inherently probabilistic (see Plan/CasePlanModel/RepetitionCompletionRaceIntegrationTests.cs
    // and the three formerly-quarantined #198 integration scenarios), so the guard's strength is
    // established here, where the interleaving is a fixture rather than a coin flip.
    public partial class StageBehaviorTests
    {
        private const string BlockedByRepetitionMessageFragment = "declared a repetition";

        // The repeating child: terminal AND Repeated, i.e. exactly the shape Table 8.12 would
        // otherwise treat as "done" while a successor is still owed.
        [Fact]
        public async Task Trigger__Given_TerminalChildDeclaredUnspawnedRepetition__Then_ManualCompleteThrowsNamingTheChild()
        {
            var (subject, mockMachine, _, children, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null)
                });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => subject.Trigger(PlanItemTransition.Complete));

            // The escape hatch is only usable if the refusal says WHICH child and WHY - a generic
            // "Table 8.12 not satisfied" would leave an operator with nothing to act on.
            ex.Message.Should().Contain(children.Single(),
                "the refusal must name the specific blocking child instance id");
            ex.Message.Should().Contain(BlockedByRepetitionMessageFragment);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);
            mockMachine.Object.State.Should().Be(PlanItemState.Active);
        }

        // Issue #194 / #198 - the design's stated escape hatch is that "a Stage that stops
        // completing is explainable from logs alone", and the refusal message alone does not
        // deliver that: it only reaches whoever happened to call Trigger(Complete), never the
        // operator investigating a case that quietly stopped moving on the AUTOMATIC path - which
        // is the path pinned here. (The MANUAL path's Warning is asserted end-to-end through the
        // real DI-resolved logger in RepetitionCompletionRaceIntegrationTests.ManualComplete__…;
        // the automatic one cannot be, because whether the gate is what held a given automatic
        // completion is a coin flip between two stream deliveries in a live cluster.)
        [Fact]
        public async Task HandleChildTransitioned__Given_CompletionHeldForARepetition__Then_LogsAWarningNamingTheChild()
        {
            var (subject, mockMachine, mockHost, children, logs) = CreateStageSubject(
                autoComplete: true,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null),
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await InvokeHandleChildTransitioned(subject, mockHost);

            mockMachine.Object.State.Should().Be(PlanItemState.Active, "the gate must actually have held this completion");

            logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning,
                    "a held completion must be diagnosable from logs alone (#194/#198)")
                .Which.Message.Should().Contain(children.First(),
                    "the Warning must name the blocking child instance id, not just say a completion was held");
        }

        // WEDGE CLASS 1 - the Suspended buffer. #178 buffers a repetition request that arrives
        // while the container is Suspended and deliberately does NOT settle it, so the gate keeps
        // holding completion for the whole suspension: correct, because the request really is still
        // owed. What must equally be true is that resuming RELEASES it - the drain spawns the
        // successor, settles the request, and (#198 F1) re-runs Table 8.12, which nothing else on
        // this path would do: the successor lands Available, which is not a transition Table 8.12
        // reacts to.
        [Fact]
        public async Task Resume__Given_CompletionHeldForABufferedRepetition__Then_DrainReleasesItAndTheStageCompletes()
        {
            var (subject, mockMachine, mockHost, children, _) = CreateStageSubject(
                autoComplete: true,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null)
                });

            var repeatingChildInstanceId = children.Single();
            var repeatingChildDefinitionId = DefinitionIdOf(mockHost, repeatingChildInstanceId);

            // Table 8.12's autoComplete=TRUE column is already satisfied (the one child is
            // Completed), so a manual Complete can only be refused by the gate.
            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Trigger(PlanItemTransition.Complete));

            await mockMachine.Object.FireAsync(PlanItemTransition.Suspend);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                mockHost.Object.Address, repeatingChildInstanceId, repeatingChildDefinitionId, 0));

            var suspendedStore = (StageBehaviorStore)mockHost.Object.State.BehaviorExtension;
            suspendedStore.PendingRepetitions.Should().HaveCount(1, "#178 buffers rather than spawning while Suspended");
            suspendedStore.IsRepetitionSettled(repeatingChildInstanceId).Should().BeFalse(
                "a buffered request is genuinely still owed, so it must keep holding completion");

            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            suspendedStore.IsRepetitionSettled(repeatingChildInstanceId).Should().BeTrue(
                "draining the buffer spawned the successor, which settles the request");
            mockMachine.Object.State.Should().Be(PlanItemState.Completed,
                "with the request resolved and the successor merely Available, Table 8.12's autoComplete=TRUE column is satisfied - and nothing but the drain's own re-evaluation will ever notice (#198 F1)");
        }

        // WEDGE CLASS 2 - a ceiling-refused container must still be completable after recovery.
        // The #67 ceiling refuses the repetition and Faults the container; the requesting child is
        // left terminal and Repeated forever, so without RepetitionRequestSettled the reactivated
        // container could never complete again and Terminate would be the only way out.
        [Fact]
        public async Task Reactivate__Given_ARepetitionWasCeilingRefused__Then_TheStageCanStillComplete()
        {
            const int ceiling = 3;

            var (subject, mockMachine, mockHost, children, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null)
                },
                repetitionCeiling: ceiling);

            var repeatingChildInstanceId = children.Single();
            var repeatingChildDefinitionId = DefinitionIdOf(mockHost, repeatingChildInstanceId);

            // currentRepetition + 1 == ceiling: refused, and the container Faults.
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                mockHost.Object.Address, repeatingChildInstanceId, repeatingChildDefinitionId, ceiling - 1));

            mockMachine.Object.State.Should().Be(PlanItemState.Failed);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never,
                "the ceiling refused the spawn - no successor exists, and none ever will");

            // A human recovers the container.
            await mockMachine.Object.FireAsync(PlanItemTransition.Reactivate);
            mockMachine.Object.State.Should().Be(PlanItemState.Active);

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed,
                "a refusal this container will never revisit must not hold Table 8.12 completion forever - that is a wedge with no recourse but Terminate");
        }

        // Non-Complete transitions are deliberately NOT gated: an operator faced with the refusal
        // above must always be able to terminate the stage instead. This is the "no permanent
        // wedge" requirement, pinned.
        [Fact]
        public async Task Trigger__Given_TerminalChildDeclaredUnspawnedRepetition__Then_TerminateIsNotGated()
        {
            var (subject, mockMachine, _, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null)
                });

            await subject.Trigger(PlanItemTransition.Terminate);

            mockMachine.Object.State.Should().Be(PlanItemState.Terminated,
                "only Complete is held by the #198 gate - Terminate/Exit remain available as operator recourse");
        }

        // ChildRepeated (#161) - the container spawned the successor. Request resolved; the gate
        // must release.
        [Fact]
        public async Task Trigger__Given_RepetitionAlreadySpawned__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)SettlementKind.Spawned)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed);
        }

        // RepetitionRequestSettled - the container definitively refused (ceiling, terminal/Failed
        // container, unknown child). No successor is ever coming, so the gate must release rather
        // than wedge the stage forever.
        [Fact]
        public async Task Trigger__Given_RepetitionDefinitivelyRefused__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)SettlementKind.Refused)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed);
        }

        // The gate must not fire on a child that merely CAN repeat: Repeated is raised only once
        // the rule has actually re-evaluated TRUE (8.6.4), and only that determination owes a
        // successor.
        [Fact]
        public async Task Trigger__Given_TerminalChildThatNeverRepeated__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed);
        }

        // The entry-criterion repetition path (HandleSentrySatisfied) raises Repeated on a child
        // that is still live, not terminal - and a live child is already handled by Table 8.12's
        // own terms. The gate's IsTerminal() conjunct keeps it from double-counting that case, and
        // from blocking on a child that has not finished owing anything yet.
        [Fact]
        public async Task Trigger__Given_NonTerminalChildDeclaredRepetition__Then_TheGateDoesNotApply()
        {
            var (subject, mockMachine, _, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    // Non-required + Active: Table 8.12's manual branch explicitly permits this
                    // (D4), so anything that refuses here can only be the #198 gate.
                    (required: false, state: PlanItemState.Active, repeated: true, settled: (SettlementKind?)null)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed);
        }

        // THE SIBLING PATH - the case a whole-stream-synchronisation approach misses. The
        // completion check is driven by a NON-repeating sibling's terminal transition, so nothing
        // about the triggering event mentions the repetition at all; the gate has to be evaluated
        // over every child on every completion path, which is exactly what reading live state
        // gives.
        [Fact]
        public async Task HandleChildTransitioned__Given_AutoComplete__When_SiblingCompletesWhileRepetitionUnspawned__Then_DoesNotComplete()
        {
            var (subject, mockMachine, mockHost, _, _) = CreateStageSubject(
                autoComplete: true,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null),
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await InvokeHandleChildTransitioned(subject, mockHost);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<AutoCompleteCriteriaMet>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);
            mockMachine.Object.State.Should().Be(PlanItemState.Active,
                "Table 8.12's autoComplete=TRUE column is satisfied, but completing now would drop the repetition the first child determined (#198)");
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_AutoComplete__When_RepetitionSpawned__Then_Completes()
        {
            var (subject, mockMachine, mockHost, _, _) = CreateStageSubject(
                autoComplete: true,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)SettlementKind.Spawned),
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await InvokeHandleChildTransitioned(subject, mockHost);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<AutoCompleteCriteriaMet>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        // Table 8.12's autoComplete=FALSE Branch 1 (the automatic OR-branch), same gate.
        [Fact]
        public async Task HandleChildTransitioned__Given_NotAutoComplete__When_SiblingCompletesWhileRepetitionUnspawned__Then_DoesNotComplete()
        {
            var (subject, mockMachine, mockHost, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null),
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await InvokeHandleChildTransitioned(subject, mockHost);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);
            mockMachine.Object.State.Should().Be(PlanItemState.Active);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_NotAutoComplete__When_RepetitionRefused__Then_Completes()
        {
            var (subject, mockMachine, mockHost, _, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)SettlementKind.Refused),
                    (required: true, state: PlanItemState.Completed, repeated: false, settled: (SettlementKind?)null)
                });

            await InvokeHandleChildTransitioned(subject, mockHost);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        // The Case root gets the gate too, and by inheritance rather than by restatement:
        // CasePlanModelBehavior overrides only ManualCompletionCriteriaSatisfied (Table 8.5/8.6's
        // different completion rule for the Case lifecycle) and inherits StageBehavior.Trigger,
        // where the repetition gate lives. This is the shape that actually matters in practice -
        // #198's three reproducing scenarios all put the repeating child directly under the
        // CasePlanModel.
        [Fact]
        public async Task Trigger__Given_CasePlanModelWithTerminalChildDeclaredUnspawnedRepetition__Then_ManualCompleteThrows()
        {
            var (subject, mockMachine, _, children, _) = CreateStageSubject(
                autoComplete: false,
                children: new[]
                {
                    // Table 8.5/8.6's Case-completion rule (no Active children, required children
                    // terminal) is fully satisfied by this child - so anything that refuses below
                    // can only be the #198 gate.
                    (required: true, state: PlanItemState.Completed, repeated: true, settled: (SettlementKind?)null)
                },
                isCasePlanModel: true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => subject.Trigger(PlanItemTransition.Complete));

            ex.Message.Should().Contain(children.Single());
            ex.Message.Should().Contain(BlockedByRepetitionMessageFragment);

            mockMachine.Object.State.Should().Be(PlanItemState.Active);
        }

        // How a child's outstanding repetition request came to be resolved on this container.
        private enum SettlementKind
        {
            // #161 ChildRepeated - the successor was actually created.
            Spawned,
            // #198 RepetitionRequestSettled - definitively refused (ceiling / terminal or Failed
            // container / unknown child), so no successor is ever coming.
            Refused
        }

        // The container's own journal already indexes children by definition id, so a scenario that
        // needs to hand HandleChildRepeated a well-formed request can read the definition id back
        // from there rather than having the builder hand out a second parallel array.
        private static string DefinitionIdOf(Mock<IBehaviorHost> mockHost, string childInstanceId) =>
            ((StageBehaviorStore)mockHost.Object.State.BehaviorExtension).Children
                .Single(kvp => kvp.Value.ContainsKey(childInstanceId)).Key;

        private static Task InvokeHandleChildTransitioned(StageBehavior subject, Mock<IBehaviorHost> mockHost) =>
            (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        mockHost.Object.Address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

        // THE canonical StageBehavior unit-test subject builder (previously three near-verbatim
        // copies: this one, StageBehaviorTests_Trigger.CreateTriggerSubject - now a thin wrapper
        // over this - and a hand-rolled CasePlanModel variant inlined into the scenario above).
        //
        // Each entry in `children` becomes a real child instance of this container: journaled here
        // through the same ChildCreated/ChildRepeated/RepetitionRequestSettled events production
        // raises (never by poking the store's internals), with a mocked grain returning the
        // snapshot the container will read back. That covers everything the Table 8.12 gates
        // consult - Required/PlanItemState for the criteria themselves, plus the two facts the #198
        // gate adds: the child's own Repeated flag (live state, on its snapshot) and this
        // container's record of having resolved that child's request (parent-local journal).
        //
        // Child DEFINITIONS are added to the Stage's PlanItems too, so HandleChildRepeated can
        // resolve a repetition request back to a definition the way it does in production.
        //
        // Returns the created child INSTANCE ids in declaration order (a scenario asserting that a
        // refusal names the right child needs them), and a FakeLogger wired into
        // IBehaviorHost.LogWithContext - the only seam through which this class logs, and the one
        // the #198 design leans on for "a held Stage is diagnosable from logs alone".
        private (StageBehavior subject, MockPlanItemStateMachine machine, Mock<IBehaviorHost> host, string[] childInstanceIds, FakeLogger logger)
            CreateStageSubject(
                bool autoComplete,
                (bool required, PlanItemState state, bool repeated, SettlementKind? settled)[] children,
                PlanItemState initialState = PlanItemState.Active,
                bool isCasePlanModel = false,
                int repetitionCeiling = RepetitionGuardOptions.DefaultMaxRepetitionsPerPlanItem)
        {
            var caseInstanceId = Guid.NewGuid();
            string address = isCasePlanModel ? "CPM" : ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();
            var stage = new Stage
            {
                Id = address,
                IsCasePlanModel = isCasePlanModel,
                AutoComplete = autoComplete
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: initialState);

            var mockGrainFactory = new Mock<IGrainFactory>();

            // Registered FIRST so the per-child setups below (a specific grain key) take precedence
            // over it - Moq resolves to the LAST matching setup. This catch-all is what answers for
            // a child the SUBJECT creates during the scenario (a spawned repetition), whose
            // instance id cannot be known here.
            var mockSpawnedChildGrain = new Mock<IPlanItemInternalGrain>();
            StubFreshlySpawnedChildSnapshot(mockSpawnedChildGrain);
            mockGrainFactory
                .Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockSpawnedChildGrain.Object);

            var childInstanceIds = new List<string>();

            foreach (var (required, state, repeated, settled) in children)
            {
                var childDefinitionId = ShortGuid.NewGuid();
                var childInstanceId = ShortGuid.NewGuid();
                childInstanceIds.Add(childInstanceId);

                stage.PlanItems.Add(new Interfaces.Model.PlanItem { Id = childDefinitionId });

                testStore.Apply(new ChildCreated
                {
                    PlanItemId = childDefinitionId,
                    PlanItemInstanceId = childInstanceId,
                    Repetition = 0
                });

                switch (settled)
                {
                    case SettlementKind.Spawned:
                        testStore.Apply(new ChildRepeated { SourceInstanceId = childInstanceId });
                        break;
                    case SettlementKind.Refused:
                        testStore.Apply(new RepetitionRequestSettled
                        {
                            SourceInstanceId = childInstanceId,
                            Reason = "test fixture: definitively refused"
                        });
                        break;
                }

                var snapshot = new PlanItemSnapshot
                {
                    Definition = new Interfaces.Model.PlanItem { Id = childDefinitionId },
                    Required = required,
                    PlanItemState = state,
                    Repeated = repeated
                };

                var mockChildGrain = new Mock<IPlanItemInternalGrain>();
                mockChildGrain.Setup(x => x.GetSnapshot())
                    .Returns(Task.FromResult(snapshot));

                mockGrainFactory
                    .Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{childInstanceId}", null))
                    .Returns(mockChildGrain.Object);
            }

            var fakeLogger = new FakeLogger();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.InstanceId).Returns(address);
            mockHost.Setup(x => x.Scope).Returns(string.Empty);
            mockHost.Setup(x => x.DefinitionId).Returns(stage.Id);
            mockHost.Setup(x => x.DefinitionScope).Returns(address);
            mockHost.Setup(x => x.Definition).Returns(pi);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            // The subject logs exclusively through this callback (see BaseBehaviorTests for the
            // same seam), so invoking it against a FakeLogger is how a unit scenario reads back
            // what production would have written.
            mockHost.Setup(x => x.LogWithContext(It.IsAny<Action<ILogger>>()))
                .Callback<Action<ILogger>>(logAction => logAction(fakeLogger));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = isCasePlanModel
                ? new CasePlanModelBehavior(mockHost.Object, stage, mockMachine.Object, repetitionCeiling)
                : new StageBehavior(mockHost.Object, stage, mockMachine.Object, repetitionCeiling);

            return (subject, mockMachine, mockHost, childInstanceIds.ToArray(), fakeLogger);
        }
    }
}
