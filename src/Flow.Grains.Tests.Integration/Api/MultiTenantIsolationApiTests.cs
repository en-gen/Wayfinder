using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Application.Identity;
using Flow.Contracts.V1;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Identity;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using CaseFileItemModel = Flow.Grains.Interfaces.Model.CaseFileItem;

namespace Flow.Grains.Tests.Integration.Api
{
    // ADO #32/#33 (sub-unit 4, P0) - proves tenant isolation through the REAL pipeline (auth ->
    // IdentityContextMiddleware -> ITenantResolver -> CaseRequestContext -> grain enforcement),
    // exercised over actual HTTP against Flow.Api's real controllers/OData/versioning, not just at
    // the grain layer (see CaseTenantIsolationIntegrationTests, which is the grain-level defense in
    // depth this suite proves is actually WIRED UP end to end) or the application layer
    // (CaseCqrsIntegrationTests). See ApiTestHostFactory for how the host is composed and
    // TestAuthenticationHandler for how a caller's tenant is selected per request without a real
    // Zitadel token.
    //
    // Each [Fact] gets a fresh TestAuthHostFactory-built host/HttpClient and two freshly-seeded
    // tenants (xUnit constructs a new test class instance - and therefore runs InitializeAsync -
    // per [Fact] by default), so no state or CaseRequestContext leakage is possible between tests;
    // ClusterFixture's underlying TestCluster is the only thing shared across the whole collection,
    // exactly like every other suite in this project.
    [Collection(ClusterCollection.Name)]
    public class MultiTenantIsolationApiTests : IAsyncLifetime
    {
        private readonly ClusterFixture _fixture;

        private IHost _host;
        private HttpClient _client;

        private Guid _tenantA;
        private Guid _tenantB;
        private string _subjectA;
        private string _subjectB;

        public MultiTenantIsolationApiTests(ClusterFixture fixture)
        {
            _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        }

        public async Task InitializeAsync()
        {
            _host = await ApiTestHostFactory.StartAsync(_fixture.ClusterClient);
            _client = _host.GetTestClient();

            _tenantA = Guid.NewGuid();
            _tenantB = Guid.NewGuid();
            _subjectA = $"sub-a-{Guid.NewGuid()}";
            _subjectB = $"sub-b-{Guid.NewGuid()}";

            var seeder = _host.Services.GetRequiredService<IIdentityRegistrySeeder>();

            await seeder.SeedAsync(new TenantSeed
            {
                TenantId = _tenantA,
                Name = "Tenant A",
                Oidc = new TenantOidcConfig { Issuer = "https://tenant-a.example/oidc", Audience = "case-flow" },
                Users = new[] { new UserSeed { Subject = _subjectA, UserId = Guid.NewGuid(), Roles = new[] { "Member" } } }
            });

            await seeder.SeedAsync(new TenantSeed
            {
                TenantId = _tenantB,
                Name = "Tenant B",
                Oidc = new TenantOidcConfig { Issuer = "https://tenant-b.example/oidc", Audience = "case-flow" },
                Users = new[] { new UserSeed { Subject = _subjectB, UserId = Guid.NewGuid(), Roles = new[] { "Member" } } }
            });
        }

        public async Task DisposeAsync()
        {
            _client?.Dispose();

            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }

        [Fact]
        public async Task TenantA__Given_OwnDefinitionAndCase__Then_DeployCreateAndReadAllSucceed()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (createResponse, view) = await CreateCaseAsync(_subjectA, definitionId);

            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            view.CaseId.Should().NotBe(Guid.Empty);
            view.DefinitionId.Should().Be(definitionId);

            var getOwn = await SendAsSubjectAsync(HttpMethod.Get, $"/api/v1/cases({view.CaseId})", _subjectA);
            getOwn.StatusCode.Should().Be(HttpStatusCode.OK,
                "the owning tenant must be able to read the case it just created");
        }

        [Fact]
        public async Task TenantB__Given_TenantAsCase__Then_GetReturns404()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (_, view) = await CreateCaseAsync(_subjectA, definitionId);

            var getForeign = await SendAsSubjectAsync(HttpMethod.Get, $"/api/v1/cases({view.CaseId})", _subjectB);

