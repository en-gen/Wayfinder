using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.CmmnElementGrain;

namespace Flow.Grains.Interfaces.Plan.Case
{
    public interface ICaseGrain : ICmmnElementGrain<Model.Case>
    {
        Task Create(string caseDefinitionId);
        Task<CaseSnapshot> Trigger(PlanItemTransition transition);
        Task<CaseSnapshot> GetSnapshot();
    }
}
