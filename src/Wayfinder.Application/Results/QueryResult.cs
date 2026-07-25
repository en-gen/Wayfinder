using System.Collections.Generic;

namespace Wayfinder.Application.Results
{
    // ADO #32 - the read-side counterpart to CommandResult<T>; see that file's remarks. Carries the
    // full status vocabulary (not just Success/NotFound) for symmetry and future queries that need
    // it, even though today's GetCaseQuery only ever returns Success or NotFound.
    public sealed class QueryResult<T> : Result
    {
        private QueryResult(ResultStatus status, T value, string error, IReadOnlyList<string> warnings)
            : base(status, error, warnings)
        {
            Value = value;
        }

        public T Value { get; }

        public static QueryResult<T> Success(T value, IReadOnlyList<string> warnings = null) =>
            new(ResultStatus.Success, value, null, warnings);

        public static QueryResult<T> BadRequest(string error) =>
            new(ResultStatus.BadRequest, default, error, null);

        public static QueryResult<T> NotFound(string error = null) =>
            new(ResultStatus.NotFound, default, error, null);

        public static QueryResult<T> Conflict(string error) =>
            new(ResultStatus.Conflict, default, error, null);

        public static QueryResult<T> Unauthorized(string error = null) =>
            new(ResultStatus.Unauthorized, default, error, null);
    }
}
