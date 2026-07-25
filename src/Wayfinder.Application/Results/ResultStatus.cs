namespace Wayfinder.Application.Results
{
    // ADO #32 - the outcome vocabulary every handler reports through. Deliberately small and HTTP-
    // shaped (a future Wayfinder.Api layer maps each status to a status code 1:1 - Success->200/201,
    // BadRequest->400, NotFound->404, Conflict->409, Unauthorized->401) without this layer itself
    // knowing anything about HTTP.
    public enum ResultStatus
    {
        Success,
        BadRequest,
        NotFound,
        Conflict,
        Unauthorized
    }
}
