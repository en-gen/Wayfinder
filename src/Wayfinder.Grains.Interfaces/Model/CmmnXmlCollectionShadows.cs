using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Flow.Grains.Interfaces.Model.Interchange;

namespace Flow.Grains.Interfaces.Model
{
    // ADO #20 - why this file exists.
    // ~~~~~
    // Every repeatable child in the generated model (Spec.CMMN.MODEL.cs) is exposed as a get-only
    // ICollection<T> property backed by a private List<T> field the parameterless constructor
    // initializes (e.g. Stage.PlanItemDefinitions, Sentry.OnParts) - correct and sufficient for
    // XmlSerializer on the WRITE side (it happily enumerates an ICollection<T> getter to emit
    // elements). On the READ side, however, empirically verified against this exact .NET 10
    // XmlSerializer: a get-only collection property only gets populated during Deserialize (via
    // repeated .Add() calls against the existing instance) when the PROPERTY'S OWN STATIC TYPE is
    // concrete (List&lt;T&gt;, an array, ...) - an interface-typed get-only property
    // (ICollection&lt;T&gt;, IEnumerable&lt;T&gt;) is silently left untouched, with no exception
    // and no diagnostic. (Confirmed with a from-scratch, unrelated test type during this work
    // item - not a quirk of this model, a general property of XmlSerializer on this runtime.)
    // Every constructor in Spec.CMMN.MODEL.cs happens to assign a genuine List&lt;T&gt; to the
    // backing field regardless of the property's declared interface type, so each shadow property
    // below is a safe cast, never a copy - it is the SAME list instance the public
    // ICollection&lt;T&gt; property already exposes, just viewed through a concrete-typed lens
    // XmlSerializer will actually populate.
    //
    // Mechanism: each original property is XmlIgnore'd via XmlAttributeOverrides
    // (CmmnXmlSerializer.BuildOverrides) so its XML mapping is claimed exclusively by the shadow
    // property declared here, which carries the identical [XmlElement]/[XmlAnyElement]/
    // [XmlAnyAttribute] attribute(s) copied verbatim from the generated property. Nothing about
    // the public API most of the codebase already uses (Stage.PlanItemDefinitions, Sentry.OnParts,
    // ...) changes - those properties still work exactly as before for every existing caller; this
    // only adds a second, XML-only view over the same backing storage. No generated file or
    // existing partial is touched: these are new partial-class members in a new file, exactly the
    // pattern Model/Case.cs, Stage.cs, Sentry.cs, and PlanningTable.cs already establish for
    // extending a generated type.
    //
    // [JsonIgnore]/[IgnoreDataMember] on every property below (found the hard way - see this work
    // item's report): CmmnPolymorphicTypeResolver's ApplyPopulateToReadOnlyCollections opts EVERY
    // get-only IEnumerable-typed property (Any string) into JsonObjectCreationHandling.Populate,
    // with no allowlist - it has no way to know a newly-added property is "just another view" of
    // one already being serialized. Left un-ignored, OrleansFallbackJsonSerializer's System.Text.
    // Json path would serialize BOTH e.g. PlanItemDefinitions and PlanItemDefinitionsXml (the same
    // underlying list, twice) into a grain call/journaled-state payload, and Populate mode would
    // then .Add() each element from BOTH JSON properties back onto that one shared list on the way
    // back in - silently doubling every collection in the model on any Orleans-serialized round
    // trip (grain-to-grain calls, journaled events). That is what actually happened the first time
    // this file was written without these attributes: PlanItemDefinitionGrain.Define started
    // throwing "already defined" and several unrelated SentryGrain/PlanningTableGrain/
    // StreamSemantics integration tests started failing or timing out, none of which this work
    // item touches - purely a side effect of the duplicated collections these shadow properties
    // caused before they were marked ignored here.
    //
    // COVERAGE: COMPLETE - every XML-mapped collection property in the generated model is
    // shadowed here. Enumerated by exhaustive scan, not sampling: the generated file declares
    // every repeatable child with the exact shape `public System.Collections.Generic.ICollection<`
    // (verified 33 matches by grep over Spec.CMMN.MODEL.cs; zero IList/IEnumerable/Collection/
    // XmlArray variants and zero settable collections exist in that file), and all 33 have a
    // shadow below. The only collection-shaped members deliberately NOT shadowed, each because it
    // needs no shadow:
    //   - string[] attribute-mapped members with public setters (CaseFileItem.TargetRefs,
    //     TableItem.ApplicabilityRuleRefs/AuthorizedRoleRefs, UserEventListener.
    //     AuthorizedRoleRefs) - settable arrays populate fine;
    //   - Documentation.Any - a single settable XmlElement, not a collection;
    //   - the engine-only partial projections (Stage.PlanItemDefinitionsNested, Sentry.
    //     PlanItemOnParts/CaseFileItemOnParts, PlanningTable.DiscretionaryItems, Case.
    //     EntryCriteria/ExitCriteria) - no XSD counterpart at all, ignored outright in
    //     BuildOverrides;
    //   - the hand-authored Interchange types (Definitions/Import) - authored with concrete
    //     List&lt;T&gt; properties from the start, so they populate without shadows.
    // Enforced structurally by CmmnXmlShadowCoverageTests (Flow.Grains.Tests/Interchange), which
    // re-derives the obligation set by reflection on every run - a regenerated model file or a
    // new partial that adds an XML-visible interface-typed collection without a matching shadow
    // (and BuildOverrides ignore) fails that suite, so this class of bug cannot silently reopen.

