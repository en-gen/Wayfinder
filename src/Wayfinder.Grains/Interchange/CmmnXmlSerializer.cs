using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Model.Interchange;

namespace Wayfinder.Grains.Interchange
{
    // ADO #20 - structural .cmmn import/export.
    // ~~~~~
    // Wraps System.Xml.Serialization.XmlSerializer directly over the XSD-generated model
    // (Spec.CMMN.MODEL.cs is XmlSerializer-ready by construction - docs/06 §2.4) plus the
    // hand-authored Definitions root (Model/Interchange/Definitions.cs - see that file's remarks
    // for why it isn't generated). No custom (de)serialization logic for any individual CMMN
    // element: every element/attribute name, namespace, and polymorphic choice (Stage.
    // PlanItemDefinitions' caseTask/humanTask/milestone/... element-name-to-type mapping,
    // Sentry.OnParts' planItemOnPart/caseFileItemOnPart mapping, TimerEventListener.TimerStart's
    // trigger-kind mapping) already comes from the generated attributes - that is the entire
    // point of a structural importer being "days, not weeks" per docs/06 §2.4.
    //
    // XmlAttributeOverrides (BuildOverrides, below) does two jobs, neither of which touches the
    // generated file or its partials - XmlAttributeOverrides is a serializer-construction-time
    // mechanism, entirely external to the types themselves:
    //
    // 1. Suppresses a handful of engine-only members that partials in Model/ add to the generated
    //    types for runtime bookkeeping (PlanItemBehaviorConfiguratorService's dispatch, System.
    //    Text.Json round-tripping) and that have no counterpart anywhere in the real CMMN XSD:
    //      - Case.EntryCriteria / Case.ExitCriteria (Model/Case.cs) - satisfy IBehaviorDefinition
    //        for the behavior configurator; tCase has no such elements, and these two are always
    //        empty (nothing in the engine ever adds to them).
    //      - Stage.IsCasePlanModel (Model/Stage.cs) - a plain bool with a public setter and no XSD
    //        counterpart. Left un-ignored, it serializes as a spurious &lt;IsCasePlanModel&gt;
    //        element on every stage - it does not need to travel through XML at all: which Stage
    //        is the case plan model is already expressed structurally (Case.CasePlanModel points
    //        at it), and CaseGrain.Create sets the flag itself regardless of where the Case came
    //        from - see CaseInterchangeExtensions.
    //      - Stage.PlanItemDefinitionsNested, Sentry.PlanItemOnParts, Sentry.CaseFileItemOnParts,
    //        PlanningTable.DiscretionaryItems - get-only properties typed as a bare
    //        IEnumerable&lt;T&gt; (an OfType()/recursive-yield projection, not a genuine
    //        collection) - see reason 2 below for why a bare IEnumerable&lt;T&gt; specifically
    //        cannot just be left alone here.
    //
    // 2. Redirects each *genuine* generated collection property this importer needs to read
    //    (Stage.PlanItemDefinitions, Sentry.OnParts, Case.Inputs, ...) to the concrete-typed
    //    shadow property Model/CmmnXmlCollectionShadows.cs adds alongside it, and ignores the
    //    original. Empirically verified against this exact .NET 10 XmlSerializer (see that file's
    //    remarks for the full explanation and the from-scratch repro this work item used to
    //    confirm it): a get-only collection property is populated on Deserialize (via repeated
    //    .Add() calls) only when the property's OWN STATIC TYPE is concrete (List&lt;T&gt;, an
    //    array); every collection in the generated model is instead typed ICollection&lt;T&gt;
    //    (an interface) at the property level even though the backing field is always a genuine
    //    List&lt;T&gt; - so left un-redirected, EVERY repeatable child anywhere in the model
    //    (PlanItemDefinitions, Sentries, PlanItems, OnParts, CaseFileItem, Roles, Property, ...)
    //    silently deserializes as empty, with no exception. This is also why the bare-
    //    IEnumerable&lt;T&gt; members in reason 1 cannot simply be left un-ignored: XmlSerializer's
    //    reflection importer requires either a settable property or a collection type with a
    //    reachable Add method, and IEnumerable&lt;T&gt; alone has neither - some of the affected
    //    properties throw at serializer-construction time, others are silently skipped, depending
    //    on which other members of the same type are reachable; ignoring them outright is the only
    //    behavior that does not depend on that.
    public static class CmmnXmlSerializer
    {
        // Deliberately a single cached instance, not `new XmlSerializer(...)` per call: the
        // XmlAttributeOverrides-taking constructor is the one overload the BCL does NOT cache
        // internally (unlike `new XmlSerializer(Type)`), so calling it repeatedly leaks a dynamic
        // assembly per call - a well-known XmlSerializer pitfall. XmlSerializer instances are
        // documented thread-safe for concurrent Serialize/Deserialize once constructed.
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Definitions), BuildOverrides());

        public static ExecutableResult<Definitions> Import(string xml)
        {
            if (xml is null) throw new ArgumentNullException(nameof(xml));

            using var stringReader = new StringReader(xml);
            return ImportCore(stringReader);
        }

        public static ExecutableResult<Definitions> Import(Stream stream)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));

            using var streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return ImportCore(streamReader);
        }

        public static ExecutableResult<string> Export(Definitions definitions)
        {
            if (definitions is null) throw new ArgumentNullException(nameof(definitions));

            try
            {
                // ADO #72 - schema-valid writer: tExpression carries its text as mixed content
                // (Expression.Text, [XmlText]-mapped), never as the non-schema "body" attribute
                // Expression.ShouldSerializeBody() now suppresses on every Serialize(). Reconcile
                // here, immediately before writing, so Text reflects Body regardless of whether
                // this Definitions came from Import (which already reconciled both ways) or was
                // built by hand with only Body set - see ReconcileExpressionText's remarks for why
                // mutating the passed-in graph in place is the deliberate, low-risk choice here.
                ReconcileExpressionText(definitions);

                var namespaces = new XmlSerializerNamespaces();
                // Empty prefix = default xmlns, matching how real .cmmn files are normally
                // authored (compare formal-16-12-01.pdf's own examples, and the CasePlanModel
                // used throughout Wayfinder.Grains.Tests.Integration): everything in a CMMN document
                // lives in one namespace, so hoisting it to the default avoids a "cmmn:" prefix
                // on every single element.
                namespaces.Add(string.Empty, CmmnXml.Namespace);

                // Self-referencing QName convention per spec §9.3.2's own example
                // (caseRef="case_ns:Fraud_Investigation_Case_ID1", "case_ns" bound to that case's
                // own defining namespace): a document's QName-typed refs to its OWN top-level
                // definitions (CaseFileItem.DefinitionRef, ProcessTask.ProcessRef, ...) resolve
                // against its own targetNamespace, so binding a prefix to it here lets those
                // attributes round-trip as e.g. definitionRef="tns:Foo" instead of an
                // auto-generated q1/q2/... prefix. Harmless either way for round-trip fidelity
                // (XmlQualifiedName equality is Namespace+Name, never the prefix spelling used to
                // write it), but this keeps hand-inspectable output closer to spec convention.
                if (!string.IsNullOrWhiteSpace(definitions.TargetNamespace))
                {
                    namespaces.Add("tns", definitions.TargetNamespace);
                }

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    Encoding = Utf8NoBom,
                    OmitXmlDeclaration = false
                };

                var stringBuilder = new StringBuilder();
                using (var stringWriter = new Utf8StringWriter(stringBuilder))
                using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
                {
                    Serializer.Serialize(xmlWriter, definitions, namespaces);
                }

                return ExecutableResult<string>.Success(stringBuilder.ToString());
            }
            catch (InvalidOperationException ex)
            {
                return ExecutableResult<string>.Failure($"failed to export CMMN XML: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static ExecutableResult<Definitions> ImportCore(TextReader textReader)
        {
            // Untrusted-input hardening: a .cmmn file may originate anywhere an importer is
            // exposed to. Neither DTDs nor external entities are part of the CMMN interchange
            // format (spec §9.3), so both are refused outright rather than merely left
            // unresolved - standard XXE mitigation for an XmlSerializer-based importer.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            try
            {
                using var xmlReader = XmlReader.Create(textReader, settings);

                // Strict namespace/root-element handling up front, with a purpose-built message,
                // rather than relying on whatever text XmlSerializer's own exception happens to
                // contain for this specific, very common mistake (wrong or missing xmlns).
                if (!Serializer.CanDeserialize(xmlReader))
                {
                    return ExecutableResult<Definitions>.Failure(
                        $"expected root element <definitions> in namespace '{CmmnXml.Namespace}' (spec §9.3.1); " +
                        $"found <{xmlReader.LocalName}> in namespace '{xmlReader.NamespaceURI}'");
                }

                var definitions = (Definitions)Serializer.Deserialize(xmlReader);

                // ADO #72 - tolerant reader (Postel's law): a source document may carry the
                // expression text as the legacy "body" attribute (Expression.Body's own
                // XmlAttribute mapping, untouched - handles this case with no help needed here) OR
                // as OMG-schema-standard mixed content (Expression.Text, [XmlText]-mapped - also
                // already populated by the untouched mapping). Either way, only one of the two
                // properties comes back populated from Deserialize(); reconcile them into the same
                // model so every existing caller of .Body (ExpressionGrain, CmmnCapabilityLint, ...)
                // keeps working regardless of which form the source document used.
                ReconcileExpressionText(definitions);

                return ExecutableResult<Definitions>.Success(definitions);
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException)
            {
                // XmlSerializer wraps the real parse failure as the InnerException of a generic
                // InvalidOperationException ("There is an error in XML document (r,c).") - surface
                // that inner message, not the uninformative outer wrapper, to the caller.
                return ExecutableResult<Definitions>.Failure($"malformed CMMN XML: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // StringWriter.Encoding is hardcoded to UTF-16 (it reports the encoding of its in-memory
        // char buffer) and XmlWriter always defers to the TextWriter's own Encoding property when
        // writing the <?xml encoding="..."?> declaration, regardless of what XmlWriterSettings.
        // Encoding says - a well-known StringWriter/XmlWriter interaction, not a bug in either.
        // Overriding it here makes the emitted declaration say UTF-8, matching how the string is
        // actually re-read (Import(string) reads via StringReader, which is encoding-agnostic,
        // but a consumer writing Export's result to a file needs the declaration to be honest).
        private sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter(StringBuilder builder) : base(builder)
            {
            }

            public override Encoding Encoding => Utf8NoBom;
        }

        // ADO #72 - Expression.Body/Expression.Text reconciliation (see Model/Expression.cs and
        // the two call sites above for the full picture). Walks the object graph reachable from
        // `root` looking for every Expression instance - there are 12 distinct Expression-typed
        // properties scattered across the model (RepetitionRule.Condition, IfPart.Condition,
        // TimerEventListener.TimerExpression, ProcessTask.ProcessRefExpression, ...), at arbitrary
        // nesting depth (Stage.PlanItemDefinitions can itself contain Stages), so a hand-enumerated
        // list would be one more place to forget to update whenever the model gains a new
        // Expression-bearing element. Reflection-based instead, gated to this codebase's own model
        // namespace so it never wanders into System.Xml DOM payloads (ExtensionElements.Any,
        // AnyAttribute(s), Relationship.Source/Target - XmlElement/XmlAttribute/XmlQualifiedName)
        // or reflects over primitives - and guarded against the model's own reference cycles
        // (Stage.PlanItemDefinitionsNested deliberately yields the stage it's called on) via a
        // reference-equality visited-set.
        private static void ReconcileExpressionText(object root) => Walk(root, new HashSet<object>(ReferenceEqualityComparer.Instance));

        private const string ModelNamespace = "Wayfinder.Grains.Interfaces.Model";

        private static void Walk(object node, HashSet<object> visited)
        {
            if (node is null || node is string) return;

            var type = node.GetType();
            var ns = type.Namespace ?? string.Empty;

            // System.Xml DOM nodes are foreign to our model - and, for XmlElement/XmlNode
            // specifically, self-enumerate their own child nodes, which would otherwise send this
            // walk wandering through arbitrary extension-element payloads for no reason.
            if (ns.StartsWith("System.Xml", StringComparison.Ordinal)) return;

            if (node is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Walk(item, visited);
                }

                return;
            }

            if (type.IsValueType) return; // bool/enum/DateTime/... - no graph identity to track

            if (!(ns == ModelNamespace || ns.StartsWith(ModelNamespace + ".", StringComparison.Ordinal)))
            {
                return; // outside our model - nothing to reconcile, nothing to reflect into
            }

            if (!visited.Add(node)) return; // cycle guard

            if (node is Expression expression)
            {
                if (!string.IsNullOrEmpty(expression.Body))
                {
                    if (string.IsNullOrEmpty(expression.Text))
                    {
                        expression.Text = expression.Body;
                    }
                }
                else if (!string.IsNullOrEmpty(expression.Text))
                {
                    expression.Body = expression.Text.Trim();
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;

                object value;
                try
                {
                    value = property.GetValue(node);
                }
                catch
                {
                    continue; // defensive: a computed property throwing mid-walk shouldn't fail Import/Export
                }

                Walk(value, visited);
            }
        }

        private static XmlAttributeOverrides BuildOverrides()
        {
            var overrides = new XmlAttributeOverrides();

            void Ignore(Type type, string member) =>
                overrides.Add(type, member, new XmlAttributes { XmlIgnore = true });

            // Reason 1: engine-only members with no XSD counterpart (see class remarks).
            Ignore(typeof(Case), nameof(Case.EntryCriteria));
            Ignore(typeof(Case), nameof(Case.ExitCriteria));
            Ignore(typeof(Stage), nameof(Stage.IsCasePlanModel));
            Ignore(typeof(Stage), nameof(Stage.PlanItemDefinitionsNested));
            Ignore(typeof(Sentry), nameof(Sentry.PlanItemOnParts));
            Ignore(typeof(Sentry), nameof(Sentry.CaseFileItemOnParts));
            Ignore(typeof(PlanningTable), nameof(PlanningTable.DiscretionaryItems));

            // Reason 2: redirect each genuine collection to its concrete-typed shadow property
            // (Model/CmmnXmlCollectionShadows.cs) so Deserialize actually populates it. Complete
            // over the generated model - one entry per ICollection<T> property in
            // Spec.CMMN.MODEL.cs (33 total; see the shadow file's COVERAGE remarks for the
            // enumeration method) - and enforced structurally by CmmnXmlShadowCoverageTests.
            Ignore(typeof(ExtensionElements), nameof(ExtensionElements.Any));
            Ignore(typeof(Relationship), nameof(Relationship.Source));
            Ignore(typeof(Relationship), nameof(Relationship.Target));
            Ignore(typeof(CmmnElement), nameof(CmmnElement.Documentation));
            Ignore(typeof(CmmnElement), nameof(CmmnElement.AnyAttributes));
            Ignore(typeof(CmmnElementWithMixedContent), nameof(CmmnElementWithMixedContent.Documentation));
            Ignore(typeof(CmmnElementWithMixedContent), nameof(CmmnElementWithMixedContent.AnyAttribute));
            Ignore(typeof(Case), nameof(Case.Inputs));
            Ignore(typeof(Case), nameof(Case.Outputs));
            Ignore(typeof(CaseFile), nameof(CaseFile.CaseFileItem));
            Ignore(typeof(CaseFileItemDefinition), nameof(CaseFileItemDefinition.Property));
            Ignore(typeof(CaseRoles), nameof(CaseRoles.Roles));
            Ignore(typeof(Children), nameof(Children.CaseFileItem));
            Ignore(typeof(PlanFragment), nameof(PlanFragment.PlanItems));
            Ignore(typeof(PlanFragment), nameof(PlanFragment.Sentries));
            Ignore(typeof(Stage), nameof(Stage.PlanItemDefinitions));
            Ignore(typeof(Stage), nameof(Stage.ExitCriteria));
            Ignore(typeof(PlanItem), nameof(PlanItem.EntryCriteria));
            Ignore(typeof(PlanItem), nameof(PlanItem.ExitCriteria));
            Ignore(typeof(Sentry), nameof(Sentry.OnParts));
            Ignore(typeof(PlanningTable), nameof(PlanningTable.TableItems));
            Ignore(typeof(PlanningTable), nameof(PlanningTable.ApplicabilityRules));
            Ignore(typeof(DiscretionaryItem), nameof(DiscretionaryItem.EntryCriteria));
            Ignore(typeof(DiscretionaryItem), nameof(DiscretionaryItem.ExitCriteria));
            Ignore(typeof(BaseTask), nameof(BaseTask.Inputs));
            Ignore(typeof(BaseTask), nameof(BaseTask.Outputs));
            Ignore(typeof(ProcessTask), nameof(ProcessTask.ParameterMapping));
            Ignore(typeof(CaseTask), nameof(CaseTask.ParameterMapping));
            Ignore(typeof(DecisionTask), nameof(DecisionTask.ParameterMapping));
            Ignore(typeof(Process), nameof(Process.Input));
            Ignore(typeof(Process), nameof(Process.Output));
            Ignore(typeof(Decision), nameof(Decision.Input));
            Ignore(typeof(Decision), nameof(Decision.Output));

            return overrides;
        }
    }
}