            getForeign.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a foreign tenant's read of another tenant's case must be indistinguishable from not-found - the isolation wall");

            // Belt and braces: a genuine application-level 404 (ResultExtensions'
            // CrossTenantAccessException -> ProblemDetails mapping) always carries a ProblemDetails
            // JSON body, unlike ASP.NET Core's own bare/empty 404 for a request that never matched
            // any route at all - asserting the body is non-empty pins this as the REAL isolation
            // path, not a routing miss that happens to also return 404.
            var foreignProblem = await getForeign.Content.ReadFromJsonAsync<ProblemDetails>();
            foreignProblem.Should().NotBeNull();
            foreignProblem!.Status.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task TenantB__Given_TenantAsCase__Then_TriggerReturns404()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (_, view) = await CreateCaseAsync(_subjectA, definitionId);

            var triggerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases({view.CaseId})/trigger")
            {
                Content = JsonContent.Create(new TriggerCaseRequest { Transition = PlanItemTransition.Complete })
            };
            triggerRequest.Headers.Add(TestAuthenticationHandler.SubjectHeaderName, _subjectB);

            var triggerForeign = await _client.SendAsync(triggerRequest);

            triggerForeign.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the isolation wall applies to the write side identically to the read side");

            // Same belt-and-braces check as the GET test above - pins this as the real
            // CrossTenantAccessException -> ProblemDetails path, not an incidental routing miss.
            var triggerProblem = await triggerForeign.Content.ReadFromJsonAsync<ProblemDetails>();
            triggerProblem.Should().NotBeNull();
            triggerProblem!.Status.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task Unauthenticated__Given_NoSubjectHeader__Then_401()
        {
            var response = await _client.GetAsync($"/api/v1/cases({Guid.NewGuid()})");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Authenticated__Given_UnprovisionedSubject__Then_403()
        {
            var response = await SendAsSubjectAsync(
                HttpMethod.Get, $"/api/v1/cases({Guid.NewGuid()})", $"never-provisioned-{Guid.NewGuid()}");

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a real, validly-authenticated caller who was never provisioned in the tenant registry is 403, not 401/404");
        }

        [Fact]
        public async Task TenantB__Given_TenantAsDefinitionId__Then_CreateCaseReturns400()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);

            var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
            {
                Content = JsonContent.Create(new CreateCaseRequest { DefinitionId = definitionId })
            };
            createRequest.Headers.Add(TestAuthenticationHandler.SubjectHeaderName, _subjectB);

