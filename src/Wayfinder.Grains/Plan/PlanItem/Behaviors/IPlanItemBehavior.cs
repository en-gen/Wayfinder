using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
{
    public interface IPlanItemBehavior
    {
        Task Trigger(PlanItemTransition transition);
    }
}
