using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Flow.Grains.Interfaces.Model
{
    public partial class Stage
    {
        public bool IsCasePlanModel { get; set; }

        // [IgnoreDataMember] alone is not sufficient for System.Text.Json (it only honors its own
        // [JsonIgnore]). This is a recursive, self-including projection (GetAllPlanItemDefinitions
        // yields `stage` itself) - without [JsonIgnore], the fallback JSON serializer throws
        // "possible object cycle detected" attempting to walk it as data.
        [JsonIgnore]
        [IgnoreDataMember]
        public IEnumerable<PlanItemDefinition> PlanItemDefinitionsNested => GetAllPlanItemDefinitions(this);
        
        private static IEnumerable<PlanItemDefinition> GetAllPlanItemDefinitions(Stage stage)
        {
            yield return stage;
            foreach (var def in stage.PlanItemDefinitions)
            {
                if (def is Stage childStage)
                {
                    foreach (var cdef in GetAllPlanItemDefinitions(childStage))
                    {
                        yield return cdef;
                    }
                }

                yield return def;
            }
        }
    }
}
