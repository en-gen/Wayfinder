using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Tests.Utils.Helpers;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    // ADO #67 / issue #153: the exact #19 foot-gun shape (non-blocking task,
    // RepetitionRule=TRUE, ManualActivationRule=FALSE, no entry criteria, blocking sentinel
    // sibling to keep the CasePlanModel alive past the first repetition - see
    // RepetitionGuardFootgunIntegrationTests's own remarks for the full CMMN-table rationale),
    // extracted into one builder both RepetitionGuardFootgunIntegrationTests (the real,
    // asserted-on test) and RepetitionGuardClusterFixture's warm-up share. Before this, the
    // warm-up hand-mirrored the test's model as a second, best-effort/swallowed copy that could
    // silently drift out of sync (and stop actually warming up the paths it needs to) if the real
    // test's shape ever changed without the warm-up being updated to match. Callers supply their
    // own ids: the real test passes its existing constants (Scope/TaskDefinitionId/etc.), so its
    // behavior and assertions are unchanged; the warm-up passes distinct "WarmUp*" ids so it can
    // never interact with the real test's state.
    internal static class FootgunCaseBuilder
    {
        public static CaseModel BuildFootgunCase(
            string caseDefinitionId,
            string scope,
            string taskDefinitionId,
            string taskPlanItemId,
            string sentinelDefinitionId,
            string sentinelPlanItemId)
        {
            var taskDefinition = new HumanTask { Id = taskDefinitionId, IsBlocking = false };
            var sentinelDefinition = new HumanTask { Id = sentinelDefinitionId, IsBlocking = true };

            return new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = scope,
                    PlanItemDefinitions = { taskDefinition, sentinelDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = taskPlanItemId,
                            DefinitionRef = taskDefinition.Id,
                            ItemControl = new PlanItemControl
                            {
                                RepetitionRule = Rules.IsRepeatableRule,
                                ManualActivationRule = Rules.NotManuallyActivated
                            }
                        },
                        new Interfaces.Model.PlanItem
                        {
                            Id = sentinelPlanItemId,
                            DefinitionRef = sentinelDefinition.Id
                        }
                    }
                }
            };
        }
    }
}
