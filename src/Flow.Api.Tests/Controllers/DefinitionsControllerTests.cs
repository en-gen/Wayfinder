using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flow.Api.Controllers;
using Flow.Application.Cases;
using Flow.Application.Mediator;
using Flow.Application.Results;
using Flow.Contracts.V1;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Flow.Api.Tests.Controllers
{
    // ADO #32 - unit-level: a faked ISender, a bare DefaultHttpContext for Request.Body (no host/
    // TestServer). Proves the raw request body becomes DeployDefinitionCommand.CmmnXml verbatim and
    // that the Result -> IActionResult mapping applies here exactly like every other controller.
    public class DefinitionsControllerTests
    {
        [Fact]
        public async Task Deploy_Given_XmlBody_Then_BuildsCommandFromRawBodyAndReturns201()
        {
            const string xml = "<case id=\"case-1\"/>";

            DeployDefinitionCommand captured = null;
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<DeployDefinitionCommand>(), It.IsAny<CancellationToken>()))
                .Returns<ICommand<CommandResult<DeployResult>>, CancellationToken>((cmd, _) =>
                {
                    captured = (DeployDefinitionCommand)cmd;
                    return Task.FromResult(
                        CommandResult<DeployResult>.Success(new DeployResult("case-1", new[] { "warning" })));
                });

            var controller = WithRequestBody(sender.Object, xml);

            var result = await controller.Deploy(CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.CmmnXml.Should().Be(xml);

            var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
            var response = objectResult.Value.Should().BeOfType<DeployDefinitionResponse>().Subject;
            response.DefinitionId.Should().Be("case-1");
            response.Warnings.Should().ContainSingle().Which.Should().Be("warning");
        }

        [Fact]
        public async Task Deploy_Given_MalformedXml_Then_Maps400()
        {
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<DeployDefinitionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CommandResult<DeployResult>.BadRequest("malformed XML"));

            var controller = WithRequestBody(sender.Object, "<not-cmmn/>");

            var result = await controller.Deploy(CancellationToken.None);

            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        }

        private static DefinitionsController WithRequestBody(ISender sender, string body) =>
            new(sender)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) },
                    },
                },
            };
    }
}
