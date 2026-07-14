using System;
using Flow.Grains.Interfaces;

namespace Flow.Application.Cases
{
    // ADO #32 - the "defaulted tenant" seam, lifted verbatim from #39's CaseOperations (a future
    // work item wires real tenancy). CaseRequestContext threads the tenant to the grains via
    // Orleans RequestContext (AsyncLocal): the ICaseDefinitionGrain key is (TenantId, definitionId),
    // and CaseGrain.Create re-reads CaseRequestContext.TenantId to find that same definition grain -
    // so the value MUST be set on THIS async flow before any grain call, and it must be the same
    // Guid used as the definition-grain key. UserId is set too because CmmnElementGrain.OnActivate
    // reads CaseRequestContext.UserId for its log scope and throws if it is unset. Values match
    // CmmnImportDeployIntegrationTests / #39's CaseOperations exactly.
    internal static class CaseRequestContextDefaults
    {
        private static readonly Guid DefaultTenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
        private static readonly Guid DefaultUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        public static void EnsureRequestContext()
        {
            CaseRequestContext.TenantId = DefaultTenantId;
            CaseRequestContext.UserId = DefaultUserId;
        }
    }
}
