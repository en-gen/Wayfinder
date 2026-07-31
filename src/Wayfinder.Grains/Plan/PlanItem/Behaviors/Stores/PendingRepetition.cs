using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores
{
    // #178 - a repetition request that arrived while its Stage/CasePlanModel was genuinely
    // Suspended, captured in exactly the shape RepetitionBuffered persisted it in (see that
    // event's remarks) so StageBehavior.DrainPendingRepetitions can replay it without needing the
    // original PlanItemRepetitionCriteriaMetEvent (which is not itself replayed).
    [GenerateSerializer]
    public class PendingRepetition
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
        [Id(1)]
        public string PlanItemDefinitionId { get; set; }
        [Id(2)]
        public int NextRepetition { get; set; }
    }
}
