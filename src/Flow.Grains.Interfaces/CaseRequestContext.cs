using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Flow.Grains.Interfaces
{
    public static class CaseRequestContext
    {
        // RequestContext.PropagateActivityId was removed from modern Orleans (it relied on
        // System.Diagnostics.Trace.CorrelationManager.ActivityId, which NETStandard doesn't
        // support). Cross-grain correlation now flows through System.Diagnostics.Activity;
        // see CmmnElementGrain/CaseDefinitionGrain, which log Activity.Current?.Id instead of
        // RequestContext.ActivityId.

        private const string TenantIdKey = "TENANT_ID";
        private const string UserIdKey = "USER_ID";
        private const string UserRolesKey = "USER_ROLES";
        private const string ActorPrincipalTypeKey = "ACTOR_PRINCIPAL_TYPE";
        private const string ActorOnBehalfOfKey = "ACTOR_ON_BEHALF_OF";

        public static Guid TenantId
        {
            get => RequestContext.Get(TenantIdKey) as Guid? ?? throw new InvalidOperationException("RequestContext TenantId not set");
            set => RequestContext.Set(TenantIdKey, value);
        }

        public static Guid UserId
        {
            get => RequestContext.Get(UserIdKey) as Guid? ?? throw new InvalidOperationException("RequestContext UserId not set");
            set => RequestContext.Set(UserIdKey, value);
        }

        // Non-throwing counterparts of TenantId/UserId above - for callers that only want the
        // ambient identity for best-effort purposes (e.g. log-context enrichment) and must not
        // fail when there is none, such as a grain reactivated by a background stream delivery or
        // reminder rather than a caller-initiated request (no client call chain means no
        // RequestContext to propagate). Deliberately NOT used anywhere the tenant-enforcement
        // contract applies (CrossTenantAccessException checks, cross-tenant grain addressing,
        // etc.) - those must keep throwing via TenantId/UserId above so an unset tenant is never
        // silently treated as "no tenant" at an enforcement boundary.
        public static Guid? TenantIdOrNull => RequestContext.Get(TenantIdKey) as Guid?;

        public static Guid? UserIdOrNull => RequestContext.Get(UserIdKey) as Guid?;

        public static IEnumerable<string> UserRoles
        {
            get => RequestContext.Get(UserRolesKey) as string[] ?? Array.Empty<string>();
            set => RequestContext.Set(UserRolesKey, value?.ToArray() ?? Array.Empty<string>());
        }

        // ADO #59 - what kind of principal UserId names for this call. Deliberately defaults to
        // User (rather than throwing like TenantId/UserId above) - unlike those two, this is new
        // plumbing with no #33-style enforcement yet, and every caller today IS a User
        // (IdentityContextMiddleware only ever sets User/null - see its remarks), so an unset
        // context defaulting to User is simply correct, not a masked bug.
        public static ActorPrincipalType ActorPrincipalType
        {
            // Stored as its underlying int, not the boxed enum itself - RequestContext's
            // client-to-silo propagation carries values through an object-typed dictionary, and an
            // unregistered custom enum type boxed into that slot does not reliably survive the
            // round trip the way a well-known BCL type (Guid, string) does; the int does.
            get => RequestContext.Get(ActorPrincipalTypeKey) as int? is int raw ? (ActorPrincipalType)raw : ActorPrincipalType.User;
            set => RequestContext.Set(ActorPrincipalTypeKey, (int)value);
        }

        // ADO #59 - an end-user identity ASSERTED by an S2S client (ActorPrincipalType.Client),
        // recorded distinctly from the authenticated principal (UserId) rather than overwriting
        // it - the audit trail this feeds (event actor stamping - see CmmnElementGrain.RaiseEvent/
        // Events.ActorStamping) must be able to tell "client X, acting on behalf of user Y" apart
        // from "user Y, acting directly". Null whenever no on-behalf-of identity is in play (every
        // caller today - no S2S path exists yet; IdentityContextMiddleware leaves this unset).
        public static string ActorOnBehalfOf
        {
            get => RequestContext.Get(ActorOnBehalfOfKey) as string;
            set => RequestContext.Set(ActorOnBehalfOfKey, value);
        }
    }
}
