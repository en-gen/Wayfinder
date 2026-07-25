using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Flow.Grains.Interfaces.Model
{
    public partial class Sentry
    {
        // [IgnoreDataMember] alone is not sufficient for System.Text.Json (it only honors its own
        // [JsonIgnore]). These are derived filters over OnParts, not independent data - without
        // [JsonIgnore] the fallback JSON serializer would duplicate OnParts entries into the payload.
        [JsonIgnore]
        [IgnoreDataMember]
        public IEnumerable<PlanItemOnPart> PlanItemOnParts => OnParts.OfType<PlanItemOnPart>();

        [JsonIgnore]
        [IgnoreDataMember]
        public IEnumerable<CaseFileItemOnPart> CaseFileItemOnParts => OnParts.OfType<CaseFileItemOnPart>();
    }
}
