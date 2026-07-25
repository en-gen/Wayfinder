using System;
using Wayfinder.Grains.Executables;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class TimerExpressionEvaluated : RuleEvaluated<Iso8601>
    {
    }
}
