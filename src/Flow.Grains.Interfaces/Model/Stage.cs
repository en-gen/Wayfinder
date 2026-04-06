using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Flow.Grains.Interfaces.Model
{
    public partial class Stage
    {
        public bool IsCasePlanModel { get; set; }

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
