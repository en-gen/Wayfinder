using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Application.CaseFileItems;
using Wayfinder.Application.Cases;
using Wayfinder.Application.DependencyInjection;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;
using CaseFileItemModel = Wayfinder.Grains.Interfaces.Model.CaseFileItem;

namespace Wayfinder.Grains.Tests.Integration.Application
{
    // ADO #58 - the CQRS-layer equivalent of CaseCqrsIntegrationTests, for case-file item version
    // history/as-of reads. CaseFileItem instances are not (yet) instantiated from a caseFileModel
    // definition graph (see CaseFileItemAddress's remarks) - a case-file item is created directly
    // against its own grain, exactly like CaseFileItemGrainTests does, while the owning CASE
    // (needed for tenant-isolation authorization - see CaseFileItemAccess) is driven through the
    // real native mediator, matching how a real caller would reach this surface.
    [Collection(ClusterCollection.Name)]
    public class CaseFileItemHistoryCqrsIntegrationTests
    {
        private readonly IServiceProvider _rootProvider;
        private readonly IClusterClient _clusterClient;

        public CaseFileItemHistoryCqrsIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

            _rootProvider = new ServiceCollection()
                .AddSingleton(_clusterClient)
                .AddFlowApplication()
                .BuildServiceProvider();
        }

        [Fact]
        public async Task GetHistoryAndGetValueAt__Given_CreateThenUpdate__Then_ReturnsOrderedVersionsAndAsOfValue()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var caseId = await CreateLiveCaseAsync(sender);

            var itemId = $"item-{Guid.NewGuid():N}";
            var itemGrain = _clusterClient.GetCaseFileItem(caseId, itemId);
            await itemGrain.Create($"def-{Guid.NewGuid():N}", new CaseFileItemModel { Id = itemId }, JsonValue.Create("v1"));
            await itemGrain.Update(JsonValue.Create("v2"));

            var history = await sender.Send(new GetCaseFileItemHistoryQuery(caseId, itemId));

            history.Succeeded.Should().BeTrue(history.Error);
            history.Value.Should().HaveCount(2);
            history.Value[0].Transition.Should().Be(CaseFileItemTransition.Create);
            history.Value[1].Transition.Should().Be(CaseFileItemTransition.Update);
            history.Value[0].ActorPrincipalId.Should().Be(CaseRequestContext.UserId);

            var firstVersion = history.Value[0].Version;
            var valueAtFirst = await sender.Send(new GetCaseFileItemValueAtQuery(caseId, itemId, firstVersion));

            valueAtFirst.Succeeded.Should().BeTrue(valueAtFirst.Error);
            valueAtFirst.Value.Version.Should().Be(firstVersion);
            valueAtFirst.Value.Value.ToJsonString().Should().Be(JsonValue.Create("v1").ToJsonString());
        }

        [Fact]
        public async Task GetHistory__Given_NeverCreatedCase__Then_NotFoundResult()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var result = await sender.Send(new GetCaseFileItemHistoryQuery(Guid.NewGuid(), "item-does-not-matter"));

            result.Failed.Should().BeTrue();
            result.Status.Should().Be(ResultStatus.NotFound);
        }

        [Fact]
        public async Task GetHistory__Given_ExistingCaseButNeverCreatedItem__Then_NotFoundResult()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var caseId = await CreateLiveCaseAsync(sender);

            var result = await sender.Send(new GetCaseFileItemHistoryQuery(caseId, "never-created-item"));

            result.Failed.Should().BeTrue();
            result.Status.Should().Be(ResultStatus.NotFound);
        }

        [Fact]
        public async Task GetValueAt__Given_VersionOutOfRange__Then_BadRequestResult()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var caseId = await CreateLiveCaseAsync(sender);

            var itemId = $"item-{Guid.NewGuid():N}";
            var itemGrain = _clusterClient.GetCaseFileItem(caseId, itemId);
            await itemGrain.Create($"def-{Guid.NewGuid():N}", new CaseFileItemModel { Id = itemId }, JsonValue.Create("v1"));

            var result = await sender.Send(new GetCaseFileItemValueAtQuery(caseId, itemId, 999));

            result.Failed.Should().BeTrue();
            result.Status.Should().Be(ResultStatus.BadRequest);
        }

        // Deploys the flagship .cmmn file and creates a live case through the real mediator -
        // mirrors CaseCqrsIntegrationTests' own DeployCreateGet flagship test setup. Only the
        // resulting caseId matters here; the case-file item under test is created independently.
        private static async Task<Guid> CreateLiveCaseAsync(ISender sender)
        {
            var xml = ReadEmbeddedResource("MilestoneSentryCase.cmmn");

            var deploy = await sender.Send(new DeployDefinitionCommand(xml));
            deploy.Succeeded.Should().BeTrue(deploy.Error);

            var created = await sender.Send(new CreateCaseCommand(deploy.Value.DefinitionId));
            created.Succeeded.Should().BeTrue(created.Error);

            return created.Value.CaseId;
        }

        private static string ReadEmbeddedResource(string suffix)
        {
            var assembly = typeof(CaseFileItemHistoryCqrsIntegrationTests).Assembly;
            var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
