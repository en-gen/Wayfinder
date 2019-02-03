using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Flow.Grains.Interfaces.Model
{
    public partial class PlanningTable
    {
        [JsonIgnore]
        [IgnoreDataMember]
        public IEnumerable<DiscretionaryItem> DiscretionaryItems => GetDiscretionaryItems(this);
        
        private static IEnumerable<DiscretionaryItem> GetDiscretionaryItems(PlanningTable table)
        {
            foreach (var tableItem in table.TableItems)
            {
                switch (tableItem)
                {
                    case DiscretionaryItem discretionaryItem:
                    {
                        yield return discretionaryItem;
                        break;
                    }
                    case PlanningTable planningTable:
                    {
                        foreach (var subItem in GetDiscretionaryItems(planningTable))
                        {
                            yield return subItem;
                        }
                        break;
                    }
                }
            }
        }
    }
}
