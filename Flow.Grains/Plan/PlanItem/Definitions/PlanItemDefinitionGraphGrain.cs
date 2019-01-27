using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    public class PlanItemDefinitionGraphGrain : Grain<PlanItemDefinitionGraphStore>, IPlanItemDefinitionGraphGrain
    {
        private Guid _flowId;

        public override Task OnActivateAsync()
        {
            _flowId = this.GetPrimaryKey();
            return base.OnActivateAsync();
        }

        public Task Construct(Interfaces.Model.Case @case)
        {
            if (_flowId.ToString() != @case.Id)
                throw new ArgumentException($"case id {@case.Id} does not match grain id {_flowId}");

            var rootNode = new Node {Id = @case.CasePlanModel.Id};
            State.IndexNode(rootNode);
            return CreateStageDefinitions(@case.CasePlanModel, rootNode);
        }

        public async Task<PlanItemDefinition> Find(string scope, string id)
        {
            if(State.Index.Count == 0) throw new InvalidOperationException("graph has not been constructed");

            var chunks = scope.Split('.').ToList();

            while(chunks.Any())
            {
                var address = $"{string.Join('.', chunks)}.{id}";
                if (State.Index.TryGetValue(address, out var node))
                {
                    return await GrainFactory.GetGrain<IPlanItemDefinitionGrain>(_flowId, node.Address).Definition();
                }

                // remove last chunk to search upwards in hierarchy
                chunks.RemoveAt(chunks.Count - 1);
            }

            return null;
        }

        private Task CreateStageDefinitions(Stage stage, Node node) =>
            Task.WhenAll(stage.PlanItemDefinitions.Select(async planItemDef =>
            {
                var subNode = node.AddNode(planItemDef.Id);
                State.IndexNode(subNode);

                await GrainFactory
                    .GetGrain<IPlanItemDefinitionGrain>(_flowId, subNode.Address)
                    .Define(planItemDef);

                if (planItemDef is Stage subStage)
                {
                    await CreateStageDefinitions(subStage, subNode);
                }
            }));
    }
}
