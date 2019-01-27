using System.Threading.Tasks;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public interface IPlanItemBehavior
    {
        // TODO: possibly adapt this interface into a grain lifecycle observer for define and activate
        Task Define();
        Task Activate();
        Task<bool> IsUserCompletable();
    }
}
