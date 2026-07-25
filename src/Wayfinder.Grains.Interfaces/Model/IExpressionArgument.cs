namespace Wayfinder.Grains.Interfaces.Model
{
    public interface IExpressionArgument
    {
        string Name { get; }
        object Value { get; }
    }
}
