using System;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Events
{
    [GenerateSerializer]
    public class PlanItemTransitionedEvent : BaseEvent
    {
        [Id(0)]
        public string SourceInstanceId { get; }
        [Id(1)]
        public PlanItemTransition StandardEvent { get; }

        [Id(2)]
        public PlanItemState Source { get; }
        [Id(3)]
        public PlanItemState Destination { get; }

        [Id(4)]
        public string ExitCriterionRef { get; }

        // #198 - the terminal child's OWN repetition verdict (8.6.4's no-entry-criteria
        // RepetitionRule re-evaluation), carried on the SAME event that announces the Complete/
        // Terminate transition rather than left to arrive later on the separate
        // PlanItemRepetitionCriteriaMetEvent stream. BaseBehavior.HandleTransitioned now decides
        // this BEFORE publishing (see its remarks), so by the time a parent Stage observes
        // Destination.IsTerminal() it also already knows whether a replacement instance is
        // coming - closing the race StageBehavior.HandleChildTransitioned's Table 8.12 check used
        // to lose silently (docs/03-cmmn-execution-semantics.md section 8's implementation note).
        // Always false for every transition other than the specific Complete->Completed/
        // Terminate->Terminated pair this rule applies to, and for any item ineligible per 8.6.4
        // (Milestones/EventListeners, the CasePlanModel, items WITH entry criteria, items with no
        // RepetitionRule) - see BaseBehavior.EvaluateRepetitionOnTerminalTransition.
        [Id(5)]
        public bool WillRepeat { get; }

        public PlanItemTransitionedEvent(
            string planItemScope,
            string planItemInstanceId,
            string planItemDefinitionId,
            PlanItemTransition standardEvent,
            PlanItemState source,
            PlanItemState destination,
            string exitCriterionRef = null,
            bool willRepeat = false) :
            base(planItemScope, planItemDefinitionId)
        {
            SourceInstanceId = planItemInstanceId;
            StandardEvent = standardEvent;
            Source = source;
            Destination = destination;
            ExitCriterionRef = exitCriterionRef;
            WillRepeat = willRepeat;
        }
    }
}
