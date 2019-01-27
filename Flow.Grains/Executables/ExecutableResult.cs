namespace Flow.Grains.Executables
{
    public class ExecutableResult<TResult>
    {
        public TResult Value { get; }
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
