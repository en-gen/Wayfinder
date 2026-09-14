using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;
using Wayfinder.Grains.Plan.CmmnElement;

namespace Wayfinder.Grains.Plan.Sentry
{
    public interface ISentryGrain : ICmmnElementGrain<Interfaces.Model.Sentry>
    {
        // pin: design 05 section A.5. Carries the declared caseFileModel item ids this
        // element needs to bind a contextRef-less expression (finding I4). Always the
        // trimmed CaseModelPin.ForLeaf() form - this element can never create plan items,
        // so it has no use for the definition map.
        Task Define(string caseDefinitionId, Interfaces.Model.Sentry definition, CaseModelPin pin);
    }
}
