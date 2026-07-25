using Wayfinder.Grains.Interfaces.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    // Pinning tests for the polymorphic CMMN model hierarchy through Orleans's actual registered
    // fallback serializer (Microsoft.Orleans.Serialization.SystemTextJson's AddJsonSerializer +
    // CmmnPolymorphicTypeResolver - see OrleansFallbackJsonSerializer, ClusterFixture, Program.cs).
    //
    // These exist because of a real historical failure: without embedded type identity, a
    // PlanItemDefinition-typed reference holding a concrete Stage/HumanTask/etc. silently
    // deserializes back as a plain PlanItemDefinition, losing the subtype that
    // PlanItemBehaviorConfiguratorService's type dispatch and Sentry OnPart matching depend on. This
    // happened once already during the Newtonsoft-era Orleans migration (hence that serializer's
    // TypeNameHandling.Auto) - these tests exist so the same regression can't reoccur silently for
    // the System.Text.Json fallback.
    [Collection(ClusterCollection.Name)]
    public class OrleansFallbackJsonSerializerTests
    {
        private readonly Serializer _serializer;

        public OrleansFallbackJsonSerializerTests(ClusterFixture fixture)
        {
            _serializer = fixture.Cluster.ServiceProvider.GetRequiredService<Serializer>();
        }

        [Fact]
        public void RoundTrip__Given_PlanItemDefinitionVariable_HoldingStage__Then_DeserializesAsConcreteStage()
        {
            PlanItemDefinition original = new Stage
            {
                Id = "stage-1",
                Name = "Approval Stage",
                IsCasePlanModel = true
            };

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<PlanItemDefinition>(bytes);

            roundTripped.Should().BeOfType<Stage>();
            var stage = Assert.IsType<Stage>(roundTripped);
            stage.Id.Should().Be("stage-1");
            stage.Name.Should().Be("Approval Stage");
            stage.IsCasePlanModel.Should().BeTrue();
        }

        [Fact]
        public void RoundTrip__Given_PlanItemDefinitionVariable_HoldingHumanTask__Then_DeserializesAsConcreteHumanTask()
        {
            PlanItemDefinition original = new HumanTask
            {
                Id = "task-1",
                Name = "Review Application",
                IsBlocking = true
            };

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<PlanItemDefinition>(bytes);

            roundTripped.Should().BeOfType<HumanTask>();
            var humanTask = Assert.IsType<HumanTask>(roundTripped);
            humanTask.Id.Should().Be("task-1");
            humanTask.Name.Should().Be("Review Application");
            humanTask.IsBlocking.Should().BeTrue();
        }
    }
}
