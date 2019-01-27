using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Hosting;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Definitions
{
    [Collection(ClusterCollection.Name)]
    public class PlanItemDefinitionGraphGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public PlanItemDefinitionGraphGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Fact]
        public void Scope__When_ParentNull__Then_Empty() => new Node().Scope.Should().BeEmpty();

        [Fact]
        public void Address__When_ParentNull__Then_Empty() => new Node().Scope.Should().BeEmpty();

        [Theory, AutoData]
        public void Scope__When_Parent__Then_ParentScopeAndParentId(string parentId, string childId)
        {
            var parent = new Node {Id = parentId};
            var subject = parent.AddNode(childId);
            subject.Scope
                .Should()
                .BeEquivalentTo(parentId);
        }

        [Theory, AutoData]
        public void Address__When_Parent__Then_ScopeAndId(string parentId, string childId)
        {
            var parent = new Node { Id = parentId };
            var subject = parent.AddNode(childId);
            subject.Address
                .Should()
                .BeEquivalentTo($"{subject.Scope}.{subject.Id}");
        }

        [Fact]
        public async Task Construct__Given_Case__When_IdsDoNotMatch__Then_ThrowArgumentException()
        {
            var flowId = Guid.NewGuid();

            var subject = ClusterClient.GetGrain<IPlanItemDefinitionGraphGrain>(flowId);
            await subject
                .Awaiting(x => x.Construct(new Case {Id = "not_a_match"}))
                .Should()
                .ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task Find__When_NotConstructed__Then_ThrowInvalidOperationException()
        {
            var flowId = Guid.NewGuid();
            var subject = ClusterClient.GetGrain<IPlanItemDefinitionGraphGrain>(flowId);

            await subject
                .Awaiting(x => x.Find("any", "any"))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task Find__Given_ConstructedGraph__When_SeveralScenarios__Then_Return()
        {
            var flowId = Guid.NewGuid();

            var expected = new HumanTask {Id = "HumanTaskA"};

            var @case = new Case
            {
                Id = flowId.ToString(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    IsCasePlanModel = true,

                    PlanItemDefinitions =
                    {
                        expected,
                        new HumanTask {Id = "HumanTaskB"},
                        new Stage
                        {
                            Id="StageA",
                            PlanItemDefinitions =
                            {
                                new HumanTask{Id="HumanTaskC"},
                                new HumanTask{Id="HumanTaskD"}
                            }
                        },
                        new Stage
                        {
                            Id="StageB",
                            PlanItemDefinitions =
                            {
                                new HumanTask{Id = "HumanTaskE"},
                                new Stage
                                {
                                    Id = "StageC"
                                }
                            }
                        }
                    }
                }
            };

            var subject = ClusterClient.GetGrain<IPlanItemDefinitionGraphGrain>(flowId);

            await subject.Awaiting(x => x.Construct(@case))
                .Should()
                .NotThrowAsync();

            var resultA = await subject.Find("CPM.StageB.StageC", "HumanTaskA"); // def in hierarchy parent
            var resultB = await subject.Find("CPM", "HumanTaskA"); // def in same scope
            var resultC = await subject.Find("CPM.StageA", "HumanTaskE"); // def not in hierarchy
            var resultD = await subject.Find("CPM", "HumanTaskD"); // def lower in hierarchy

            resultA.Should().BeEquivalentTo(expected);
            resultB.Should().BeEquivalentTo(expected);
            resultC.Should().BeNull();
            resultD.Should().BeNull();
        }
    }
}
