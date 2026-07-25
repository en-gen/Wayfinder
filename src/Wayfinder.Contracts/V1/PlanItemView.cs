namespace Flow.Contracts.V1
{
    // ADO #32 - one instantiated plan item, projected flat (no nested-stage tree) for a thin V1
    // wire shape. Id/Name come from the PlanItem instance (the <planItem> element - e.g.
    // "PlanItemA"); Type is the CLR type name of the resolved PlanItemDefinition (e.g. "Milestone",
    // "HumanTask", "Stage") since PlanItemDefinition itself has no explicit "kind" attribute in the
    // CMMN model - the .NET type IS the kind. Mirrors #39's Flow.Silo.Api.PlanItemView.
    public sealed class PlanItemView
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Type { get; init; }
        public PlanItemState State { get; init; }
    }
}
