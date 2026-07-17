using System;
using Flow.Grains.Executables;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class TimerExpressionEvaluated : RuleEvaluated<Iso8601>
    {
    }
}
