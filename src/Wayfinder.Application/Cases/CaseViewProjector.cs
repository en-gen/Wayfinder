using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Contracts.V1;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem;
using Orleans;
using ContractPlanItemState = Wayfinder.Contracts.V1.PlanItemState;
using DomainPlanItemState = Wayfinder.Grains.Interfaces.Model.PlanItemState;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - projects the rich internal CaseSnapshot down to the wire-shaped Wayfinder.Contracts.V1.
    // CaseView, lifted from #39's CaseOperations.BuildCaseViewAsync/CollectPlanItemsAsync. Shared by
    // CreateCaseCommandHandler, TriggerCaseCommandHandler, and GetCaseQueryHandler - the three
    // handlers that hand a live CaseSnapshot back to a caller.
    internal static class CaseViewProjector
    {
        // The case root's grain-key scope segment. Fixed convention (matches #39/the integration
        // tests): the ICaseGrain is keyed (caseInstanceId, "CPM"), and every child plan item it
        // instantiates is addressed at "{caseScope}.{childInstanceId}" (see StageBehavior.
        // CreateChild) - so this same string is the root of every plan-item address.
        public const string CaseScope = "CPM";

        public static async Task<CaseView> BuildAsync(IClusterClient clusterClient, Guid caseInstanceId, CaseSnapshot snapshot)
        {
            var planItems = new List<PlanItemView>();
            await CollectPlanItemsAsync(clusterClient, caseInstanceId, CaseScope, snapshot.BehaviorExtension, planItems);

            return new CaseView
            {
                CaseId = caseInstanceId,
                DefinitionId = snapshot.Definition?.Id,
                State = ToContract(snapshot.PlanItemState),
                // Stable ordering: Children is a Dictionary (unordered) - order by id so a caller
                // sees a deterministic list.
                PlanItems = planItems.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()
            };
        }

        // Walks the StageBehavior child map (planItemId => instanceId => repetition), resolving each
        // instantiated plan item's grain to read its live state/type, then recursing into any child
        // that is itself a stage. Child grains are addressed "{parentAddress}.{instanceId}", exactly
        // how StageBehavior.CreateChild addressed them when it instantiated them.
        private static async Task CollectPlanItemsAsync(
            IClusterClient clusterClient,
            Guid caseInstanceId,
            string parentAddress,
            StageBehaviorSnapshot stageBehavior,
            List<PlanItemView> accumulator)
        {
            if (stageBehavior?.Children == null) return;

            foreach (var (planItemId, instances) in stageBehavior.Children)
            {
                if (instances == null) continue;

                foreach (var instanceId in instances.Keys)
                {
                    var address = $"{parentAddress}.{instanceId}";
                    var planItemSnapshot = await clusterClient
                        .GetGrain<IPlanItemInternalGrain>(caseInstanceId, address)
                        .GetSnapshot();

                    if (planItemSnapshot == null) continue;

                    accumulator.Add(new PlanItemView
                    {
                        // Model.PlanItem.Id is the <planItem> instance id (e.g. "PlanItemA");
                        // planItemId is the same value (the child map is keyed by it) - prefer the
                        // snapshot's own copy, fall back to the map key.
                        Id = planItemSnapshot.Definition?.Id ?? planItemId,
                        Name = planItemSnapshot.Definition?.Name ?? planItemSnapshot.PlanItemDefinition?.Name,
                        // PlanItemDefinition has no explicit "kind" attribute - its CLR type IS the
                        // kind (Milestone, HumanTask, Stage, ...).
                        Type = planItemSnapshot.PlanItemDefinition?.GetType().Name,
                        State = ToContract(planItemSnapshot.PlanItemState)
                    });

                    // A plan item backed by a Stage tracks its own children in a StageBehaviorSnapshot
                    // (PlanItemSnapshot.BehaviorExtension is typed object; only stages carry one).
                    if (planItemSnapshot.BehaviorExtension is StageBehaviorSnapshot childStage)
                    {
                        await CollectPlanItemsAsync(clusterClient, caseInstanceId, address, childStage, accumulator);
                    }
                }
            }
        }

        // The anti-corruption boundary between the domain's PlanItemState (Wayfinder.Grains.Interfaces.
        // Model) and the wire's PlanItemState (Wayfinder.Contracts.V1) - see Wayfinder.Contracts.V1.
        // PlanItemState's remarks for why Wayfinder.Contracts does not simply reference the domain enum.
        // An explicit switch (not a same-ordinal cast) so an unmapped new domain member fails loudly
        // at runtime instead of silently mis-mapping.
        private static ContractPlanItemState ToContract(DomainPlanItemState state) => state switch
        {
            DomainPlanItemState.Uninitialized => ContractPlanItemState.Uninitialized,
            DomainPlanItemState.Available => ContractPlanItemState.Available,
            DomainPlanItemState.Enabled => ContractPlanItemState.Enabled,
            DomainPlanItemState.Disabled => ContractPlanItemState.Disabled,
            DomainPlanItemState.Active => ContractPlanItemState.Active,
            DomainPlanItemState.Suspended => ContractPlanItemState.Suspended,
            DomainPlanItemState.Failed => ContractPlanItemState.Failed,
            DomainPlanItemState.Completed => ContractPlanItemState.Completed,
            DomainPlanItemState.Closed => ContractPlanItemState.Closed,
            DomainPlanItemState.Terminated => ContractPlanItemState.Terminated,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, $"Unmapped {nameof(DomainPlanItemState)} value.")
        };
    }
}
