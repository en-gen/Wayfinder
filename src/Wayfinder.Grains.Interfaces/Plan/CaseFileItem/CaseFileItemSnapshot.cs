using System;
using System.Text.Json.Nodes;
using Orleans;

namespace Wayfinder.Grains.Interfaces.Plan.CaseFileItem
{
    [GenerateSerializer]
    public class CaseFileItemSnapshot
    {
        [Id(0)]
        public Model.CaseFileItem Definition { get; set; }

        [Id(1)]
        public CaseFileItemState CaseFileItemState { get; set; }

        [Id(2)]
        public JsonNode Value { get; set; }

        // ADO #58 - snapshot enrichment so a list view can show "v7, changed 2h ago" without a
        // separate GetHistory round trip. Mirrors BaseStore.Updated (mapped by SnapshotMapper.
        // ToSnapshot(CaseFileItemStore)) - unlike CurrentVersion below, this one IS set by the
        // pure store-level mapper, since Updated already lives on the store.
        [Id(3)]
        public DateTime? UpdatedUtc { get; set; }

        // The item's current journal sequence number (this grain's own JournaledGrain.Version) -
        // NOT set by SnapshotMapper.ToSnapshot(CaseFileItemStore) (a JournaledGrain concept the
        // pure store-level mapper has no access to); CaseFileItemGrain.GetSnapshot sets this
        // itself after mapping. Matches CaseFileItemVersionDescriptor.Version's numbering exactly
        // - feeding this straight into GetValueAt returns the item's current live Value.
        [Id(4)]
        public int CurrentVersion { get; set; }
    }
}
