using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Definitions
{
    public class PlanItemDefinitionGrain : Grain<PlanItemDefinitionStore>, IPlanItemDefinitionGrain
    {
        private Guid _tenantId;
        private string _scope;
        private string _id;

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _tenantId = this.GetPrimaryKey(out var address);
            _scope = address.Substring(0, address.LastIndexOf('.'));
            _id = address.Split('.').Last();
            return base.OnActivateAsync(cancellationToken);
        }

        public Task Define(PlanItemDefinition planItemDefinition)
        {
            if (State.Created != null) throw new InvalidOperationException($"PlanItemDefinition {_id} already defined for tenant {_tenantId}");
            if (planItemDefinition.Id != _id) throw new ArgumentException($"PlanItemDefinition id {planItemDefinition.Id} must match grain id {_id}");

            State.PlanItemDefinition = planItemDefinition;
            State.Created = DateTime.UtcNow;
            return WriteStateAsync();
        }

        public Task<PlanItemDefinition> Definition() => Task.FromResult(State.PlanItemDefinition);
        public Task<bool> IsDefined() => Task.FromResult(State.Created != null);
    }
}