            var response = await _client.SendAsync(createRequest);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "case definitions are tenant-scoped (ICaseDefinitionGrain is keyed (TenantId, defId)) - " +
                "tenant B's own (TenantB, definitionId) grain was never Define()'d, so this is caller error, not found");
        }

        // ADO #58 - case-file item version history must honor tenant isolation identically to a
        // current-value read (GetCaseQuery's own TenantB__Given_TenantAsCase__Then_GetReturns404
        // above). The case-file item itself has no TenantId of its own (see CaseFileItemAccess's
        // remarks) - it authorizes via its owning Case, so a foreign tenant's history read must be
        // indistinguishable from not-found, exactly like a foreign tenant's case read.
        [Fact]
        public async Task TenantB__Given_TenantAsCaseFileItemHistory__Then_GetHistoryReturns404()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (_, view) = await CreateCaseAsync(_subjectA, definitionId);
            var itemId = await CreateCaseFileItemAsync(view.CaseId);

            var getForeign = await SendAsSubjectAsync(
                HttpMethod.Get, $"/api/v1/cases({view.CaseId})/case-file-items({itemId})/history", _subjectB);

            getForeign.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "case-file item history must honor the same tenant isolation as a current-value read");

            var foreignProblem = await getForeign.Content.ReadFromJsonAsync<ProblemDetails>();
            foreignProblem.Should().NotBeNull();
            foreignProblem!.Status.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task TenantB__Given_TenantAsCaseFileItemVersion__Then_GetValueAtReturns404()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (_, view) = await CreateCaseAsync(_subjectA, definitionId);
            var itemId = await CreateCaseFileItemAsync(view.CaseId);

            var getForeign = await SendAsSubjectAsync(
                HttpMethod.Get, $"/api/v1/cases({view.CaseId})/case-file-items({itemId})/versions(2)", _subjectB);

            getForeign.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "case-file item as-of reads must honor the same tenant isolation as a current-value read");
        }

        [Fact]
        public async Task TenantA__Given_OwnCaseFileItem__Then_GetHistorySucceeds()
        {
            var definitionId = await DeployDefinitionAsync(_subjectA);
            var (_, view) = await CreateCaseAsync(_subjectA, definitionId);
            var itemId = await CreateCaseFileItemAsync(view.CaseId);

            var getOwn = await SendAsSubjectAsync(
                HttpMethod.Get, $"/api/v1/cases({view.CaseId})/case-file-items({itemId})/history", _subjectA);

            getOwn.StatusCode.Should().Be(HttpStatusCode.OK,
                "the owning tenant must be able to read its own case-file item's history");

            var history = await getOwn.Content.ReadFromJsonAsync<List<CaseFileItemVersionView>>();
            history.Should().ContainSingle();
            history![0].Transition.Should().Be(CaseFileItemTransition.Create);
        }

        // Creates a CaseFileItem directly against its own grain (CaseFileItem instances are not
        // yet instantiated from a caseFileModel definition graph - see CaseFileItemAddress's
        // remarks), exactly the way CaseFileItemGrainTests does. The item itself carries no
        // tenant of its own (only its owning Case does - see CaseFileItemAccess), but
        // CaseRequestContext.TenantId still needs SOME value here: CmmnElementGrain.
        // OnActivateAsync reads it unconditionally for its log-context scope (throws if unset),
        // regardless of whether the grain type itself enforces tenancy. This direct grain call
        // runs on the test method's own AsyncLocal flow, not through IdentityContextMiddleware
        // (which only primes CaseRequestContext for requests routed through the TestServer/
        // HttpClient below), so it has to be primed here explicitly - matching every other
        // grain-level integration test's constructor (e.g. CaseFileItemGrainTests).
        private async Task<string> CreateCaseFileItemAsync(Guid caseId)
        {
            CaseRequestContext.TenantId = _tenantA;
            CaseRequestContext.UserId = Guid.NewGuid();

            var itemId = $"item-{Guid.NewGuid():N}";
            var itemGrain = _fixture.ClusterClient.GetCaseFileItem(caseId, itemId);
            await itemGrain.Create($"def-{Guid.NewGuid():N}", new CaseFileItemModel { Id = itemId }, JsonValue.Create("v1"));
            return itemId;
        }

        private async Task<string> DeployDefinitionAsync(string subject)
        {
            var xml = ReadEmbeddedCmmn("MilestoneSentryCase.cmmn");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/definitions")
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
            request.Headers.Add(TestAuthenticationHandler.SubjectHeaderName, subject);

            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            var body = await response.Content.ReadFromJsonAsync<DeployDefinitionResponse>();
            body!.DefinitionId.Should().NotBeNullOrWhiteSpace();
            return body.DefinitionId;
        }

        private async Task<(HttpResponseMessage Response, CaseView View)> CreateCaseAsync(string subject, string definitionId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
            {
                Content = JsonContent.Create(new CreateCaseRequest { DefinitionId = definitionId })
            };
            request.Headers.Add(TestAuthenticationHandler.SubjectHeaderName, subject);

            var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var view = await response.Content.ReadFromJsonAsync<CaseView>();
            return (response, view);
        }

        private Task<HttpResponseMessage> SendAsSubjectAsync(HttpMethod method, string url, string subject)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(TestAuthenticationHandler.SubjectHeaderName, subject);
            return _client.SendAsync(request);
        }

        // Same embedded-resource pattern CaseCqrsIntegrationTests uses (the flagship .cmmn file,
        // embedded so the test never depends on the test assembly's output directory layout).
        private static string ReadEmbeddedCmmn(string suffix)
        {
            var assembly = typeof(MultiTenantIsolationApiTests).Assembly;
            var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
