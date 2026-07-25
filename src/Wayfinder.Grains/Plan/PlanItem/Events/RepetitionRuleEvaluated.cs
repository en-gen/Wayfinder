using System;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // 8.6.4 RepetitionRule
    // ~~~~~
    // "The first time a ... instance is instantiated and transitions to the Available state it
    // is not considered a repetition, nevertheless the RepetitionRule MUST be evaluated and its
    // result discarded." Discard marks that first, non-actionable evaluation so
    // PlanItemStore/CaseStore can apply the audit trail (Updated) without letting Result/Error
    // overwrite the persisted Repeatable flag - see BaseBehavior.EvaluateRepetitionRule.
    [GenerateSerializer]
    public class RepetitionRuleEvaluated : RuleEvaluated<bool>
    {
        [Id(2)]
        public bool Discard { get; set; }
    }
}
