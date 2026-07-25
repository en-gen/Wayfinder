using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;

namespace Wayfinder.Grains.Interfaces.Plan.Case
{
    public interface ICaseGrain : ICmmnElementGrain<Model.Case>
    {
        Task Create(string caseDefinitionId);
        Task<CaseSnapshot> Trigger(PlanItemTransition transition);
        Task<CaseSnapshot> GetSnapshot();
    }
}
