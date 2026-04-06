using System;
using Flow.Grains.Executables;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class TimerExpressionEvaluated : RuleEvaluated<Iso8601>
    {
    }
}
