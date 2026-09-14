using System;
using System.Collections.Generic;
using System.Linq;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Plan
{
    // The case model, resolved ONCE at CaseGrain.Create and pinned for the life of the case
    // instance (design 05 section A.5).
    //
    // Threaded down the plan-item tree (StageBehavior.CreateChild -> DefineRepetition -> Defined)
    // rather than fetched, because an element calling back into the case grain would be exactly the
    // D3 wait cycle this phase is removing. When phase P2 collapses plan items into the case grain
    // this whole carrier collapses with them into one field on the case.
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

        // The pin as handed to an element that can never create children. Identical today; phase
        // P1's definition-pinning half gives it something to trim.
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
