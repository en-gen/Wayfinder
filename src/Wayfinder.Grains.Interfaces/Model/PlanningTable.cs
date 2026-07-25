using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Wayfinder.Grains.Interfaces.Model
{
    public partial class PlanningTable
    {
        // [IgnoreDataMember] alone is not sufficient for System.Text.Json (it only honors its own
        // [JsonIgnore]); DiscretionaryItems is a recursive get-only projection over TableItems, so
        // without this the fallback JSON serializer would attempt to walk it as data.
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
