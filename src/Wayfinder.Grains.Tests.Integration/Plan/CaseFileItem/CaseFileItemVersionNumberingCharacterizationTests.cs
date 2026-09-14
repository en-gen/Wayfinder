using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseFileItemModel = Wayfinder.Grains.Interfaces.Model.CaseFileItem;
using CaseFileItemTransition = Wayfinder.Grains.Interfaces.Model.CaseFileItemTransition;

namespace Wayfinder.Grains.Tests.Integration.Plan.CaseFileItem
{
    // Issue #247 (P0 - characterization), docs/05-case-grain-redesign.md §D.4.3.
    // ~~~~~
    // GetHistory/GetValueAt are HTTP-exposed (/case-file-items({itemId})/history,
    // /versions({itemVersion})) and are defined today over JournaledGrain.RetrieveConfirmedEvents
    // on the item's OWN journal: CaseFileItemVersionDescriptor.Version documents itself as "the
    // Nth event RetrieveConfirmedEvents(0, Version) would return". The case-grain redesign
    // merges every element's journal into one case journal and replaces that raw position with a
    // PER-ITEM LOGICAL COUNTER incremented on each of the item's own Table 8.2 transitions. The
    // two numbering schemes must agree exactly, or a live HTTP contract breaks silently - §D.4.3
    // calls this "the one genuinely hard compatibility problem in this redesign".
    //
    // §D.4.3 flags one specific unknown as "not obvious from reading the code": whether
    // CmmnElementDefined occupies version 1. These tests answer it, and pin the exact number each
    // of the eight Table 8.2 operations lands on INCLUDING the gaps the non-value-carrying
    // events (ChildAdded/ChildRemoved/ReferenceAdded/ReferenceRemoved/Discarded) leave behind.
    //
    // Deliberately EXACT integers throughout, not BeGreaterThan. CaseFileItemGrainTests already
    // covers the same surface with relational assertions ("history[1].Version should be greater
    // than history[0].Version"), which is the right shape for "history is ordered" and no use at
    // all as a compatibility baseline: a per-item counter that started at 1 and skipped nothing
    // would satisfy every one of those and still break every stored /versions(N) URL.
    //
    // These are characterization tests. They record what the engine does TODAY; they are not an
    // argument that today's numbering is the right design. If the redesign's logical counter
    // cannot be made to reproduce these numbers, §D.4.3's own answer is a V2 route, not a quiet
    // change - and these tests are what makes that decision visible rather than accidental.
    [Collection(ClusterCollection.Name)]
    public class CaseFileItemVersionNumberingCharacterizationTests
    {
        private readonly IClusterClient _clusterClient;

        public CaseFileItemVersionNumberingCharacterizationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // §D.4.3's explicitly-flagged unknown, answered directly: YES - CmmnElementDefined
        // occupies version 1.
        //
        // Create() raises TWO journal events: CmmnElementGrain.Define's CmmnElementDefined, then
        // CaseFileItemGrain.Create's own ValueChanged. The item's very first VALUE therefore sits
        // at journal position 2, and position 1 is occupied by an event that carries no value at
        // all - which is why GetValueAt(1) returns null rather than the created value, and why a
        // per-item logical counter that starts the create transition at 1 would be off by one
        // against every URL already issued.
        [Theory, AutoData]
        public async Task Versioning__Given_FreshlyCreatedItem__Then_CmmnElementDefinedOccupiesVersion1AndCreateLandsOnVersion2
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, new CaseFileItemModel { Id = caseFileItemId }, JsonValue.Create("v1"));

            var snapshot = await subject.GetSnapshot();
            snapshot.CurrentVersion.Should().Be(2,
                "Create() raises CmmnElementDefined and then ValueChanged, so a just-created item's " +
                "journal already holds two confirmed events (§D.4.3)");

            var history = await subject.GetHistory();
            history.Should().ContainSingle();
            history[0].Version.Should().Be(2,
                "the create transition's own ValueChanged is the SECOND journaled event - " +
                "CmmnElementDefined occupies version 1");
            history[0].Transition.Should().Be(CaseFileItemTransition.Create);

            // The direct probe of the §D.4.3 question. Version 1 is a legal argument (1..Version)
            // and it replays exactly one event - CmmnElementDefined - which is not a ValueChanged,
            // so the fold produces no value at all.
            var valueAtVersion1 = await subject.GetValueAt(1);
            valueAtVersion1.Should().BeNull(
                "version 1 is CmmnElementDefined, which carries no Value - if the item's own create " +
                "transition occupied version 1 this would be \"v1\"");

