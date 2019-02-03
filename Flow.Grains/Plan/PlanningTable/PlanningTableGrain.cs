using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Plan.PlanningTable
{
    public class PlanningTableGrain :
        CmmnElementGrain<PlanningTableStore, Interfaces.Model.PlanningTable>,
        IPlanningTableGrain
    {
        public PlanningTableGrain(ILogger<PlanningTableGrain> logger) :
            base(logger)
        {
        }

        public Task Define(Interfaces.Model.PlanningTable definition)
        {
            throw new NotImplementedException();
        }

        public Task<DiscretionaryItem[]> GetPlannableItems()
        {
            return GetPlannableItems(TentativeState.Definition);
        }

        public Task PlanDiscretionaryItem(string discretionaryItemId)
        {
            var item = State.Definition.DiscretionaryItems.SingleOrDefault(x => x.Id.Equals(discretionaryItemId));

            if (item == null)
            {
                LogWithContext(logger => logger.LogWarning(
                    "Attempted to plan DiscretionaryItem {DiscretionaryItemId} that is not in this PlanningTable's hierarchy",
                    discretionaryItemId));
                return Task.CompletedTask;
            }

            // TODO: raise event for planned item

            return Task.CompletedTask;
        }

        public Task<DiscretionaryItem[]> GetPlannedItems()
        {
            // TODO
            return Task.FromResult(Array.Empty<DiscretionaryItem>());
        }

        // Table 5.36 - TableItem attributes
        // ~~~~~
        // If the condition of the ApplicabilityRule object evaluates
        // to TRUE, then the TableItem is applicable for planning,
        // otherwise it is not. If no ApplicabilityRule is associated
        // with a TableItem, its applicability is considered TRUE.
        // ==========
        // 5.4.9.3 Applicability Rules
        // ~~~~~
        // TableItems for which an associated ApplicabilityRule evaluates to FALSE will not be exposed
        // to Case workers for planning purpose.
        // TODO: this is kinda fugly
        private async Task<DiscretionaryItem[]> GetPlannableItems(Interfaces.Model.PlanningTable planningTable)
        {
            // determine this table's items that are currently applicable
            var itemApplicability = await Task.WhenAll(planningTable.TableItems.Select(async tableItem =>
            {
                if (tableItem.ApplicabilityRuleRefs == null || !tableItem.ApplicabilityRuleRefs.Any())
                {
                    return (tableItem: tableItem, isApplicable: true);
                }

                var applicabilityRules = planningTable.ApplicabilityRules.ToDictionary(x => x.Id);

                var applicabilityRuleResults = await Task.WhenAll(tableItem.ApplicabilityRuleRefs
                    .Select(async ruleRef => applicabilityRules.TryGetValue(ruleRef, out var rule) &&
                                             await EvaluateApplicabilityRule(rule)));

                return (tableItem: tableItem, isApplicable: applicabilityRuleResults.All(x => x));
            }));

            // for applicable items, return discretionaries, or recurse into sub-table
            var itemStatuses = await Task.WhenAll(itemApplicability
                .Where(x => x.isApplicable)
                .Select(async x =>
                {
                    switch (x.tableItem)
                    {
                        case DiscretionaryItem discretionaryItem:
                            return new[] { discretionaryItem };
                        case Interfaces.Model.PlanningTable table:
                            return await GetPlannableItems(table);
                        default:
                            return Array.Empty<DiscretionaryItem>();
                    }
                }));

            // flatten the arrays
            return itemStatuses.SelectMany(x => x).ToArray();
        }

        public async Task<bool> EvaluateApplicabilityRule(ApplicabilityRule rule)
        {
            // 5.36 - TableItem attributes
            // ~~~~~
            // If the condition of the ApplicabilityRule object evaluates
            // to TRUE, then the TableItem is applicable for planning,
            // otherwise it is not. If no ApplicabilityRule is associated
            // with a TableItem, its applicability is considered TRUE.
            if (rule?.Condition == null) return true;

            var result = await GrainFactory.GetGrain<IExpressionGrain>(_caseInstanceId)
                .ExecuteAsBool(rule.ContextRef, rule.Condition);

            if (result.IsError) return false;

            // TODO: handle fault? - could publish an error event to parent

            return result.Value;
        }
    }
}
