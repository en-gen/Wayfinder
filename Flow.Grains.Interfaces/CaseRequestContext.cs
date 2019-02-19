using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Flow.Grains.Interfaces
{
    public static class CaseRequestContext
    {
        static CaseRequestContext()
        {
            RequestContext.PropagateActivityId = true;
        }

        private const string TenantIdKey = "TENANT_ID";
        private const string UserIdKey = "USER_ID";
        private const string UserRolesKey = "USER_ROLES";

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

        public static IEnumerable<string> UserRoles
        {
            get => RequestContext.Get(UserRolesKey) as string[] ?? Array.Empty<string>();
            set => RequestContext.Set(UserRolesKey, value?.ToArray() ?? Array.Empty<string>());
        }
    }
}