            (await subject.GetValueAt(2))!.ToJsonString().Should().Be(JsonValue.Create("v1").ToJsonString(),
                "version 2 is the create transition's ValueChanged");
        }

        // The full Table 8.2 walk in one journal, with the exact version each operation lands on
        // and the exact gaps the non-value-carrying ones leave. This is the number sequence the
        // redesign's per-item logical counter has to reproduce.
        //
        //   version 1 = CmmnElementDefined   (not a Table 8.2 transition at all; no descriptor)
        //   version 2 = create               -> descriptor
        //   version 3 = update               -> descriptor
        //   version 4 = replace              -> descriptor
        //   version 5 = addChild             -> NO descriptor, but consumes the number
        //   version 6 = addReference         -> NO descriptor, but consumes the number
        //   version 7 = delete               -> NO descriptor, but consumes the number
        //
        // Note what that means for the redesign: the operation counter and the journal position
        // differ by exactly one (the CmmnElementDefined offset) ONLY as long as every raised
        // event is one of the item's own Table 8.2 transitions. Nothing today enforces that
        // invariant - it holds by construction because CaseFileItemGrain raises nothing else.
        [Theory, AutoData]
        public async Task Versioning__Given_EveryTable82Operation__Then_VersionsAreContiguousFrom2WithGapsForNonValueCarryingEvents
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string childId, string targetId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, new CaseFileItemModel { Id = caseFileItemId }, JsonValue.Create("v1"));
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(2, "create is the 2nd journaled event");

            await subject.Update(JsonValue.Create("v2"));
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(3, "update is the 3rd");

            await subject.Replace(JsonValue.Create("v3"));
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(4, "replace is the 4th");

            await subject.AddChild(childId);
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(5,
                "addChild carries no Value but still consumes a journal sequence number (§D.4.3)");

            await subject.AddReference(targetId);
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(6,
                "addReference carries no Value but still consumes a journal sequence number (§D.4.3)");

            await subject.Delete();
            (await subject.GetSnapshot()).CurrentVersion.Should().Be(7,
                "delete carries no Value but still consumes a journal sequence number (§D.4.3)");

            var history = await subject.GetHistory();

            history.Select(x => x.Version).Should().Equal(new[] { 2, 3, 4 },
                "only the three value-carrying transitions produce descriptors, and their versions " +
                "are RAW JOURNAL POSITIONS - not a 1,2,3 counter over value changes");
            history.Select(x => x.Transition).Should().Equal(new CaseFileItemTransition?[]
            {
                CaseFileItemTransition.Create,
                CaseFileItemTransition.Update,
                CaseFileItemTransition.Replace
            });
        }

        // The gap versions are legal GetValueAt arguments and fold to whatever value was already
        // in effect - so /versions(5) and /versions(7) on a real deployment both return the
        // replace value, not 404 and not null. Pinned because a logical counter that simply did
        // not allocate numbers for addChild/addReference/delete would change the ANSWER at those
        // URLs, not merely their availability.
        [Theory, AutoData]
        public async Task Versioning__Given_NonValueCarryingGapVersions__Then_TheyFoldToTheLastValueInEffect
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string childId, string targetId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, new CaseFileItemModel { Id = caseFileItemId }, JsonValue.Create("v1"));
            await subject.Update(JsonValue.Create("v2"));
            await subject.Replace(JsonValue.Create("v3"));
            await subject.AddChild(childId);
            await subject.AddReference(targetId);
            await subject.Delete();

            (await subject.GetValueAt(1)).Should().BeNull("version 1 is CmmnElementDefined");
            (await subject.GetValueAt(2))!.ToJsonString().Should().Be(JsonValue.Create("v1").ToJsonString());
            (await subject.GetValueAt(3))!.ToJsonString().Should().Be(JsonValue.Create("v2").ToJsonString());
            (await subject.GetValueAt(4))!.ToJsonString().Should().Be(JsonValue.Create("v3").ToJsonString());
            (await subject.GetValueAt(5))!.ToJsonString().Should().Be(JsonValue.Create("v3").ToJsonString(),
                "addChild's own version folds to the value already in effect");
            (await subject.GetValueAt(6))!.ToJsonString().Should().Be(JsonValue.Create("v3").ToJsonString(),
                "addReference's own version folds to the value already in effect");
            (await subject.GetValueAt(7))!.ToJsonString().Should().Be(JsonValue.Create("v3").ToJsonString(),
                "delete does NOT clear the as-of value - a Discarded item's history stays readable");

            // The valid range is exactly 1..CurrentVersion: one past the end is out of range, and
            // 0 is too. Pinned because the redesign's counter must agree on the UPPER BOUND as
            // well as on the individual numbers.
            await subject.Awaiting(x => x.GetValueAt(0)).Should().ThrowAsync<ArgumentOutOfRangeException>();
            await subject.Awaiting(x => x.GetValueAt(8)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        // RemoveChild/RemoveReference complete the Table 8.2 set. Same rule, pinned separately so
        // the numbering above is not accidentally read as "adds consume numbers, removes do not".
        [Theory, AutoData]
        public async Task Versioning__Given_RemoveChildAndRemoveReference__Then_TheyAlsoConsumeVersionsWithoutProducingDescriptors
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string childId, string targetId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, new CaseFileItemModel { Id = caseFileItemId }, JsonValue.Create("v1"));
            await subject.AddChild(childId);
            await subject.RemoveChild(childId);
            await subject.AddReference(targetId);
            await subject.RemoveReference(targetId);

            (await subject.GetSnapshot()).CurrentVersion.Should().Be(6,
                "CmmnElementDefined + create + addChild + removeChild + addReference + removeReference");

            var history = await subject.GetHistory();
            history.Select(x => x.Version).Should().Equal(new[] { 2 },
                "only the create transition carried a Value - the four containment/reference " +
                "operations consumed versions 3..6 and produced no descriptor at all");
        }
    }
}
