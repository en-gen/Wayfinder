using System;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // #198 (review round 2 - BLOCKER fix verification) - PlanItemTransitionedEvent{WillRepeat=true}
    // and the corresponding PlanItemRepetitionCriteriaMetEvent travel on separate, unordered
    // streams: on develop, memory streams hash-map the two namespaces onto different queues with
    // independent pulling agents, so the repetition-met event routinely arrives and resolves
    // FIRST (measured at a majority of runs, not an edge case). These tests drive
    // StageBehavior.HandleChildTransitioned/HandleChildRepeated through BOTH possible arrival
    // orders and assert the container is NEVER permanently blocked from Table 8.12 completion -
    // the exact distinction the original (single-set) design's own test suite could not make,
    // since a wedged container and a correctly-deferring one both look identical to an assertion
    // that only checks "not Completed yet".
    public partial class StageBehaviorTests
    {
        // Sanity check that the guard actually engages: without it, this exact child snapshot
        // shape (the only child, terminal, autoComplete=FALSE, no PlanningTable) satisfies Table
        // 8.12 Branch 1 outright - see the sibling
        // HandleChildTransitioned__Given_Active__When_NotAutocompleteChildrenTerminal__Then_Complete
        // test, which is IDENTICAL except for WillRepeat. WillRepeat=true here must defer it.
        [Fact]
        public async Task HandleChildTransitioned__Given_WillRepeatTrue__Then_DefersCompletionEvenThoughChildIsTerminal()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();
            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = sourceInstanceId,
                Repetition = 0
            });

            var snapshot = new PlanItemSnapshot { Required = true, PlanItemState = PlanItemState.Completed };

            var mockPlanItem = new Mock<IPlanItemInternalGrain>();
            mockPlanItem.Setup(x => x.GetSnapshot()).Returns(Task.FromResult(snapshot));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{sourceInstanceId}", null))
                .Returns(mockPlanItem.Object);

            var mockHost = BuildMockHost(caseInstanceId, address, pi, testStore, mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);
            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await InvokeHandleChildTransitioned(subject, address, sourceInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: true);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never,
                "#198: a terminal child that told us it will repeat must defer Table 8.12 completion");

            ((Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores.StageBehaviorStore)testStore.BehaviorExtension)
                .AnyOutstandingRepetitionVerdicts.Should().BeTrue();
        }

        // The BLOCKER regression itself, exercised through the real StageBehavior handlers (not
        // just the store in isolation - see StageBehaviorStoreTests for that): the repetition-met
        // event resolves BEFORE the terminal transition event's WillRepeat=true mark ever arrives.
        [Fact]
        public async Task HandleChildRepeated_ThenHandleChildTransitioned__RepetitionFirstOrdering__Then_DoesNotWedgeCompletion()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";
            var sourceInstanceId = ShortGuid.NewGuid(); // repetition 0, the source that will repeat
            var repeatedInstanceId = ShortGuid.NewGuid(); // repetition 1, spawned below

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = sourceInstanceId,
                Repetition = 0
            });

            // repetition 1's own state is mutable across the test's steps: Enabled (freshly
            // spawned, non-terminal) until the final step, where it also completes.
            var repeatedState = PlanItemState.Enabled;

            var sourceSnapshot = new PlanItemSnapshot { Required = false, PlanItemState = PlanItemState.Completed };

            var mockSourcePlanItem = new Mock<IPlanItemInternalGrain>();
            mockSourcePlanItem.Setup(x => x.GetSnapshot()).Returns(Task.FromResult(sourceSnapshot));

            var mockRepeatedPlanItem = new Mock<IPlanItemInternalGrain>();
            mockRepeatedPlanItem.Setup(x => x.GetSnapshot())
                .Returns(() => Task.FromResult(new PlanItemSnapshot { Required = false, PlanItemState = repeatedState }));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{sourceInstanceId}", null))
                .Returns(mockSourcePlanItem.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{repeatedInstanceId}", null))
                .Returns(mockRepeatedPlanItem.Object);
            // DefineRepetition/CreateChild mints its own instance id (ShortGuid.NewGuid()) rather
            // than one this test controls, so route ANY instance under this address to the
            // repeated-child mock too - the source instance lookup above still wins for its own
            // exact key.
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.Is<string>(k => k.StartsWith(address + ".") && k != $"{address}.{sourceInstanceId}"), null))
                .Returns(mockRepeatedPlanItem.Object);

            var mockHost = BuildMockHost(caseInstanceId, address, pi, testStore, mockGrainFactory.Object,
                parentInstanceId, instanceId, scope, definitionScope);

            var mockMachine = new MockPlanItemStateMachine(testStore);
            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            // Step 1 - the repetition-met event arrives and resolves FIRST: spawns repetition 1,
            // settles sourceInstanceId, even though nothing was ever marked pending for it yet.
            await InvokeHandleChildRepeated(subject, address, sourceInstanceId, planItemDefinitionId, currentRepetition: 0);

            var stageStore = (Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores.StageBehaviorStore)testStore.BehaviorExtension;
            stageStore.IsRepetitionVerdictSettled(sourceInstanceId).Should().BeTrue();
            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never,
                "repetition 1 (Enabled) is not terminal - completion correctly still cannot fire");

            // Step 2 - the terminal transition event's WillRepeat=true mark arrives SECOND, late:
            // must be a no-op (the tombstone from step 1 already settled it), not a strand.
            await InvokeHandleChildTransitioned(subject, address, sourceInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: true);

            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse(
                "#198 BLOCKER regression: a WillRepeat=true mark arriving AFTER its own resolution " +
                "must not strand Table 8.12 completion for this container forever");

            // Step 3 - proof the container is not wedged: repetition 1 also completes (does not
            // itself repeat), and with the outstanding set empty and every child now terminal,
            // Table 8.12 Branch 1 must be able to fire Complete.
            repeatedState = PlanItemState.Completed;
            await InvokeHandleChildTransitioned(subject, address, repeatedInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: false);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once,
                "the container must still be able to complete once every child (including the " +
                "repetition spawned mid-race) is genuinely terminal - not permanently blocked");
        }

        // The mirror case: the terminal transition event's WillRepeat=true mark arrives FIRST (the
        // ordering the original #198 fix assumed was the only one), then the repetition-met event
        // resolves it. Included alongside the reordered test above so both orderings are pinned
        // side by side, not just the one that used to be assumed.
        [Fact]
        public async Task HandleChildTransitioned_ThenHandleChildRepeated__TransitionFirstOrdering__Then_ResolvesAndAllowsEventualCompletion()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";
            var sourceInstanceId = ShortGuid.NewGuid();
            var repeatedInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = sourceInstanceId,
                Repetition = 0
            });

            var repeatedState = PlanItemState.Enabled;

            var sourceSnapshot = new PlanItemSnapshot { Required = false, PlanItemState = PlanItemState.Completed };

            var mockSourcePlanItem = new Mock<IPlanItemInternalGrain>();
            mockSourcePlanItem.Setup(x => x.GetSnapshot()).Returns(Task.FromResult(sourceSnapshot));

            var mockRepeatedPlanItem = new Mock<IPlanItemInternalGrain>();
            mockRepeatedPlanItem.Setup(x => x.GetSnapshot())
                .Returns(() => Task.FromResult(new PlanItemSnapshot { Required = false, PlanItemState = repeatedState }));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{sourceInstanceId}", null))
                .Returns(mockSourcePlanItem.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.Is<string>(k => k.StartsWith(address + ".") && k != $"{address}.{sourceInstanceId}"), null))
                .Returns(mockRepeatedPlanItem.Object);

            var mockHost = BuildMockHost(caseInstanceId, address, pi, testStore, mockGrainFactory.Object,
                parentInstanceId, instanceId, scope, definitionScope);

            var mockMachine = new MockPlanItemStateMachine(testStore);
            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            var stageStore = (Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores.StageBehaviorStore)testStore.BehaviorExtension;

            // Step 1 - the terminal transition event arrives FIRST: marks pending, defers.
            await InvokeHandleChildTransitioned(subject, address, sourceInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: true);

            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeTrue();
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);

            // Step 2 - the repetition-met event arrives SECOND and resolves it: spawns repetition
            // 1 (Enabled, non-terminal), clears the outstanding mark. Completion still correctly
            // does not fire - repetition 1 itself is not yet terminal.
            await InvokeHandleChildRepeated(subject, address, sourceInstanceId, planItemDefinitionId, currentRepetition: 0);

            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never,
                "resolving must not itself complete the container while the spawned repetition is still live");

            // Step 3 - repetition 1 also completes without repeating further: the container must
            // now be free to complete.
            repeatedState = PlanItemState.Completed;
            await InvokeHandleChildTransitioned(subject, address, repeatedInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: false);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        // #198 (review round 2) - the inverted MarkRepetitionPending idempotency guard: a
        // redelivery of the SAME terminal PlanItemTransitionedEvent arriving after its own
        // resolution must not re-strand the container either.
        [Fact]
        public async Task HandleChildRepeated_ThenHandleChildTransitionedTwice__RedeliveredTransitionAfterSettlement__Then_DoesNotReStrand()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = sourceInstanceId,
                Repetition = 0
            });

            var sourceSnapshot = new PlanItemSnapshot { Required = false, PlanItemState = PlanItemState.Completed };
            var mockSourcePlanItem = new Mock<IPlanItemInternalGrain>();
            mockSourcePlanItem.Setup(x => x.GetSnapshot()).Returns(Task.FromResult(sourceSnapshot));

            var repeatedSnapshot = new PlanItemSnapshot { Required = false, PlanItemState = PlanItemState.Enabled };
            var mockRepeatedPlanItem = new Mock<IPlanItemInternalGrain>();
            mockRepeatedPlanItem.Setup(x => x.GetSnapshot()).Returns(Task.FromResult(repeatedSnapshot));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{sourceInstanceId}", null))
                .Returns(mockSourcePlanItem.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.Is<string>(k => k.StartsWith(address + ".") && k != $"{address}.{sourceInstanceId}"), null))
                .Returns(mockRepeatedPlanItem.Object);

            var mockHost = BuildMockHost(caseInstanceId, address, pi, testStore, mockGrainFactory.Object,
                parentInstanceId, instanceId, scope, definitionScope);

            var mockMachine = new MockPlanItemStateMachine(testStore);
            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            var stageStore = (Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores.StageBehaviorStore)testStore.BehaviorExtension;

            // resolves first, settling sourceInstanceId with nothing ever marked pending
            await InvokeHandleChildRepeated(subject, address, sourceInstanceId, planItemDefinitionId, currentRepetition: 0);
            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse();

            // first (late) delivery of the terminal transition event
            await InvokeHandleChildTransitioned(subject, address, sourceInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: true);
            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse();

            // a stream REDELIVERY of that SAME transitioned event
            await InvokeHandleChildTransitioned(subject, address, sourceInstanceId,
                PlanItemState.Active, PlanItemState.Completed, willRepeat: true);

            stageStore.AnyOutstandingRepetitionVerdicts.Should().BeFalse(
                "a redelivered terminal transition event, arriving after its own resolution, must " +
                "not re-mark a strand either");
        }

        private static Mock<IBehaviorHost> BuildMockHost(
            Guid caseInstanceId,
            string address,
            Interfaces.Model.PlanItem definition,
            TestPlanItemStore testStore,
            IGrainFactory grainFactory,
            string parentInstanceId = null,
            string instanceId = null,
            string scope = null,
            string definitionScope = null)
        {
            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.Definition).Returns(definition);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory).Returns(grainFactory);

            if (parentInstanceId != null) mockHost.Setup(x => x.ParentInstanceId).Returns(parentInstanceId);
            if (instanceId != null) mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            if (scope != null) mockHost.Setup(x => x.Scope).Returns(scope);
            if (definitionScope != null) mockHost.Setup(x => x.DefinitionScope).Returns(definitionScope);
            mockHost.Setup(x => x.DefinitionId).Returns(definition?.Id);

            return mockHost;
        }

        private static Task InvokeHandleChildTransitioned(
            StageBehavior subject, string sourceScope, string sourceInstanceId,
            PlanItemState source, PlanItemState destination, bool willRepeat) =>
            (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        sourceScope,
                        sourceInstanceId,
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Complete,
                        source,
                        destination,
                        null,
                        willRepeat),
                    (StreamSequenceToken)null
                });

        private static Task InvokeHandleChildRepeated(
            StageBehavior subject, string sourceScope, string sourceInstanceId,
            string planItemDefinitionId, int currentRepetition) =>
            (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        sourceScope, sourceInstanceId, planItemDefinitionId, currentRepetition),
                    (StreamSequenceToken)null
                });
    }
}
