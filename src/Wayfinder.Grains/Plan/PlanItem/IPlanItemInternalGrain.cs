using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;

namespace Wayfinder.Grains.Plan.PlanItem
{
    public interface IPlanItemInternalGrain : IPlanItemGrain
    {
        // planItemDefinition/pin: design 05 section A.5. The parent resolves this child's
        // PlanItemDefinition out of the model pinned at CaseGrain.Create and hands both down,
        // instead of the child re-reading ICaseDefinitionGrain on every define (repetition
        // spawns included). Both are null only on PlanItemGrain.Define's bare 2-arg overload,
        // which falls back to the old lookup - see that method's remarks.
        Task DefineRepetition(
            string caseDefinitionId,
            Interfaces.Model.PlanItem definition,
            int repetition,
            string parentDefinitionId,
            string parentDefinitionScope,
            PlanItemDefinition planItemDefinition,
            CaseModelPin pin);
    }
}
