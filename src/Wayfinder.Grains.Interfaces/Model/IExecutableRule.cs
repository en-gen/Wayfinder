namespace Wayfinder.Grains.Interfaces.Model
{
    public interface IExecutableRule
    {
        string ContextRef { get; }
        Expression Condition { get; }
    }
}
