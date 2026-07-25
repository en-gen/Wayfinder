using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;

namespace Wayfinder.Grains.Interfaces.Plan.CaseFileItem
{
    // 8.3.1 - CaseFileItem operations
    // ~~~~~
    // getCaseFileItemInstance* et al. are navigation/query operations over an already-instantiated
    // CaseFile (e.g. child/parent/target/source lookup by name) intended for Expression/binding
    // consumers. They are not part of this grain surface: this work item's scope is the runtime
    // lifecycle (8.3's transitions and the CaseFileItemTransitionedEvent that unlocks case-file
    // sentries/timer start-triggers), not the caseFileModel-driven navigation layer. See work item
    // #16's report for what remains for later work.
    public interface ICaseFileItemGrain : ICmmnElementGrain<Model.CaseFileItem>
    {
        // 8.3 - CaseFileItem Lifecycle, Table 8.2: create (Ø -> Available).
        Task Create(string caseDefinitionId, Model.CaseFileItem definition, JsonNode value);

        // Table 8.2: update (Available -> Available). Property-level update, per the spec
        // distinction from replace; this engine does not distinguish partial vs. full content
        // updates at the storage layer (both simply set Value), only in which standardEvent is
        // published - see CaseFileItemGrain for the citation on why.
        Task Update(JsonNode value);

        // Table 8.2: replace (Available -> Available). Content replacement.
        Task Replace(JsonNode value);

        // Table 8.2: add child / remove child (Available -> Available). Containment hierarchy
        // (5.3.2's children/parent association) - tracked here only as transition triggers with
        // a referenced child CaseFileItem id; the containment graph itself is out of scope (see
        // class remarks above).
        Task AddChild(string childCaseFileItemId);
        Task RemoveChild(string childCaseFileItemId);

        // Table 8.2: add reference / remove reference (Available -> Available). Reference
        // hierarchy (5.3.2's targetRefs/sourceRef association) - same scoping note as above.
        Task AddReference(string targetCaseFileItemId);
        Task RemoveReference(string targetCaseFileItemId);

        // Table 8.2: delete (Available -> Discarded). Terminal.
        Task Delete();

        Task<CaseFileItemSnapshot> GetSnapshot();

        // ADO #58 - case-file item version history: the curated surface over this grain's
        // journal, built on top of Orleans's own JournaledGrain.RetrieveConfirmedEvents (see
        // ICmmnElementGrain.GetJournaledEvents's remarks - this is the "richer, curated
        // equivalent" that seam's own remarks anticipated). One descriptor per journaled event
        // that carries a Value change - see CaseFileItemVersionDescriptor's remarks for exactly
        // which events those are and why the others are excluded.
        Task<IReadOnlyList<CaseFileItemVersionDescriptor>> GetHistory();

        // As-of read: the item's Value as it stood after journal event `version` (1-based,
        // matching CaseFileItemVersionDescriptor.Version and RetrieveConfirmedEvents' own
        // indexing). Valid for any version 1..the grain's current Version, not only the ones
        // GetHistory reports - a version between two value-carrying events simply returns
        // whatever value was already in effect. Throws ArgumentOutOfRangeException for a version
        // outside that range.
        Task<JsonNode> GetValueAt(int version);
    }
}
