using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flow.Application.Cases;
using Flow.Application.DependencyInjection;
using Flow.Application.Mediator;
using Flow.Contracts.V1;
using Flow.Grains.Interfaces;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Integration.Application
{
    // ADO #32/#33 - the CQRS-layer equivalent of #39's CaseOperationsIntegrationTests: exercises the
    // exact commands/queries Flow.Api (and a future MCP server) dispatch, but drives them through
    // the REAL native mediator (ISender / AddFlowApplication) over the existing in-memory Orleans
    // ClusterFixture - no web host, no Azurite/Docker (case ops need no durable storage), and no
    // JwtBearer/tenant-registry resolution either (that is Flow.Api's IdentityContextMiddleware,
    // exercised in Flow.Api.Tests instead - see that project). Proves a timer-free flagship .cmmn
    // file (MilestoneSentryCase.cmmn) becomes a live case and a readable Flow.Contracts.V1.CaseView
    // entirely through the Application seam.
    [Collection(ClusterCollection.Name)]
    public class CaseCqrsIntegrationTests
    {
        private readonly IServiceProvider _rootProvider;

        public CaseCqrsIntegrationTests(ClusterFixture fixture)
        {
            // Compose the Application layer exactly as a host would: register the fixture's co-hosted
            // Orleans client (the only dependency the handlers inject) and let AddFlowApplication
            // wire ISender + every ICommandHandler<,>/IQueryHandler<,> by its own reflection scan.
            //
            // ADO #33 - the handlers no longer default CaseRequestContext themselves (that seam,
            // CaseRequestContextDefaults, was deleted: Flow.Api's IdentityContextMiddleware is now
            // the only place that happens, from an authenticated caller's resolved identity). This
            // fixture has no HTTP pipeline, so it primes CaseRequestContext directly instead - the
            // same values CaseRequestContextDefaults used, and the same pattern every grain-level
            // integration fixture in this solution already uses (see e.g.
            // CaseLifecycleIntegrationTests's constructor).
            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

            _rootProvider = new ServiceCollection()
                .AddSingleton<IClusterClient>(fixture.ClusterClient)
                .AddFlowApplication()
                .BuildServiceProvider();
        }

        [Fact]
        public async Task DeployCreateGet__Given_MilestoneSentryCmmnFile__Then_CaseViewReflectsInitialState()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var xml = ReadEmbeddedResource("MilestoneSentryCase.cmmn");

            // Deploy: a spec-conformant .cmmn file becomes a deployed definition through the native
            // mediator, not a hand-built model. The flagship uses only supported constructs, so no
            // warnings.
            var deploy = await sender.Send(new DeployDefinitionCommand(xml));

            deploy.Succeeded.Should().BeTrue(deploy.Error);
            deploy.Value.DefinitionId.Should().NotBeNullOrWhiteSpace();
            deploy.Value.Warnings.Should().BeEmpty(
                "MilestoneSentryCase.cmmn only uses constructs the engine faithfully runs");

            // Create: a live case instance, driven through Create (Table 8.6 - the CasePlanModel
            // goes straight to Active).
            var created = await sender.Send(new CreateCaseCommand(deploy.Value.DefinitionId));

            created.Succeeded.Should().BeTrue(created.Error);
            var createdView = created.Value;
            createdView.CaseId.Should().NotBe(Guid.Empty);
            createdView.DefinitionId.Should().Be(deploy.Value.DefinitionId);
            createdView.State.Should().Be(PlanItemState.Active,
                "Table 8.6: the CasePlanModel's create transition goes straight to Active");

            createdView.PlanItems.Should().ContainSingle(
                "the CasePlanModel instantiates exactly one plan item (PlanItemA -> MilestoneA)");
            var milestone = createdView.PlanItems.Single();
            milestone.Id.Should().Be("PlanItemA");
            milestone.Name.Should().Be("PlanItemA");
            milestone.Type.Should().Be(nameof(Grains.Interfaces.Model.Milestone),
                "PlanItemA's definitionRef points at the <milestone> MilestoneA");
            milestone.State.Should().Be(PlanItemState.Available,
                "the Milestone was instantiated by Case creation and is waiting on its entry criterion");

            // Read-back: GetCaseQuery re-projects the SAME live case identically to what create
            // returned, through the query side of the mediator.
            var fetched = await sender.Send(new GetCaseQuery(createdView.CaseId));

            fetched.Succeeded.Should().BeTrue(fetched.Error);
            var fetchedView = fetched.Value;
            fetchedView.Should().NotBeNull();
            fetchedView.CaseId.Should().Be(createdView.CaseId);
            fetchedView.DefinitionId.Should().Be(deploy.Value.DefinitionId);
            fetchedView.State.Should().Be(PlanItemState.Active);
            fetchedView.PlanItems.Should().ContainSingle();
            var fetchedMilestone = fetchedView.PlanItems.Single();
            fetchedMilestone.Id.Should().Be("PlanItemA");
            fetchedMilestone.Type.Should().Be(nameof(Grains.Interfaces.Model.Milestone));
            fetchedMilestone.State.Should().Be(PlanItemState.Available);
        }

        [Fact]
        public async Task GetCase__Given_NeverCreatedCaseId__Then_NotFoundResult()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // A virtual Orleans grain always "exists", but a case that was never created has a null
            // Definition - the query reports that as a NotFound result (which a future HTTP layer
            // maps to 404), never a thrown exception.
            var result = await sender.Send(new GetCaseQuery(Guid.NewGuid()));

            result.Failed.Should().BeTrue();
            result.Status.Should().Be(Flow.Application.Results.ResultStatus.NotFound);
            result.Value.Should().BeNull();
        }

        [Fact]
        public async Task DeployDefinition__Given_MalformedXml__Then_BadRequestNotThrow()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // Bad .cmmn is expected bad input, surfaced as a BadRequest result (mapped to 400 by a
            // future HTTP layer), never a thrown exception.
            var deploy = await sender.Send(new DeployDefinitionCommand("<not-cmmn/>"));

            deploy.Failed.Should().BeTrue();
            deploy.Status.Should().Be(Flow.Application.Results.ResultStatus.BadRequest);
            deploy.Error.Should().NotBeNullOrWhiteSpace();
            deploy.Value.Should().BeNull();
        }

        [Fact]
        public async Task CreateCase__Given_UnknownDefinitionId__Then_BadRequestNotThrow()
        {
            using var scope = _rootProvider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // Creating from a definition that was never deployed is a caller error (CaseGrain.Create's
            // InvalidOperationException guard), mapped by the handler to a BadRequest result rather
            // than propagating as an exception.
            var created = await sender.Send(new CreateCaseCommand($"never-deployed-{Guid.NewGuid()}"));

            created.Failed.Should().BeTrue();
            created.Status.Should().Be(Flow.Application.Results.ResultStatus.BadRequest);
            created.Value.Should().BeNull();
        }

        private static string ReadEmbeddedResource(string suffix)
        {
            var assembly = typeof(CaseCqrsIntegrationTests).Assembly;
            var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