    public partial class ExtensionElements
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlAnyElement]
        public List<System.Xml.XmlElement> AnyXml => (List<System.Xml.XmlElement>)Any;
    }

    public partial class Relationship
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("source", Namespace = CmmnXml.Namespace, DataType = "QName")]
        public List<System.Xml.XmlQualifiedName> SourceXml => (List<System.Xml.XmlQualifiedName>)Source;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("target", Namespace = CmmnXml.Namespace, DataType = "QName")]
        public List<System.Xml.XmlQualifiedName> TargetXml => (List<System.Xml.XmlQualifiedName>)Target;
    }

    public partial class CmmnElement
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("documentation", Namespace = CmmnXml.Namespace)]
        public List<Documentation> DocumentationXml => (List<Documentation>)Documentation;

        [JsonIgnore, IgnoreDataMember]
        [XmlAnyAttribute]
        public List<System.Xml.XmlAttribute> AnyAttributesXml => (List<System.Xml.XmlAttribute>)AnyAttributes;
    }

    public partial class CmmnElementWithMixedContent
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("documentation", Namespace = CmmnXml.Namespace)]
        public List<Documentation> DocumentationXml => (List<Documentation>)Documentation;

        [JsonIgnore, IgnoreDataMember]
        [XmlAnyAttribute]
        public List<System.Xml.XmlAttribute> AnyAttributeXml => (List<System.Xml.XmlAttribute>)AnyAttribute;
    }

    public partial class Case
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("input", Namespace = CmmnXml.Namespace)]
        public List<CaseParameter> InputsXml => (List<CaseParameter>)Inputs;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("output", Namespace = CmmnXml.Namespace)]
        public List<CaseParameter> OutputsXml => (List<CaseParameter>)Outputs;
    }

    public partial class CaseFile
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("caseFileItem", Namespace = CmmnXml.Namespace)]
        public List<CaseFileItem> CaseFileItemXml => (List<CaseFileItem>)CaseFileItem;
    }

    public partial class CaseFileItemDefinition
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("property", Namespace = CmmnXml.Namespace)]
        public List<Property> PropertyXml => (List<Property>)Property;
    }

    public partial class CaseRoles
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("role", Namespace = CmmnXml.Namespace)]
        public List<Role> RolesXml => (List<Role>)Roles;
    }

    public partial class Children
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("caseFileItem", Namespace = CmmnXml.Namespace)]
        public List<CaseFileItem> CaseFileItemXml => (List<CaseFileItem>)CaseFileItem;
    }

    public partial class PlanFragment
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("planItem", Namespace = CmmnXml.Namespace)]
        public List<PlanItem> PlanItemsXml => (List<PlanItem>)PlanItems;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("sentry", Namespace = CmmnXml.Namespace)]
        public List<Sentry> SentriesXml => (List<Sentry>)Sentries;
    }

    public partial class Stage
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("caseTask", Type = typeof(CaseTask), Namespace = CmmnXml.Namespace)]
        [XmlElement("decisionTask", Type = typeof(DecisionTask), Namespace = CmmnXml.Namespace)]
        [XmlElement("eventListener", Type = typeof(EventListener), Namespace = CmmnXml.Namespace)]
        [XmlElement("humanTask", Type = typeof(HumanTask), Namespace = CmmnXml.Namespace)]
        [XmlElement("milestone", Type = typeof(Milestone), Namespace = CmmnXml.Namespace)]
        [XmlElement("processTask", Type = typeof(ProcessTask), Namespace = CmmnXml.Namespace)]
        [XmlElement("stage", Type = typeof(Stage), Namespace = CmmnXml.Namespace)]
        [XmlElement("timerEventListener", Type = typeof(TimerEventListener), Namespace = CmmnXml.Namespace)]
        [XmlElement("userEventListener", Type = typeof(UserEventListener), Namespace = CmmnXml.Namespace)]
        public List<PlanItemDefinition> PlanItemDefinitionsXml => (List<PlanItemDefinition>)PlanItemDefinitions;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("exitCriterion", Namespace = CmmnXml.Namespace)]
        public List<ExitCriterion> ExitCriteriaXml => (List<ExitCriterion>)ExitCriteria;
    }

    public partial class PlanItem
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("entryCriterion", Namespace = CmmnXml.Namespace)]
        public List<EntryCriterion> EntryCriteriaXml => (List<EntryCriterion>)EntryCriteria;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("exitCriterion", Namespace = CmmnXml.Namespace)]
        public List<ExitCriterion> ExitCriteriaXml => (List<ExitCriterion>)ExitCriteria;
    }

    public partial class Sentry
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("planItemOnPart", Type = typeof(PlanItemOnPart), Namespace = CmmnXml.Namespace)]
        [XmlElement("caseFileItemOnPart", Type = typeof(CaseFileItemOnPart), Namespace = CmmnXml.Namespace)]
        public List<OnPart> OnPartsXml => (List<OnPart>)OnParts;
    }

    public partial class PlanningTable
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("discretionaryItem", Type = typeof(DiscretionaryItem), Namespace = CmmnXml.Namespace)]
        [XmlElement("planningTable", Type = typeof(PlanningTable), Namespace = CmmnXml.Namespace)]
        public List<TableItem> TableItemsXml => (List<TableItem>)TableItems;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("applicabilityRule", Namespace = CmmnXml.Namespace)]
        public List<ApplicabilityRule> ApplicabilityRulesXml => (List<ApplicabilityRule>)ApplicabilityRules;
    }

    public partial class DiscretionaryItem
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("entryCriterion", Namespace = CmmnXml.Namespace)]
        public List<EntryCriterion> EntryCriteriaXml => (List<EntryCriterion>)EntryCriteria;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("exitCriterion", Namespace = CmmnXml.Namespace)]
        public List<ExitCriterion> ExitCriteriaXml => (List<ExitCriterion>)ExitCriteria;
    }

    public partial class BaseTask
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("input", Namespace = CmmnXml.Namespace)]
        public List<CaseParameter> InputsXml => (List<CaseParameter>)Inputs;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("output", Namespace = CmmnXml.Namespace)]
        public List<CaseParameter> OutputsXml => (List<CaseParameter>)Outputs;
    }

    public partial class ProcessTask
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("parameterMapping", Namespace = CmmnXml.Namespace)]
        public List<ParameterMapping> ParameterMappingXml => (List<ParameterMapping>)ParameterMapping;
    }

    public partial class CaseTask
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("parameterMapping", Namespace = CmmnXml.Namespace)]
        public List<ParameterMapping> ParameterMappingXml => (List<ParameterMapping>)ParameterMapping;
    }

    public partial class DecisionTask
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("parameterMapping", Namespace = CmmnXml.Namespace)]
        public List<ParameterMapping> ParameterMappingXml => (List<ParameterMapping>)ParameterMapping;
    }

    public partial class Process
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("input", Namespace = CmmnXml.Namespace)]
        public List<ProcessParameter> InputXml => (List<ProcessParameter>)Input;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("output", Namespace = CmmnXml.Namespace)]
        public List<ProcessParameter> OutputXml => (List<ProcessParameter>)Output;
    }

    public partial class Decision
    {
        [JsonIgnore, IgnoreDataMember]
        [XmlElement("input", Namespace = CmmnXml.Namespace)]
        public List<DecisionParameter> InputXml => (List<DecisionParameter>)Input;

        [JsonIgnore, IgnoreDataMember]
        [XmlElement("output", Namespace = CmmnXml.Namespace)]
        public List<DecisionParameter> OutputXml => (List<DecisionParameter>)Output;
    }
}
