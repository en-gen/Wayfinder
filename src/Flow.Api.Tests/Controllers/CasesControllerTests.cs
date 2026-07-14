using System;
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
using DomainTransition = Flow.Grains.Interfaces.Model.PlanItemTransition;

namespace Flow.Api.Tests.Controllers
{
    // ADO #32/#33 - unit-level: a faked ISender in place of the real native mediator, no host/
    // TestServer. Each test proves the controller action builds the RIGHT command/query from its
    // HTTP inputs and maps the mediator's Result back to the right IActionResult/status.
    public class CasesControllerTests
    {
        [Fact]
        public async Task Create_Given_ValidRequest_Then_BuildsCreateCaseCommandAndReturns201WithView()
        {
            var view = new CaseView { CaseId = Guid.NewGuid(), DefinitionId = "def-1", State = PlanItemState.Active };

            CreateCaseCommand captured = null;
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<CreateCaseCommand>(), It.IsAny<CancellationToken>()))
                .Returns<ICommand<CommandResult<CaseView>>, CancellationToken>((cmd, _) =>
                {
                    captured = (CreateCaseCommand)cmd;
                    return Task.FromResult(CommandResult<CaseView>.Success(view));
                });

            var controller = new CasesController(sender.Object);

            var result = await controller.Create(new CreateCaseRequest { DefinitionId = "def-1" }, CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.DefinitionId.Should().Be("def-1");

            var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
            objectResult.Value.Should().BeSameAs(view);
        }

        [Fact]
        public async Task Create_Given_HandlerBadRequest_Then_Maps400()
        {
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<CreateCaseCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CommandResult<CaseView>.BadRequest("definitionId is required"));

            var controller = new CasesController(sender.Object);

            var result = await controller.Create(new CreateCaseRequest(), CancellationToken.None);

            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        }

        [Fact]
        public async Task Get_Given_RouteKey_Then_BuildsGetCaseQueryAndReturns200WithView()
        {
            var caseId = Guid.NewGuid();
            var view = new CaseView { CaseId = caseId, DefinitionId = "def-1", State = PlanItemState.Active };

            GetCaseQuery captured = null;
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<GetCaseQuery>(), It.IsAny<CancellationToken>()))
                .Returns<IQuery<QueryResult<CaseView>>, CancellationToken>((query, _) =>
                {
                    captured = (GetCaseQuery)query;
                    return Task.FromResult(QueryResult<CaseView>.Success(view));
                });

            var controller = new CasesController(sender.Object);

            var result = await controller.Get(caseId, CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.CaseId.Should().Be(caseId);

            var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(view);
        }

        [Fact]
        public async Task Get_Given_NeverCreatedCase_Then_Maps404()
        {
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<GetCaseQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(QueryResult<CaseView>.NotFound());

            var controller = new CasesController(sender.Object);

            var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task Trigger_Given_RouteKeyAndBody_Then_BuildsTriggerCommandWithMappedDomainTransition()
        {
            var caseId = Guid.NewGuid();
            var view = new CaseView { CaseId = caseId, DefinitionId = "def-1", State = PlanItemState.Completed };

            TriggerCaseCommand captured = null;
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<TriggerCaseCommand>(), It.IsAny<CancellationToken>()))
                .Returns<ICommand<CommandResult<CaseView>>, CancellationToken>((cmd, _) =>
                {
                    captured = (TriggerCaseCommand)cmd;
                    return Task.FromResult(CommandResult<CaseView>.Success(view));
                });

            var controller = new CasesController(sender.Object);

            var request = new TriggerCaseRequest { Transition = PlanItemTransition.Complete };
            var result = await controller.Trigger(caseId, request, CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.CaseId.Should().Be(caseId);
            captured.Transition.Should().Be(DomainTransition.Complete);

            var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(view);
        }

        [Fact]
        public async Task Trigger_Given_InvalidTransition_Then_Maps400()
        {
            var sender = new Mock<ISender>();
            sender
                .Setup(s => s.Send(It.IsAny<TriggerCaseCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CommandResult<CaseView>.BadRequest("attempted Trigger on Uninitialized Case"));

            var controller = new CasesController(sender.Object);

            var result = await controller.Trigger(
                Guid.NewGuid(), new TriggerCaseRequest { Transition = PlanItemTransition.Start }, CancellationToken.None);

            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        }
    }
}
