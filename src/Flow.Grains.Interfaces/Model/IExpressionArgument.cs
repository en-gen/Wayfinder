namespace Flow.Grains.Interfaces.Model
{
    public interface IExpressionArgument
    {
        string Name { get; }
        object Value { get; }
    }
}
