using System;
using System.Diagnostics.Tracing;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Tests.Helpers;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Hosting;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    [Collection(ClusterCollection.Name)]
    public class MilestoneBehaviorTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public MilestoneBehaviorTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task IsUserCompletable__Given_DefinedMilestone__Then_False
            (Guid caseInstanceId)
        {
            var piDef = new Milestone();

            var store = CreateStore(caseInstanceId, piDef);

            var sm = CreateMockStateMachine(store);

            var host = new Mock<IBehaviorHost>();
            host
                .Setup(x => x.StateMachine)
                .Returns(sm.Object);

            var subject = new MilestoneBehavior(host.Object, piDef);

            var result = await subject.IsUserCompletable();

            result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task HandleParentTransitioned__Given_Host__When_ParentSuspended__Then_Suspend
            (Guid caseInstanceId, string parentId)
        {
            var def = new Milestone {Id = "Milestone"};
            var planItem = new Interfaces.Model.PlanItem
            {
                DefinitionRef = def.Id
            };

            var store = CreateStore(caseInstanceId, def, planItem);

            var mockSM = CreateMockStateMachine(store);

            var host = new Mock<IBehaviorHost>();
            host
                .Setup(x => x.Definition)
                .Returns(planItem);
            host
                .Setup(x => x.StateMachine)
                .Returns(mockSM.Object);
            host
                .Setup(x => x.ParentId)
                .Returns(parentId);

            Func<PlanItemTransitionedEvent, StreamSequenceToken, Task> capturedHandler = null;
            host
                .Setup(x => x.SubscribeTo<PlanItemTransitionedEvent>(
                    parentId,
                    It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                    StreamFlags.Create | StreamFlags.Resume))
                .Returns(Task.CompletedTask)
                .Callback<string, Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>, StreamFlags>(
                    (id, handler, flags) => capturedHandler = handler);

            var subject = new MilestoneBehavior(host.Object, def);

            await subject.Activate();

            capturedHandler.Should().NotBeNull();

            await capturedHandler(
                new PlanItemTransitionedEvent(
                    string.Empty,
                    "CPM",
                    PlanItemTransition.ParentSuspend,
                    PlanItemState.Active,
                    PlanItemState.Suspended),
                null
            );

            mockSM.Verify(x => x.CanFire(PlanItemTransition.Suspend), Times.Once);
            mockSM.Verify(x => x.FireAsync(PlanItemTransition.Suspend), Times.Once);
        }

        [Theory, AutoData]
        public async Task HandleSentrySatisfied__Given_DefinedMilestone__When_OnPartOccurred__Then_TriggerOccur
            (Guid caseDefinitionId, Guid caseInstanceId, string sentryId)
        {
            var milestone = new Milestone {Id = "Milestone"};

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItem",
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        Id = "EntryCriterion",
                        SentryRef = sentryId
                    }
                }
            };

            var @case = new Case
            {
                Id = caseDefinitionId.ToString(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    PlanItemDefinitions =
                    {
                        milestone
                    },
                    PlanItems =
                    {
                        planItem,
                    }
                }
            };

            await ClusterClient
                .GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Construct(@case);

            var subject = ClusterClient.GetGrain<IPlanItemGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");
            await subject.Define(caseDefinitionId, planItem);
            await subject.Trigger(PlanItemTransition.Create);

            (await subject.GetState()).Should().Be(PlanItemState.Available);

            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentryId)
                .OnNextAsync(new SentrySatisfiedEvent(@case.CasePlanModel.Id, sentryId, true));

            (await subject.GetState()).Should().Be(PlanItemState.Completed);
        }

        [Theory, AutoData]
        public async Task MilestoneAOccur_SentrySatisfied_MilestoneBOccur(Guid caseDefinitionId, Guid caseInstanceId)
        {
            var milestone = new Milestone{Id = "Milestone"};

            var planItemA = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = milestone.Id,
            };

            var sentry = new Interfaces.Model.Sentry
            {
                Id = "Sentry",
                OnParts =
                {
                    new PlanItemOnPart
                    {
                        SourceRef = planItemA.Id,
                        StandardEvent = PlanItemTransition.Occur
                    }
                },
                IfPart = new IfPart
                {
                    Condition = Rules.TruthyExpression
                }
            };

            var planItemB = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemB",
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentry.Id
                    }
                }
            };

            var @case = new Case
            {
                Id = caseDefinitionId.ToString(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    PlanItemDefinitions =
                    {
                        milestone
                    },
                    PlanItems =
                    {
                        planItemA,
                        planItemB
                    },
                    Sentries =
                    {
                        sentry
                    }
                }
            };

            await ClusterClient
                .GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Construct(@case);
            
            var subjectA = ClusterClient
                .GetGrain<IPlanItemGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItemA.Id}");
            var subjectB = ClusterClient
                .GetGrain<IPlanItemGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItemB.Id}");

            await Task.WhenAll(
                subjectA.Define(caseDefinitionId, planItemA),
                subjectB.Define(caseDefinitionId, planItemB)
            );

            await Task.WhenAll(
                subjectA.Trigger(PlanItemTransition.Create),
                subjectB.Trigger(PlanItemTransition.Create)
            );

            await subjectA.Trigger(PlanItemTransition.Occur);
        }

        private PlanItemStore CreateStore(Guid? caseDefId = null, Milestone piDef = null, Interfaces.Model.PlanItem def = null, PlanItemState initialState = PlanItemState.Available)
        {
            caseDefId = caseDefId ?? Guid.NewGuid();
            piDef = piDef ?? new Milestone {Id = "Milestone"};
            def = def ?? new Interfaces.Model.PlanItem
            {
                Id = "PlanItem",
                DefinitionRef = piDef.Id
            };

            var store = new PlanItemStore();
            store.Apply(new Defined
            {
                CaseDefinitionId = caseDefId.Value,
                PlanItemDefinition = piDef,
                Definition = def
            });
            store.Apply(new Transitioned
            {
                Destination = initialState
            });
            return store;
        }

        private Mock<IPlanItemStateMachine> CreateMockStateMachine(PlanItemStore store)
        {
            if (store.PlanItemDefinition == null) throw new ArgumentNullException(nameof(store.PlanItemDefinition), "You forgot to set the store's PlanItemDefinition (again)");

            var machine = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            var mock = new Mock<IPlanItemStateMachine>();
            mock
                .Setup(x => x.Configure(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(x => machine.Configure(x));
            mock
                .Setup(x => x.CanFire(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => machine.CanFire(x));

            mock
                .Setup(x => x.FireAsync(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => machine.FireAsync(x));

            return mock;
        }
    }
}
