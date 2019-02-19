using System.Threading.Tasks;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Definitions;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Plan.Case
{
    public class CaseGrain : CmmnElementGrain<CaseStore, Interfaces.Model.Case>, ICaseGrain
    {
        public CaseGrain(ILogger logger) :
            base(logger)
        {
        }
    }
}
