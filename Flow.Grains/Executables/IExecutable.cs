using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Executables
{
    public interface IExecutable
    {
        IExecutable WithArgument<TArgument>(TArgument argument)
            where TArgument : IExpressionArgument;

        IExecutable WithContext(object values);

        ExecutableResult<string> ExecuteAsString();
        ExecutableResult<bool> ExecuteAsBool();
    }
}
