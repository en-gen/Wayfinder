using Orleans;

namespace Wayfinder.Grains.Executables
{
    [GenerateSerializer]
    public class ExecutableResult<TResult>
    {
        [Id(0)]
        public TResult Value { get; }
        [Id(1)]
        public string Message { get; }

        // #158 follow-up - IsError was previously *derived* from Message
        // (!string.IsNullOrWhiteSpace(Message)), but a caught exception can legitimately carry a
        // blank Message (e.g. Jint's `throw new Error()` / `throw ''` yield Exception.Message ==
        // ""), which silently made a genuine failure indistinguishable from success. Stored
        // directly by the two factories below instead, so a blank-message failure still reports
        // IsError == true.
        [Id(2)]
        public bool IsError { get; }

        private ExecutableResult(TResult result)
        {
            Value = result;
            IsError = false;
        }

        private ExecutableResult(string errorMessage)
        {
            Message = errorMessage;
            IsError = true;
        }

        public static ExecutableResult<TResult> Success(TResult result) => new ExecutableResult<TResult>(result);

        public static ExecutableResult<TResult> Failure(string message) => new ExecutableResult<TResult>(message);

        // Routes every EvaluateRule-shaped call site through one place, so the hand-rolled
        // `result?.Value ?? default` coalesce (which produced #158: Value is never actually null on
        // failure, so that idiom silently let a failure's default(TResult) masquerade as a real
        // value) cannot be reproduced by a future caller. Callers still need their own null-check
        // for "no rule was configured at all" (no ExecutableResult exists yet) - only the
        // error-vs-value distinction lives here.
        public TResult ValueOr(TResult fallback) => IsError ? fallback : Value;
    }
}
