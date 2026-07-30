using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.CmmnElement;
using Microsoft.Extensions.Logging;

namespace Wayfinder.Grains.Plan.PlanningTable
{
    public class PlanningTableGrain :
        CmmnElementGrain<PlanningTableStore, Interfaces.Model.PlanningTable>,
        IPlanningTableGrain
    {
        public PlanningTableGrain(ILogger<PlanningTableGrain> logger) :
            base(logger)
        {
        }

        public Task<DiscretionaryItem[]> GetPlannableItems()
        {
            return GetPlannableItems(TentativeState.Definition);
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
                    .Select(async ruleRef => applicabilityRules.TryGetValue(ruleRef, out var rule)
                                             && await EvaluateApplicabilityRule(rule)));

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

        // 5.36 - TableItem attributes
        // ~~~~~
        // If the condition of the ApplicabilityRule object evaluates
        // to TRUE, then the TableItem is applicable for planning,
        // otherwise it is not. If no ApplicabilityRule is associated
        // with a TableItem, its applicability is considered TRUE.
        //
        // #158 - same defect class as BaseBehavior.EvaluateRule: an erroring Condition must fall
        // back to this rule's spec default (TRUE) via ExecutableResult.ValueOr rather than a bare
        // `?.Value ?? true`, since Value is never actually null on a Failure result. Here the
        // consequence was fail-CLOSED (an erroring rule silently dropped the DiscretionaryItem from
        // the planning table) rather than #158's fail-open, but it's the identical coalesce bug.
        //
        // #184 - this method is reached only from the read-only GetPlannableItems() query (never
        // from a write/transition path), so it deliberately does NOT RaiseEvent an
        // ApplicabilityRuleEvaluated here. It previously did, on every call, with no ConfirmEvents()
        // anywhere on this path - journal size scaled 1:1 with read/poll volume rather than case
        // activity. Nothing consumes that event today (no Apply override on PlanningTableStore
        // beyond the inherited BaseStore.Apply(BaseUpdate), which only bumps a store-level Updated
        // timestamp that is never mapped into any snapshot - PlanningTableStore has no ToSnapshot at
        // all), so dropping the raise is a pure journaling-hygiene fix: the applicability result
        // below is still computed identically, including the #158 fail-open ValueOr(true) default
        // and the error logging just below.
        private async Task<bool> EvaluateApplicabilityRule(ApplicabilityRule rule)
        {
            ExecutableResult<bool> ruleResult = null;
            if (rule?.Condition != null)
            {
                ruleResult = await GrainFactory.GetGrain<IExpressionGrain>(_caseInstanceId)
                    .ExecuteAsBool(rule.ContextRef, rule.Condition);
            }
            var result = ruleResult?.ValueOr(true) ?? true;

            if (ruleResult?.IsError ?? false)
            {
                LogWithContext(logger => logger.LogError(
                    "{Element} {ElementScope}.{ElementInstanceId} | Evaluation of applicability rule resulted in error: {ApplicabilityRuleError}",
                    Definition.GetType().Name,
                    _scope,
                    Definition.Id,
                    ruleResult.Message));
            }

            return result;
        }
    }
}
