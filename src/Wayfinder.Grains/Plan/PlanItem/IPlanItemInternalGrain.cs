using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;

namespace Wayfinder.Grains.Plan.PlanItem
{
    public interface IPlanItemInternalGrain : IPlanItemGrain
    {
        // pin: the case model pinned at CaseGrain.Create, threaded down by StageBehavior.
        // CreateChild rather than fetched. Null only on PlanItemGrain.Define's bare 2-arg
        // overload, which has no parent to thread one from.
        Task DefineRepetition(
            string caseDefinitionId,
            Interfaces.Model.PlanItem definition,
            int repetition,
            string parentDefinitionId,
            string parentDefinitionScope,
            CaseModelPin pin);
    }
}
