using System;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using Flow.Api.Infrastructure;
using Flow.Application.Cases;
using Flow.Application.Mediator;
using Flow.Contracts.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;

namespace Flow.Api.Controllers
{
    // ADO #32/#33 - the case-operation surface, thin over ISender. A deliberate mix of plain and
    // OData-flavored actions on one controller (matching the resource, not the routing style):
    //
    //   - Create is plain attribute-routed MVC (no queryable result, nothing to $select/$expand).
    //   - Get is the OData entity read: [EnableQuery] applies $select/$expand from
    //     Infrastructure/CasesEdmModel.cs's EDM model to the returned CaseView.
    //   - Trigger is the OData "bound action" (POST .../trigger) in URL shape and EDM
    //     self-description (see CasesEdmModel), but dispatches via a normal attribute route rather
    //     than OData's own action-invoker - explicit attribute routing does not depend on
    //     convention-based controller-name-to-entity-set matching, so Create/Get/Trigger can share
    //     this one controller unambiguously.
    //
    // All three route templates are hand-written against the "cases" entity-set name
    // (lower-case, matching the plain POST) rather than relying on OData's default PascalCase
    // convention.
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/cases")]
    public sealed class CasesController : ControllerBase
    {
        private readonly ISender _sender;

        public CasesController(ISender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        // TODO #32 read-model: the $filter-able GET /api/v1/cases collection (EntitySet-level
        // query, e.g. "?$filter=state eq 'Active'") needs a queryable read model this unit does not
        // build - GetCaseQueryHandler only knows how to look up one case by id via its live grain.
        // This is the slot a future EntitySet action ([EnableQuery] public Task<IActionResult>
        // Get(CancellationToken)) fills once that read model exists.

        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] CreateCaseRequest request, CancellationToken cancellationToken)
        {
            var command = new CreateCaseCommand(request?.DefinitionId);

            return await _sender.Send(command, cancellationToken).ToActionResultAsync(view =>
                new ObjectResult(view) { StatusCode = StatusCodes.Status201Created });
        }

        // ADO #32/#33 (sub-unit 4) - "~/" makes this an ABSOLUTE route template rather than one
        // relative to the controller-level [Route]: ASP.NET Core's attribute-route combinator
        // always joins a relative child template onto its parent with an inserted "/"
        // (AttributeRouteModel.CombineTemplates), which would otherwise produce
        // "api/v{version}/cases/({key})" - a stray slash before the parenthesis that breaks the
        // documented OData-style "cases({key})" URL shape this controller's own remarks describe
        // (caught by MultiTenantIsolationApiTests, the first suite to exercise this route over
        // real HTTP rather than by calling the controller method directly - see
        // Flow.Api.Tests/Controllers/CasesControllerTests.cs, which never routes a real request).
        [HttpGet("~/api/v{version:apiVersion}/cases({key})")]
        [EnableQuery]
        public async Task<IActionResult> Get(Guid key, CancellationToken cancellationToken)
        {
            var query = new GetCaseQuery(key);

            return await _sender.Send(query, cancellationToken).ToActionResultAsync(view => Ok(view));
        }

        // Same "~/" absolute-template fix as Get above, and for the same reason.
        [HttpPost("~/api/v{version:apiVersion}/cases({key})/trigger")]
        public async Task<IActionResult> Trigger(
            Guid key, [FromBody] TriggerCaseRequest request, CancellationToken cancellationToken)
        {
            var transition = PlanItemTransitionMapper.ToDomain(request.Transition);
            var command = new TriggerCaseCommand(key, transition);

            return await _sender.Send(command, cancellationToken).ToActionResultAsync(view => Ok(view));
        }
    }
}
