using System.Threading;
using System.Threading.Tasks;

namespace Wayfinder.Application.Identity
{
    // ADO #33 - server-side resolution of a token's subject into Wayfinder's internal identity
    // (TenantId/UserId/UserRoles - see Wayfinder.Grains.Interfaces.CaseRequestContext). This is the
    // seam sub-unit 3's HTTP auth middleware calls after authenticating a caller, to turn the
    // token's "sub" claim into the CaseRequestContext values every grain call already relies on.
    // Nothing calls this yet - purely additive until sub-unit 3 wires it in.
    public interface ITenantResolver
    {
        // issuer is accepted now but unused by today's subject-only lookup - reserved for #74's
        // per-tenant SSO federation, where disambiguating a subject may eventually need the
        // issuing IdP too (a subject string alone is only unique within one IdP).
        Task<ResolvedIdentity> ResolveAsync(
            string subject, string issuer = null, CancellationToken cancellationToken = default);
    }
}
