using System;
using System.Linq;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Model.Interchange;

namespace Flow.Grains.Interchange
{
    // ADO #20 - the deploy seam.
    // ~~~~~
    // Adapts an imported Definitions graph into the same Case object
    // ICaseDefinitionGrain.Define(Case) already accepts from hand-built models today (see e.g.
    // CasePlanModelInstantiationIntegrationTests / CaseFileItemSentryIntegrationTests) - the one
    // seam this work item's brief asks for, deliberately minimal: no grain calls happen here,
    // callers still call Define(...) (and ICaseGrain.Create()) themselves, exactly as every
    // existing test does.
    public static class CaseInterchangeExtensions
    {
        // Selects the deployable Case from a parsed Definitions graph and bridges the one gap
        // between "structurally valid per the CMMN XSD" and "what CaseGrain.Create() needs at
        // runtime": tCase.caseRoles is optional (spec §5.2's tCase content model - caseRoles is a
        // minOccurs="0" element), but CaseGrain.Create iterates @case.CaseRoles.Roles
        // unconditionally (Plan/Case/CaseGrain.cs: "await Task.WhenAll(@case.CaseRoles.Roles...")
        // - a spec-legal Case that omits &lt;caseRoles&gt; entirely would NRE there before this
        // seam existed. Nothing else about the Case is touched or validated here: in particular,
        // IsCasePlanModel is deliberately NOT set on the returned Case's CasePlanModel -
        // CaseGrain.Create already does that itself (@case.CasePlanModel.IsCasePlanModel = true)
        // unconditionally, regardless of where the Case came from, so duplicating it here would
        // be redundant, not defensive.
        public static Case ToDeployableCase(this Definitions definitions, string caseId = null)
        {
            if (definitions is null) throw new ArgumentNullException(nameof(definitions));

            var @case = caseId == null
                ? definitions.Cases.SingleOrDefault()
                : definitions.Cases.SingleOrDefault(c => c.Id == caseId);

            if (@case == null)
            {
                throw new InvalidOperationException(caseId == null
                    ? $"definitions declares {definitions.Cases.Count} <case> element(s); pass caseId to disambiguate"
                    : $"no <case id=\"{caseId}\"> found in definitions");
            }

            @case.CaseRoles ??= new CaseRoles();

            return @case;
        }
    }
}
