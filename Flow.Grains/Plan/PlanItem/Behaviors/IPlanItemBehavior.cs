using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public interface IPlanItemBehavior
    {
        Task Trigger(PlanItemTransition transition);
    }
}
