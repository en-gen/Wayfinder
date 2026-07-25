namespace Wayfinder.Contracts.V1
{
    // ADO #32 - the future POST /api/cases request body (Unit 2). Carried here so the DTO's shape
    // is versioned alongside the rest of V1 even though no HTTP layer exists yet in this unit.
    public sealed class CreateCaseRequest
    {
        public string DefinitionId { get; init; }
    }
}
