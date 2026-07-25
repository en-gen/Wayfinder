using System;
using System.Threading.Tasks;
using Wayfinder.Api.Infrastructure;
using Wayfinder.Application.Results;
using Wayfinder.Grains.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Wayfinder.Api.Tests.Infrastructure
{
    // ADO #32/#33 - unit coverage for the single Result -> IActionResult seam every Wayfinder.Api
    // controller dispatches through (see ResultExtensions' remarks): every ResultStatus maps to its
    // documented HTTP status, and a thrown CrossTenantAccessException maps to 404 identically to a
    // genuine not-found - never a status that would let a caller distinguish the two.
    public class ResultExtensionsTests
    {
        [Fact]
        public async Task ToActionResultAsync_Given_CommandSuccess_Then_InvokesOnSuccessWithValue()
        {
            var resultTask = Task.FromResult(CommandResult<string>.Success("payload"));

            var actionResult = await resultTask.ToActionResultAsync(value => new OkObjectResult(value));

            actionResult.Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().Be("payload");
        }

        [Theory]
        [InlineData(ResultStatus.BadRequest, StatusCodes.Status400BadRequest)]
        [InlineData(ResultStatus.NotFound, StatusCodes.Status404NotFound)]
        [InlineData(ResultStatus.Conflict, StatusCodes.Status409Conflict)]
        [InlineData(ResultStatus.Unauthorized, StatusCodes.Status401Unauthorized)]
        public async Task ToActionResultAsync_Given_CommandFailureStatus_Then_MapsToDocumentedHttpStatus(
            ResultStatus status, int expectedStatusCode)
        {
            var failure = BuildCommandFailure(status, "something went wrong");

            var actionResult = await Task.FromResult(failure)
                .ToActionResultAsync(_ => throw new InvalidOperationException("onSuccess must not run"));

            var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(expectedStatusCode);
            objectResult.Value.Should().BeOfType<ProblemDetails>()
                .Which.Detail.Should().Be("something went wrong");
        }

        [Fact]
        public async Task ToActionResultAsync_Given_QuerySuccess_Then_InvokesOnSuccessWithValue()
        {
            var resultTask = Task.FromResult(QueryResult<int>.Success(42));

            var actionResult = await resultTask.ToActionResultAsync(value => new OkObjectResult(value));

            actionResult.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(42);
        }

        [Fact]
        public async Task ToActionResultAsync_Given_QueryNotFound_Then_Maps404()
        {
            var resultTask = Task.FromResult(QueryResult<int>.NotFound());

            var actionResult = await resultTask.ToActionResultAsync(value => new OkObjectResult(value));

            actionResult.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task ToActionResultAsync_Given_CommandThrowsCrossTenantAccessException_Then_Maps404()
        {
            var actionResult = await ThrowingCommandAsync()
                .ToActionResultAsync(value => new OkObjectResult(value));

            actionResult.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        }

        [Fact]
        public async Task ToActionResultAsync_Given_QueryThrowsCrossTenantAccessException_Then_Maps404()
        {
            var actionResult = await ThrowingQueryAsync()
                .ToActionResultAsync(value => new OkObjectResult(value));

            actionResult.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        }

        private static CommandResult<string> BuildCommandFailure(ResultStatus status, string error) => status switch
        {
            ResultStatus.BadRequest => CommandResult<string>.BadRequest(error),
            ResultStatus.NotFound => CommandResult<string>.NotFound(error),
            ResultStatus.Conflict => CommandResult<string>.Conflict(error),
            ResultStatus.Unauthorized => CommandResult<string>.Unauthorized(error),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        // CrossTenantAccessException deliberately never echoes case/tenant identity (see that
        // type's remarks) - the 404 this produces must be indistinguishable from a genuine
        // never-created case, so no Detail/Title assertion beyond the status code here.
        //
        // Task.FromException (not a synchronously-throwing method) - a real ISender.Send call
        // always returns a Task first and only faults it once awaited (the grain call is remote and
        // genuinely async), so the exception must be observed from inside ToActionResultAsync's own
        // try/await, exactly like production. A method body that threw directly would raise before
        // ToActionResultAsync's try block ever ran, proving nothing about its catch.
        private static Task<CommandResult<string>> ThrowingCommandAsync() =>
            Task.FromException<CommandResult<string>>(new CrossTenantAccessException());

        private static Task<QueryResult<string>> ThrowingQueryAsync() =>
            Task.FromException<QueryResult<string>>(new CrossTenantAccessException());
    }
}
