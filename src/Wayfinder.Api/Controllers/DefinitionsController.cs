using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using Flow.Api.Infrastructure;
using Flow.Application.Cases;
using Flow.Application.Mediator;
using Flow.Contracts.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Flow.Api.Controllers
{
    // ADO #32 - a plain versioned controller (not OData-flavored - there is no queryable
    // "definitions" entity set, just an import operation), thin over ISender exactly like the other
    // Cases actions. The request body is raw CMMN 1.1 XML, not a JSON DTO, so it is read directly
    // off Request.Body rather than model-bound - [ApiController]'s automatic body-binding only
    // applies to parameters that opt in, and none do here.
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/definitions")]
    public sealed class DefinitionsController : ControllerBase
    {
        private readonly ISender _sender;

        public DefinitionsController(ISender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        [HttpPost]
        [Consumes("application/xml", "text/xml")]
        public async Task<IActionResult> Deploy(CancellationToken cancellationToken)
        {
            string cmmnXml;
            using (var reader = new StreamReader(Request.Body))
            {
                cmmnXml = await reader.ReadToEndAsync(cancellationToken);
            }

            var command = new DeployDefinitionCommand(cmmnXml);

            return await _sender.Send(command, cancellationToken).ToActionResultAsync(result =>
                new ObjectResult(new DeployDefinitionResponse
                {
                    DefinitionId = result.DefinitionId,
                    Warnings = result.Warnings,
                })
                {
                    StatusCode = StatusCodes.Status201Created,
                });
        }
    }
}
