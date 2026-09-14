using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.Case.Events;
using Wayfinder.Grains.Plan.PlanItem.Definitions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Runtime;

namespace Wayfinder.Grains.Plan.Case
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
            // ADO #66 - Case.ExitCriteria was disconnected from CasePlanModel.ExitCriteria
            // ~~~~~
            // Case.EntryCriteria/Case.ExitCriteria (Model/Case.cs) exist solely so the Case
            // object itself satisfies IBehaviorDefinition for CaseGrain.IBehaviorHost.Definition
            // (see CmmnXmlSerializer.cs's BuildOverrides remarks) - and that Host.Definition,
            // not State.Definition.CasePlanModel, is exactly what BaseBehavior.SubscribeToCriteria
            // / HandleSentrySatisfied read (Host.Definition.ExitCriteria) for the CasePlanModel's
            // own behavior. tCase has no exitCriterion element of its own in the XSD - only the
            // outermost tStage does ("if the Stage is the outermost Stage, zero or more references
            // to exitCriterion", Spec.CMMN.MODEL.cs) - so a .cmmn file's (or a directly-constructed
            // Case's) real CasePlanModel-level exit criteria land correctly on
            // CasePlanModel.ExitCriteria, never on Case.ExitCriteria. Left unconnected, Case.
            // ExitCriteria stayed permanently empty regardless of what was authored, so the
            // CasePlanModel's own exit-criteria subscription (once armed - see
            // CasePlanModelBehavior.HandleEnterActiveFromCreate) enumerated zero criteria and its
            // HandleSentrySatisfied never matched a satisfied sentry to a criterion. Copied once
            // here, at definition time (not per Case instance in CaseGrain.Create): exit criteria
            // are definition-level data, identical for every instance of this case definition, and
            // this is the one point where the definition object is still being assembled before it
            // becomes the confirmed, canonical CaseDefinitionDefined payload below. Case.
            // EntryCriteria is deliberately NOT mirrored the same way - it must stay the fixed
            // empty list (8.4.1: the outermost Stage instance MUST NOT contain entry criteria).
            foreach (var exitCriterion in definition.CasePlanModel.ExitCriteria)
            {
                definition.ExitCriteria.Add(exitCriterion);
            }

            var casePlanModelNode = new DefinitionGraphNode(definition.CasePlanModel.Id);
            await CreateStageDefinitions(definition.CasePlanModel, casePlanModelNode);

            // ADO #59 - this grain is the one other JournaledGrain root in the codebase besides
            // CmmnElementGrain<,> (it does not derive from it - see CaseDefinitionGrain's own type
            // declaration), so it cannot pick up CmmnElementGrain.RaiseEvent's shadowed stamping.
            // Same shared helper, called explicitly at this grain's one and only RaiseEvent call
            // site instead - not a second, divergent stamping implementation.
            var caseDefinitionDefined = new CaseDefinitionDefined
            {
                Definition = definition,
                DefinitionRoot = casePlanModelNode
            };
            ActorStamping.Apply(caseDefinitionDefined);
            RaiseEvent(caseDefinitionDefined);

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

        // Design 05 section A.5. Same resolution GetPlanItemDefinition performs, for every node
        // at once: DefinitionIndex's keys ARE the addresses that method searches, so a map built
        // here can be walked upwards by CaseModelPin.Resolve with identical results. The index's
        // root entry is the casePlanModel node itself, which CreateStageDefinitions never
        // registered a PlanItemDefinitionGrain for - it resolves to null and is dropped.
        public async Task<Dictionary<string, PlanItemDefinition>> GetPlanItemDefinitions()
        {
            if (!State.Defined) throw new InvalidOperationException("case has not yet been defined");

            var resolved = await Task.WhenAll(State.DefinitionIndex.Values
                .Select(async node => (
                    node.Address,
                    definition: await GrainFactory
                        .GetGrain<IPlanItemDefinitionGrain>(_tenantId, $"{_caseDefinitionId}.{node.Address}")
                        .Definition())));

            return resolved
                .Where(x => x.definition != null)
                .ToDictionary(x => x.Address, x => x.definition);
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
