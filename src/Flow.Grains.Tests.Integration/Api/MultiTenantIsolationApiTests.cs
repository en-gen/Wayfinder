using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Flow.Application.Identity;
using Flow.Contracts.V1;
using Flow.Grains.Interfaces.Identity;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

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
