using Wayfinder.Contracts.V1;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace Wayfinder.Api.Infrastructure
{
    // ADO #32/#33 - the OData EDM model backing GET /api/v1/cases({id})'s [EnableQuery]
    // ($select/$expand) support and the "trigger" bound action's self-description. CaseView has no
    // "Id"/"CaseViewId" property for ODataConventionModelBuilder's default key-detection convention
    // to find (it is "CaseId" - see CaseView's remarks on why it stays a flat V1 DTO), so the key is
    // declared explicitly rather than relying on that guess. The "cases" entity-set name is
    // deliberately lower-case to match the REST surface's existing casing (POST /api/v1/cases) - one
    // consistent URL vocabulary across both the plain and OData-flavored actions on CasesController.
    public static class CasesEdmModel
    {
        public static IEdmModel Build()
        {
            var builder = new ODataConventionModelBuilder();

            var cases = builder.EntitySet<CaseView>("cases");
            cases.EntityType.HasKey(c => c.CaseId);

            // Declared for EDM self-description ($metadata) even though CasesController dispatches
            // POST /api/v1/cases({id})/trigger via plain attribute routing rather than OData's own
            // bound-action invoker - see that controller's remarks. CaseView is already registered
            // above as the "cases" entity set's element type, so the action's return type must be
            // wired via ReturnsFromEntitySet (not the plain Returns<T>() used for a complex/
            // non-entity return type) - ModelBuilder's own guard throws
            // "already declared as an entity type" otherwise (caught by booting the host locally
            // against Azurite during verification).
            var trigger = cases.EntityType.Action("trigger");
            trigger.ReturnsFromEntitySet<CaseView>("cases");

            return builder.GetEdmModel();
        }
    }
}
