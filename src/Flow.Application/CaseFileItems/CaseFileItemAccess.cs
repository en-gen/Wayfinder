using System;
using System.Threading.Tasks;
using Flow.Application.Cases;
using Flow.Grains.Interfaces.Plan.Case;
using Orleans;

namespace Flow.Application.CaseFileItems
{
    // ADO #58 - case-file item reads must honor the exact same tenant/existence isolation as a
    // current-value (case) read (see ADO #33/CaseTenantIsolationIntegrationTests). A CaseFileItem
    // grain carries no TenantId of its own (see CaseFileItemStore's remarks - it is case-scoped,
    // not independently tenant-stamped), so it cannot enforce that itself the way CaseGrain.
    // GetSnapshot/Trigger do (State.TenantId != CaseRequestContext.TenantId). This mirrors
    // CaseFileItemGrain's OWN existing delegation pattern for a different concern
    // (EnsureCaseNotClosed resolves the owning ICaseGrain to ask "is my case Closed?") - here the
    // question is "is my case MINE?", answered the same way: resolve the owning ICaseGrain and
    // let CaseGrain.GetSnapshot's own enforcement answer it. CrossTenantAccessException
    // propagates uncaught (exactly like GetCaseQueryHandler/TriggerCaseCommandHandler already do)
    // for ResultExtensions.ToActionResultAsync to map to 404 at the HTTP edge.
    internal static class CaseFileItemAccess
    {
        // Returns true only if the owning case exists AND (implicitly, via CaseGrain.GetSnapshot's
        // own guard) belongs to the caller's CaseRequestContext.TenantId - throws
        // CrossTenantAccessException otherwise, left uncaught for the caller's own
        // ToActionResultAsync mapping.
        public static async Task<bool> OwningCaseExistsAsync(IClusterClient clusterClient, Guid caseId)
        {
            var caseGrain = clusterClient.GetGrain<ICaseGrain>(caseId, CaseViewProjector.CaseScope);
            var snapshot = await caseGrain.GetSnapshot();
            return snapshot?.Definition != null;
        }
    }
}
