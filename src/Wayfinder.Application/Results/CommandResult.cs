using System.Collections.Generic;

namespace Flow.Application.Results
{
    // ADO #32 - what every ICommandHandler<,> returns: the outcome (Result) plus the value produced
    // on success (e.g. a DeployResult or a CaseView). Value is default(T) for any non-Success status -
    // callers must check Succeeded/Status before reading it.
    public sealed class CommandResult<T> : Result
    {
        private CommandResult(ResultStatus status, T value, string error, IReadOnlyList<string> warnings)
            : base(status, error, warnings)
        {
            Value = value;
        }

        public T Value { get; }

        public static CommandResult<T> Success(T value, IReadOnlyList<string> warnings = null) =>
            new(ResultStatus.Success, value, null, warnings);

        public static CommandResult<T> BadRequest(string error) =>
            new(ResultStatus.BadRequest, default, error, null);

        public static CommandResult<T> NotFound(string error = null) =>
            new(ResultStatus.NotFound, default, error, null);

        public static CommandResult<T> Conflict(string error) =>
            new(ResultStatus.Conflict, default, error, null);

        public static CommandResult<T> Unauthorized(string error = null) =>
            new(ResultStatus.Unauthorized, default, error, null);
    }
}
