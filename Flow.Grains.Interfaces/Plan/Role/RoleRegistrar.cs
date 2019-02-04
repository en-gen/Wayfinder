using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Flow.Grains.Interfaces.Plan.Role
{
    public static class RoleRegistrar
    {
        // TODO: maybe an extension method on a user object, or something...
        public static void Register(IEnumerable<string> roles)
        {
            RequestContext.Set(RequestContextConstants.CurrentUserRoles, roles.ToArray());
        }

        public static IEnumerable<string> GetRoles()
        {
            return RequestContext.Get(RequestContextConstants.CurrentUserRoles) as string[] ?? Array.Empty<string>();
        }
    }
}
