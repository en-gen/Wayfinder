using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public abstract class RuleEvaluated<TResult> : BaseUpdate
    {
        [Id(0)]
        public TResult Result { get; set; }
        [Id(1)]
        public string Error { get; set; }
    }
}
