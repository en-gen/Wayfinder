using Orleans;

namespace Flow.Grains.Executables
{
    [GenerateSerializer]
    public class ExecutableResult<TResult>
    {
        [Id(0)]
        public TResult Value { get; }
        [Id(1)]
        public string Message { get; }

        public bool IsError => !string.IsNullOrWhiteSpace(Message);

        private ExecutableResult(TResult result)
        {
            Value = result;
        }

        private ExecutableResult(string errorMessage)
        {
            Message = errorMessage;
        }

        public static ExecutableResult<TResult> Success(TResult result) => new ExecutableResult<TResult>(result);

        public static ExecutableResult<TResult> Failure(string message) => new ExecutableResult<TResult>(message);
    }
}
