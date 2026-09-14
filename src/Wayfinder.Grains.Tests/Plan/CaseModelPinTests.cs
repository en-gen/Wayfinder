using System.Linq;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;
using FluentAssertions;
using Xunit;
using ModelCaseFileItem = Wayfinder.Grains.Interfaces.Model.CaseFileItem;

namespace Wayfinder.Grains.Tests.Plan
{
    // Unit tests for CaseModelPin (design 05 section A.5 - definition pinning).
    // ~~~~~
    // The pin is what lets the whole definition graph be resolved ONCE at Create and threaded down,
    // so nothing in a running case calls ICaseDefinitionGrain.GetPlanItemDefinition again - which
    // closes the hazard that a redeployed definition could change a live case's model, and removes
    // an outbound call from every child instantiation.
    //
    // Resolve's scope walk is the subtle part and the reason these are unit tests rather than being
    // left to integration coverage: it retries the lookup while popping one segment off the
    // definition scope at a time, which is how a nested Stage finds a definition declared in an
    // ancestor scope. That loop has a hit-at-depth case, a hit-after-popping case and a
    // walked-to-exhaustion case, and they are near-impossible to aim at through a whole case
    // instance but trivial to state directly.
    public class CaseModelPinTests
    {
        private static CaseModelPin PinWith(params string[] addresses)
        {
            var pin = new CaseModelPin
            {
                PlanItemDefinitions = new System.Collections.Generic.Dictionary<string, PlanItemDefinition>()
            };

            foreach (var address in addresses)
            {
                pin.PlanItemDefinitions[address] = new PlanItemDefinition { Id = address };
            }

            return pin;
        }

        // ---------------------------------------------------------------- Resolve

        [Fact]
        public void Resolve__Given_ExactScopeMatch__ReturnsTheDefinition()
        {
            var pin = PinWith("Case1.StageA.TaskX");

            pin.Resolve("Case1.StageA", "TaskX").Id.Should().Be("Case1.StageA.TaskX");
        }

        // The walk: nothing at Case1.StageA.StageB.TaskX, so pop to Case1.StageA.TaskX and hit.
        // This is how a nested Stage inherits a definition declared further up.
        [Fact]
        public void Resolve__Given_DefinitionInAnAncestorScope__WalksUpAndFindsIt()
        {
            var pin = PinWith("Case1.TaskX");

            pin.Resolve("Case1.StageA.StageB", "TaskX").Id.Should().Be("Case1.TaskX");
        }

        // The nearest scope must win, or a nested redefinition would be shadowed by its ancestor.
        [Fact]
        public void Resolve__Given_DefinitionAtSeveralDepths__PrefersTheNearestScope()
        {
            var pin = PinWith("Case1.TaskX", "Case1.StageA.TaskX");

            pin.Resolve("Case1.StageA", "TaskX").Id.Should().Be("Case1.StageA.TaskX");
        }

        [Fact]
        public void Resolve__Given_NoMatchAnywhere__WalksToExhaustionAndReturnsNull()
        {
            var pin = PinWith("Case1.StageA.TaskX");

            pin.Resolve("Case1.StageA", "Absent").Should().BeNull();
        }

        [Fact]
        public void Resolve__Given_NoDefinitionsPinned__ReturnsNull()
        {
            new CaseModelPin().Resolve("Case1.StageA", "TaskX").Should().BeNull();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Resolve__Given_NoScope__ReturnsNull(string scope)
        {
            PinWith("Case1.TaskX").Resolve(scope, "TaskX").Should().BeNull();
        }

        // ---------------------------------------------------------------- ForLeaf

        // Leaf elements (sentries, planning tables, non-Stage plan items) never instantiate
        // children, so they carry only the case-file item ids and not the definition map. That is
        // the mitigation for section A.5's journal cost on the current topology: the pin is threaded
        // per stage instance, so handing every leaf a full copy of the model would multiply it.
        [Fact]
        public void ForLeaf__DropsTheDefinitionMapButKeepsCaseFileItemIds()
        {
            var pin = PinWith("Case1.TaskX");
            pin.CaseFileItemIds = new[] { "ItemA", "ItemB" };

            var leaf = pin.ForLeaf();

            leaf.PlanItemDefinitions.Should().BeNull();
            leaf.CaseFileItemIds.Should().Equal("ItemA", "ItemB");
        }

        // ---------------------------------------------------------------- CaseFileItemIdsOf

        [Fact]
        public void CaseFileItemIdsOf__Given_NullModel__ReturnsEmpty()
        {
            CaseModelPin.CaseFileItemIdsOf(null).Should().BeEmpty();
        }

        [Fact]
        public void CaseFileItemIdsOf__Given_NestedChildren__WalksTheWholeTree()
        {
            var model = new CaseFile
            {
                CaseFileItem =
                {
                    new ModelCaseFileItem
                    {
                        Id = "Parent",
                        Children = new Children
                        {
                            CaseFileItem = { new ModelCaseFileItem { Id = "Child" } }
                        }
                    }
                }
            };

            CaseModelPin.CaseFileItemIdsOf(model).Should().BeEquivalentTo(new[] { "Parent", "Child" });
        }

        // Items with no id contribute nothing rather than an empty-string entry that would later
        // bind an unnamed property into the caseFileModel object (I4).
        [Fact]
        public void CaseFileItemIdsOf__Given_ItemsWithoutIds__SkipsThem()
        {
            var model = new CaseFile
            {
                CaseFileItem =
                {
                    new ModelCaseFileItem { Id = "Real" },
                    new ModelCaseFileItem { Id = null },
                    new ModelCaseFileItem { Id = "" }
                }
            };

            CaseModelPin.CaseFileItemIdsOf(model).Should().Equal("Real");
        }

        [Fact]
        public void CaseFileItemIdsOf__Given_DuplicateIds__ReturnsThemOnce()
        {
            var model = new CaseFile
            {
                CaseFileItem =
                {
                    new ModelCaseFileItem { Id = "Dup" },
                    new ModelCaseFileItem { Id = "Dup" }
                }
            };

            CaseModelPin.CaseFileItemIdsOf(model).Should().ContainSingle().Which.Should().Be("Dup");
        }
    }
}
