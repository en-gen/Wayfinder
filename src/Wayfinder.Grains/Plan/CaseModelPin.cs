using System;
using System.Collections.Generic;
using System.Linq;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Plan
{
    // Design 05 §A.5 - the case model, resolved ONCE at CaseGrain.Create and pinned for the life of
    // the case instance.
    //
    // Before this, PlanItemGrain.DefineRepetition called CaseDefinitionGrain.GetPlanItemDefinition
    // on every define - including every repetition spawn - so the definition a live case ran
    // against was whatever the definition grains happened to hold at that moment rather than what
    // the case was created with. Resolving once and journaling the result makes the pin explicit:
    // nothing in the running case reaches back to ICaseDefinitionGrain, so a case's model cannot
    // change under it, and an entire class of outbound call leaves the turn (§D.6).
    //
    // Threaded down the plan-item tree (StageBehavior.CreateChild -> DefineRepetition -> Defined)
    // rather than fetched, because a plan item calling back into the case grain would be exactly
    // the D3 wait cycle this phase is removing. PlanItemDefinitions is carried only by elements
    // that can create children (Stages); everything else gets the trimmed pin from ForLeaf(), which
    // keeps the journal cost of the pin proportional to the model rather than to the instance count.
    // When phase P2 collapses plan items into the case grain this whole carrier collapses with them
    // into one field on the case.
    [GenerateSerializer]
    public sealed class CaseModelPin
    {
        // Definition ids of every caseFileItem declared by the case's caseFileModel (5.3.1),
        // including nested children. Bound as the evaluation context when an expression's
        // contextRef is absent - Table 5.32 / Table 5.54, finding I4. Declared ids only: which of
        // them actually EXIST for a given case instance is a runtime question, answered by reading
        // each one (see ExpressionContext.BindCaseFileModel), because case-file items are created
        // ad hoc (#16) rather than instantiated from the model.
        [Id(0)]
        public string[] CaseFileItemIds { get; set; } = Array.Empty<string>();

        // Every registered PlanItemDefinition, keyed by the definition-scope address
        // CaseDefinitionGrain.DefinitionIndex uses (DefinitionGraphNode.Address). Null on a pin
        // trimmed by ForLeaf().
        [Id(1)]
        public Dictionary<string, PlanItemDefinition> PlanItemDefinitions { get; set; }

        // The same upward-walking lookup CaseDefinitionGrain.GetPlanItemDefinition performs, run
        // against the pin instead of against the definition grains (#65: DefinitionIndex is keyed
        // on DEFINITION-id paths, so the search scope must be a definition path, never a runtime
        // instance address). Returns null when the definitionRef resolves to nothing, which the
        // caller reports exactly as before.
        public PlanItemDefinition Resolve(string definitionScope, string definitionId)
        {
            if (PlanItemDefinitions == null || string.IsNullOrEmpty(definitionScope)) return null;

            var chunks = definitionScope.Split('.').ToList();

            while (chunks.Any())
            {
                var address = $"{string.Join('.', chunks)}.{definitionId}";
                if (PlanItemDefinitions.TryGetValue(address, out var definition))
                {
                    return definition;
                }

                // remove last chunk to search upwards in hierarchy
                chunks.RemoveAt(chunks.Count - 1);
            }

            return null;
        }

        // The pin as handed to an element that can never create children: the case-file item ids
        // it needs for expression binding, without the definition map it would only journal and
        // never read.
        public CaseModelPin ForLeaf() => new CaseModelPin { CaseFileItemIds = CaseFileItemIds };

        // Collects every caseFileItem definition id declared by a caseFileModel, including nested
        // children (5.3.2's containment hierarchy). Flat, because CaseFileItem instances are
        // addressed flat by definition id - see CaseFileItemAddress.For.
        public static string[] CaseFileItemIdsOf(CaseFile caseFileModel)
        {
            var ids = new List<string>();

            void Walk(IEnumerable<Interfaces.Model.CaseFileItem> items)
            {
                if (items == null) return;

                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.Id)) ids.Add(item.Id);
                    Walk(item.Children?.CaseFileItem);
                }
            }

            Walk(caseFileModel?.CaseFileItem);

            return ids.Distinct().ToArray();
        }
    }
}
