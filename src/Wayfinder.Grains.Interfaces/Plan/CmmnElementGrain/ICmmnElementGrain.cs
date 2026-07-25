using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.CmmnElementGrain
{
    public interface ICmmnElementGrain<in TDefinition> : IGrainWithGuidCompoundKey
        where TDefinition : Model.CmmnElement
    {
        Task<bool> Defined();
        Task Define(string caseDefinitionId, TDefinition definition);

        // ADO #59 - raw read-back of this grain's confirmed journal (Orleans's own
        // JournaledGrain.RetrieveConfirmedEvents), added as the smallest working seam for this
        // work item's replay-safety tests. See CmmnElementGrain.GetJournaledEvents's remarks -
        // #58 (case-file version history) is expected to want a richer surface than this.
        Task<IReadOnlyList<object>> GetJournaledEvents();
    }
}
