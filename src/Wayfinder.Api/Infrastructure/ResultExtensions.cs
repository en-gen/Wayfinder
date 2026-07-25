using System;
using System.Threading.Tasks;
using Wayfinder.Application.Results;
using Wayfinder.Grains.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wayfinder.Api.Infrastructure
{
    // ADO #32/#33 - the single Result -> IActionResult seam every controller action dispatches
    // through. Two concerns, both in one place so no controller re-implements either:
    //
    //   1. ResultStatus -> HTTP status (Success/BadRequest/NotFound/Conflict/Unauthorized), per
    //      ResultStatus's own remarks - the vocabulary was deliberately shaped 1:1 for this.
    //   2. CrossTenantAccessException -> 404. This is NOT a Result status - CaseGrain throws it as
    //      a real exception when CaseRequestContext.TenantId doesn't match the case's owning tenant
    //      (see that type's remarks), so the only place to catch it is around the ISender.Send call
    //      itself. Deliberately identical to a genuine not-found (never Conflict/403/anything that
    //      would let a caller distinguish "wrong tenant" from "never existed").
    //
    // 401 is handled entirely upstream by JwtBearer/the fallback authorization policy (an
    // unauthenticated caller never reaches a controller action at all); 403 is handled entirely by
    // the identity middleware (an authenticated-but-unprovisioned caller is short-circuited before
    // routing reaches MVC). Neither status is produced here.
    public static class ResultExtensions
    {
        public static async Task<IActionResult> ToActionResultAsync<T>(
            this Task<CommandResult<T>> resultTask, Func<T, IActionResult> onSuccess)
        {
            if (resultTask is null) throw new ArgumentNullException(nameof(resultTask));
            if (onSuccess is null) throw new ArgumentNullException(nameof(onSuccess));

            try
            {
                var result = await resultTask.ConfigureAwait(false);
                return result.Status == ResultStatus.Success
                    ? onSuccess(result.Value)
                    : MapFailure(result.Status, result.Error);
            }
            catch (CrossTenantAccessException)
            {
                return CrossTenantNotFound();
            }
        }

        public static async Task<IActionResult> ToActionResultAsync<T>(
            this Task<QueryResult<T>> resultTask, Func<T, IActionResult> onSuccess)
        {
            if (resultTask is null) throw new ArgumentNullException(nameof(resultTask));
            if (onSuccess is null) throw new ArgumentNullException(nameof(onSuccess));

            try
            {
                var result = await resultTask.ConfigureAwait(false);
                return result.Status == ResultStatus.Success
                    ? onSuccess(result.Value)
                    : MapFailure(result.Status, result.Error);
            }
            catch (CrossTenantAccessException)
            {
                return CrossTenantNotFound();
            }
        }

        // Exposed for unit tests / non-async callers that already hold a materialized Result -
        // both ToActionResultAsync overloads above funnel their non-Success path through this.
        public static IActionResult MapFailure(ResultStatus status, string error) => status switch
        {
            ResultStatus.BadRequest => Problem(StatusCodes.Status400BadRequest, error),
            ResultStatus.NotFound => Problem(StatusCodes.Status404NotFound, error),
            ResultStatus.Conflict => Problem(StatusCodes.Status409Conflict, error),
            ResultStatus.Unauthorized => Problem(StatusCodes.Status401Unauthorized, error),
            _ => throw new ArgumentOutOfRangeException(
                nameof(status), status, $"Unmapped {nameof(ResultStatus)}"),
        };

        private static IActionResult CrossTenantNotFound() =>
            Problem(StatusCodes.Status404NotFound, error: null);

        private static IActionResult Problem(int statusCode, string error) =>
            new ObjectResult(new ProblemDetails
            {
                Status = statusCode,
                Title = ReasonPhrase(statusCode),
                Detail = error,
            })
            {
                StatusCode = statusCode,
            };

        private static string ReasonPhrase(int statusCode) => statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status404NotFound => "Not Found",
            StatusCodes.Status409Conflict => "Conflict",
            _ => "Error",
        };
    }
}
