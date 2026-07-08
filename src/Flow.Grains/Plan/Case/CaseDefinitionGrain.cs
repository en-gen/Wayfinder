using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.PlanItem.Definitions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Runtime;

namespace Flow.Grains.Plan.Case
{
    public class CaseDefinitionGrain : JournaledGrain<CaseDefinitionStore>, ICaseDefinitionGrain
    {
        private readonly ILogger _logger;

        private Guid _tenantId;
        private string _caseDefinitionId;

        private readonly IDictionary<string, object> _logContext;

        public CaseDefinitionGrain(ILogger<CaseDefinitionGrain> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logContext = new Dictionary<string, object>();
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _tenantId = this.GetPrimaryKey(out _caseDefinitionId);

            _logContext["CorrelationId"] = System.Diagnostics.Activity.Current?.Id;
            _logContext["Element"] = typeof(Interfaces.Model.Case).Name;

            await base.OnActivateAsync(cancellationToken);

            if (State.Defined)
            {
                _logContext["ElementDefinitionId"] = State.Definition.Id;
            }
        }

        public Task<bool> Defined() => Task.FromResult(State.Defined);
        
        public async Task Define(Interfaces.Model.Case definition)
        {
            var casePlanModelNode = new DefinitionGraphNode(definition.CasePlanModel.Id);
            await CreateStageDefinitions(definition.CasePlanModel, casePlanModelNode);

            RaiseEvent(new CaseDefinitionDefined
            {
                Definition = definition,
                DefinitionRoot = casePlanModelNode
            });

            _logContext["ElementDefinitionId"] = definition.Id;

            LogWithContext(logger => logger.LogInformation(
                "{Element} {ElementDefinitionId} | new case definition configured",
                typeof(Interfaces.Model.Case).Name,
                definition.Id));

            await ConfirmEvents();
        }

        public Task<Interfaces.Model.Case> GetDefinition() => Task.FromResult(State.Definition);

        public async Task<PlanItemDefinition> GetPlanItemDefinition(string scope, string definitionId)
        {
            if (!State.Defined) throw new InvalidOperationException("case has not yet been defined");

            var chunks = scope.Split('.').ToList();

            while (chunks.Any())
            {
                var address = $"{string.Join('.', chunks)}.{definitionId}";
                if (State.DefinitionIndex.TryGetValue(address, out var node))
                {
                    return await GrainFactory.GetGrain<IPlanItemDefinitionGrain>(_tenantId, $"{_caseDefinitionId}.{node.Address}").Definition();
                }

                // remove last chunk to search upwards in hierarchy
                chunks.RemoveAt(chunks.Count - 1);
            }

            return null;
        }

        private Task CreateStageDefinitions(Stage stage, DefinitionGraphNode node) =>
            Task.WhenAll(stage.PlanItemDefinitions.Select(async planItemDef =>
            {
                var subNode = node.AddNode(planItemDef.Id);

                await GrainFactory
                    .GetGrain<IPlanItemDefinitionGrain>(_tenantId, $"{_caseDefinitionId}.{subNode.Address}")
                    .Define(planItemDef);

                if (planItemDef is Stage subStage)
                {
                    await CreateStageDefinitions(subStage, subNode);
                }
            }));

        private void LogWithContext(Action<ILogger> logAction)
        {
            using (_logger.BeginScope(_logContext))
            {
                logAction?.Invoke(_logger);
            }
        }
    }
}
