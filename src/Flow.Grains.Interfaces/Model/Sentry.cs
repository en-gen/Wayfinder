using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Flow.Grains.Interfaces.Model
{
    public partial class Sentry
    {
        [IgnoreDataMember]
        public IEnumerable<PlanItemOnPart> PlanItemOnParts => OnParts.OfType<PlanItemOnPart>();

        [IgnoreDataMember]
        public IEnumerable<CaseFileItemOnPart> CaseFileItemOnParts => OnParts.OfType<CaseFileItemOnPart>();
    }
}
