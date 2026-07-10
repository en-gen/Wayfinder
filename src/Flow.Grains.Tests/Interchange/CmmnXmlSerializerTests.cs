using System.Linq;
using System.Reflection;
using Flow.Grains.Interchange;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Model.Interchange;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Xunit;

namespace Flow.Grains.Tests.Interchange
{
    // ADO #20 - CmmnXmlSerializer: malformed-XML error surfacing, and the round-trip fidelity
    // acceptance bar (import(export(import(x))) structurally equals import(x)).
    public class CmmnXmlSerializerTests
    {
        [Fact]
        public void Import__Given_NotWellFormedXml__Then_ReturnsFailureNotException()
        {
            const string xml = "<definitions xmlns=\"http://www.omg.org/spec/CMMN/20151109/MODEL\"><case id=\"A\">";

            var result = CmmnXmlSerializer.Import(xml);

            result.IsError.Should().BeTrue();
            result.Message.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public void Import__Given_MissingNamespace__Then_ReturnsFailureCitingExpectedNamespace()
        {
            const string xml = "<definitions id=\"A\"><case id=\"B\"/></definitions>";

            var result = CmmnXmlSerializer.Import(xml);

            result.IsError.Should().BeTrue();
            result.Message.Should().Contain(CmmnXml.Namespace);
        }

        [Fact]
        public void Import__Given_WrongRootElement__Then_ReturnsFailure()
        {
            const string xml = "<notDefinitions xmlns=\"http://www.omg.org/spec/CMMN/20151109/MODEL\"/>";

            var result = CmmnXmlSerializer.Import(xml);

            result.IsError.Should().BeTrue();
        }

        [Fact]
        public void Import__Given_EmptyString__Then_ReturnsFailure()
        {
            var result = CmmnXmlSerializer.Import(string.Empty);

            result.IsError.Should().BeTrue();
        }

        // One import-side assertion per XML-mapped collection in the model layer - the "did
        // import actually populate it" proof for every shadow in CmmnXmlCollectionShadows.cs
        // (the round-trip tests below then prove export x re-import over the same instance).
        [Fact]
        public void Import__Given_RichSample__Then_ParsesExpectedShape()
        {
            var xml = ReadEmbeddedResource("RichSample.cmmn");

            var result = CmmnXmlSerializer.Import(xml);

            result.IsError.Should().BeFalse(result.Message);
            var definitions = result.Value;

            definitions.TargetNamespace.Should().Be("http://case.flow/samples/rich");
            definitions.ExpressionLanguage.Should().Be(ExpressionLanguage.Jint);
            definitions.CaseFileItemDefinitions.Should().ContainSingle(d => d.Id == "CFID_1");
            definitions.CaseFileItemDefinitions.Single().Property.Should().ContainSingle(p => p.Name == "amount");

            // tDefinitions-level collections (per CMMN11.xsd's own sequence).
            definitions.AnyAttributes.Should().ContainSingle(a => a.LocalName == "origin" && a.Value == "test-suite");
            definitions.ExtensionElements.Any.Should().ContainSingle(e => e.LocalName == "settings");
            var relationship = definitions.Relationships.Should().ContainSingle().Subject;
            relationship.Source.Should().ContainSingle(q => q.Name == "RichCase" && q.Namespace == "http://case.flow/samples/rich");
            relationship.Target.Should().ContainSingle(q => q.Name == "Process_1" && q.Namespace == "http://case.flow/samples/rich");
            definitions.Artifacts.Should().ContainSingle().Which.Should().BeOfType<TextAnnotation>()
                .Which.Text.Should().Be("Annotation kept at the definitions level.");

            var process = definitions.Processes.Should().ContainSingle().Subject;
            process.Input.Should().ContainSingle(p => p.Id == "PIn_1");
            process.Output.Should().ContainSingle(p => p.Id == "POut_1");

            var decision = definitions.Decisions.Should().ContainSingle().Subject;
            decision.Input.Should().ContainSingle(p => p.Id == "DIn_1");
            decision.Output.Should().ContainSingle(p => p.Id == "DOut_1");

            var @case = definitions.Cases.Should().ContainSingle().Subject;
            @case.Id.Should().Be("RichCase");
            @case.Documentation.Should().ContainSingle().Which.Text.Should().Be("The rich round-trip sample case.");
            @case.Inputs.Should().ContainSingle(p => p.Id == "CaseIn_1");

            var cpm = @case.CasePlanModel;
            cpm.Id.Should().Be("RichCPM");
            cpm.AnyAttributes.Should().ContainSingle(a => a.LocalName == "tier" && a.Value == "gold");
            cpm.ExtensionElements.Any.Should().ContainSingle(e => e.LocalName == "audit");
            cpm.ExitCriteria.Should().ContainSingle(c => c.Id == "CPMExit_1");
            cpm.PlanItemDefinitions.Should().HaveCount(7);
            cpm.PlanItems.Should().HaveCount(4);

            var sentry = cpm.Sentries.Should().ContainSingle(s => s.Id == "RichSentry").Subject;
            sentry.OnParts.Should().ContainSingle(o => o.Id == "OnPart_1");
            sentry.IfPart.Condition.AnyAttribute.Should().ContainSingle(a => a.LocalName == "hint" && a.Value == "fast");

            var piHuman = cpm.PlanItems.Single(p => p.Id == "PI_Human");
            piHuman.EntryCriteria.Should().ContainSingle(c => c.Id == "EC_1");
            piHuman.ExitCriteria.Should().ContainSingle(c => c.Id == "PIExit_1");

            var rootItem = @case.CaseFileModel.CaseFileItem.Should().ContainSingle(i => i.Id == "RichItem").Subject;
            rootItem.Children.CaseFileItem.Should().ContainSingle(i => i.Id == "RichChildItem");

            var planningTable = cpm.PlanningTable;
            planningTable.Should().NotBeNull();
            var discretionaryItem = planningTable.TableItems.Should().ContainSingle(t => t.Id == "DiscItem_1").Subject
                .Should().BeOfType<DiscretionaryItem>().Subject;
            discretionaryItem.EntryCriteria.Should().ContainSingle(c => c.Id == "DIEntry_1");
            discretionaryItem.ExitCriteria.Should().ContainSingle(c => c.Id == "DIExit_1");
            planningTable.ApplicabilityRules.Should().ContainSingle(r => r.Id == "AppRule_1");

            var humanTask = cpm.PlanItemDefinitions.OfType<HumanTask>().Should().ContainSingle().Subject;
            humanTask.DefaultControl.Should().NotBeNull();
            humanTask.DefaultControl.RepetitionRule.Should().NotBeNull();
            humanTask.DefaultControl.RequiredRule.Should().NotBeNull();
            humanTask.DefaultControl.ManualActivationRule.Should().NotBeNull();
            humanTask.Inputs.Should().ContainSingle(p => p.Id == "HTIn_1");
            humanTask.Outputs.Should().ContainSingle(p => p.Id == "HTOut_1");

            cpm.PlanItemDefinitions.OfType<ProcessTask>().Should().ContainSingle()
                .Which.ParameterMapping.Should().ContainSingle(m => m.Id == "PM_1");
            cpm.PlanItemDefinitions.OfType<CaseTask>().Should().ContainSingle()
                .Which.ParameterMapping.Should().ContainSingle(m => m.Id == "PM_2");
            cpm.PlanItemDefinitions.OfType<DecisionTask>().Should().ContainSingle()
                .Which.ParameterMapping.Should().ContainSingle(m => m.Id == "PM_3");

            var nestedStage = cpm.PlanItemDefinitions.OfType<Stage>().Should().ContainSingle().Subject;
            nestedStage.Id.Should().Be("RichNestedStage");
            nestedStage.PlanItems.Should().ContainSingle(p => p.Id == "PI_NestedMilestone");
            nestedStage.PlanItemDefinitions.Should().ContainSingle(d => d.Id == "NestedMilestone");
            nestedStage.ExitCriteria.Should().ContainSingle(c => c.Id == "NestedExit_1");

            @case.CaseRoles.Roles.Should().ContainSingle(r => r.Id == "RoleA");
        }

        [Fact]
        public void Import__Given_DocumentationInsideMixedContentExpression__Then_Populates()
        {
            // tExpression is mixed content (tCmmnElementWithMixedContent), so whitespace inside
            // it is significant by XSD design - round-tripping an INDENTED expression body is
            // inherently whitespace-mutating, which is why RichSample.cmmn keeps its condition
            // elements childless. The lossless-IMPORT invariant is what the
            // CmmnElementWithMixedContent.DocumentationXml shadow guarantees, pinned here: the
            // child is authored tight (no intra-element whitespace) and must arrive in
            // Documentation instead of being silently dropped.
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <definitions xmlns="http://www.omg.org/spec/CMMN/20151109/MODEL" id="D1" targetNamespace="http://case.flow/samples/mixed-doc">
                  <case id="C1">
                    <casePlanModel id="CPM1">
                      <sentry id="S1">
                        <ifPart id="IP1" contextRef="X"><condition language="expression://lang/jint" body="true"><documentation id="DocX">why this fires</documentation></condition></ifPart>
                      </sentry>
                    </casePlanModel>
                  </case>
                </definitions>
                """;

            var result = CmmnXmlSerializer.Import(xml);

            result.IsError.Should().BeFalse(result.Message);
            var condition = result.Value.Cases.Single().CasePlanModel.Sentries.Single().IfPart.Condition;
            condition.Documentation.Should().ContainSingle().Which.Text.Should().Be("why this fires");
        }

        [Fact]
        public void Import__Given_StreamOverload__Then_Succeeds()
        {
            var resourceName = typeof(CmmnXmlSerializerTests).Assembly.GetManifestResourceNames()
                .Single(n => n.EndsWith("RichSample.cmmn"));
            using var stream = typeof(CmmnXmlSerializerTests).Assembly.GetManifestResourceStream(resourceName);

            var result = CmmnXmlSerializer.Import(stream);

            result.IsError.Should().BeFalse(result.Message);
        }

        [Fact]
        public void RoundTrip__Given_RichSample__Then_ImportExportImport_IsStructurallyEquivalentToImport()
        {
            var xml = ReadEmbeddedResource("RichSample.cmmn");

            var firstImport = CmmnXmlSerializer.Import(xml);
            firstImport.IsError.Should().BeFalse(firstImport.Message);

            var exported = CmmnXmlSerializer.Export(firstImport.Value);
            exported.IsError.Should().BeFalse(exported.Message);
            exported.Value.Should().Contain(CmmnXml.Namespace);

            var secondImport = CmmnXmlSerializer.Import(exported.Value);
            secondImport.IsError.Should().BeFalse(secondImport.Message);

            secondImport.Value.Should().BeEquivalentTo(firstImport.Value, ExcludingEngineOnlyMembers);
        }

        [Fact]
        public void RoundTrip__Given_MilestoneSentryShape__Then_ImportExportImport_IsStructurallyEquivalentToImport()
        {
            // A second, independently-authored shape (mirrors the flagship) so round-trip
            // coverage is not solely dependent on RichSample.cmmn's specific construct mix.
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <definitions xmlns="http://www.omg.org/spec/CMMN/20151109/MODEL"
                             xmlns:tns="http://case.flow/samples/roundtrip-small"
                             targetNamespace="http://case.flow/samples/roundtrip-small"
                             expressionLanguage="expression://lang/jint"
                             id="Definitions_Small"
                             name="SmallSample">
                  <caseFileItemDefinition id="CFID_1" name="ItemDef"/>
                  <case id="SmallCase" name="SmallCase">
                    <caseFileModel>
                      <caseFileItem id="TheItem" name="TheItem" definitionRef="tns:CFID_1" multiplicity="ExactlyOne"/>
                    </caseFileModel>
                    <casePlanModel id="CPM" autoComplete="true">
                      <planItem id="PI_A" definitionRef="MilestoneA">
                        <entryCriterion id="EC_1" sentryRef="Sentry1"/>
                      </planItem>
                      <sentry id="Sentry1">
                        <caseFileItemOnPart id="OnPart1" sourceRef="TheItem">
                          <standardEvent>update</standardEvent>
                        </caseFileItemOnPart>
                        <ifPart id="IfPart1" contextRef="TheItem">
                          <condition language="expression://lang/jint" body="value.amount &gt; 0"/>
                        </ifPart>
                      </sentry>
                      <milestone id="MilestoneA"/>
                    </casePlanModel>
                    <caseRoles/>
                  </case>
                </definitions>
                """;

            var firstImport = CmmnXmlSerializer.Import(xml);
            firstImport.IsError.Should().BeFalse(firstImport.Message);

            var exported = CmmnXmlSerializer.Export(firstImport.Value);
            exported.IsError.Should().BeFalse(exported.Message);

            var secondImport = CmmnXmlSerializer.Import(exported.Value);
            secondImport.IsError.Should().BeFalse(secondImport.Message);

            secondImport.Value.Should().BeEquivalentTo(firstImport.Value, ExcludingEngineOnlyMembers);
        }

        // Excludes two families of members that would otherwise be walked twice or add no
        // information to a structural-equality check (see CmmnXmlSerializer.BuildOverrides for
        // the authoritative list and rationale - this mirrors it exactly):
        //   - engine-only, non-XSD partial members (always-empty, or recursive/self-including
        //     computed projections like Stage.PlanItemDefinitionsNested, which yields the stage
        //     itself and would force a cyclic walk with no informational value);
        //   - the concrete-typed XML shadow properties (Model/CmmnXmlCollectionShadows.cs) - each
        //     one is a live view over the exact same List&lt;T&gt; instance its ICollection&lt;T&gt;
        //     counterpart (already compared) exposes, so comparing both is redundant, not additive.
        //
        // DOM nodes (extension any-elements and any-attributes) are compared semantically rather
        // than by FluentAssertions' default member walk: an XmlElement/XmlAttribute drags in
        // OwnerDocument/ParentNode (the entire parsed document, cyclic) and, for XmlAttribute,
        // the PREFIX is legitimately serializer-chosen on export (the namespace is what's
        // authoritative) - so elements compare by OuterXml (self-contained: extension payloads in
        // the samples declare their own xmlns) and attributes by local name + namespace + value.
        private static EquivalencyAssertionOptions<Definitions> ExcludingEngineOnlyMembers(EquivalencyAssertionOptions<Definitions> options) =>
            options
                .IgnoringCyclicReferences()
                .Excluding(member => IsEngineOnlyMember(member))
                .Using<System.Xml.XmlElement>(context =>
                {
                    if (context.Expectation is null)
                    {
                        context.Subject.Should().BeNull();
                        return;
                    }

                    context.Subject.Should().NotBeNull();
                    context.Subject.OuterXml.Should().Be(context.Expectation.OuterXml);
                })
                .WhenTypeIs<System.Xml.XmlElement>()
                .Using<System.Xml.XmlAttribute>(context =>
                {
                    if (context.Expectation is null)
                    {
                        context.Subject.Should().BeNull();
                        return;
                    }

                    context.Subject.Should().NotBeNull();
                    context.Subject.LocalName.Should().Be(context.Expectation.LocalName);
                    context.Subject.NamespaceURI.Should().Be(context.Expectation.NamespaceURI);
                    context.Subject.Value.Should().Be(context.Expectation.Value);
                })
                .WhenTypeIs<System.Xml.XmlAttribute>();

        private static bool IsEngineOnlyMember(IMemberInfo member) =>
            (member.DeclaringType == typeof(Case) && (member.Name == nameof(Case.EntryCriteria) || member.Name == nameof(Case.ExitCriteria))) ||
            (member.DeclaringType == typeof(Stage) && (member.Name == nameof(Stage.IsCasePlanModel) || member.Name == nameof(Stage.PlanItemDefinitionsNested))) ||
            (member.DeclaringType == typeof(Sentry) && (member.Name == nameof(Sentry.PlanItemOnParts) || member.Name == nameof(Sentry.CaseFileItemOnParts))) ||
            (member.DeclaringType == typeof(PlanningTable) && member.Name == nameof(PlanningTable.DiscretionaryItems)) ||
            member.Name.EndsWith("Xml", System.StringComparison.Ordinal);

        private static string ReadEmbeddedResource(string suffix)
        {
            var assembly = typeof(CmmnXmlSerializerTests).Assembly;
            var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new System.IO.StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
