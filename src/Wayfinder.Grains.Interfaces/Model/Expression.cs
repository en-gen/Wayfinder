namespace Wayfinder.Grains.Interfaces.Model
{
    // ADO #72 - OMG CMMN 1.1's tExpression (CMMN11CaseModel.xsd) is mixed="true" with a single
    // "language" attribute; it has NO "body" attribute. Expression.Body (Spec.CMMN.MODEL.cs) is a
    // pre-existing, non-schema addition this engine has always used as its in-memory canonical
    // property for expression text - ExpressionGrain.BuildExecutable and every test in the
    // codebase read/write .Body, never .Text (the inherited CmmnElementWithMixedContent.Text,
    // [XmlText]-mapped, which IS how the schema's mixed content round-trips). Body stays exactly
    // as generated - nothing here changes what any existing caller reads or writes.
    //
    // The only thing this partial adds is suppressing "body" from EXPORT: ShouldSerializeBody()
    // is XmlSerializer's standard sidecar-method convention (works for XmlAttribute-mapped members
    // exactly like XmlIgnore-by-condition) and affects Serialize() only - Deserialize() is
    // untouched, so a document that still carries body="..." (this engine's own pre-#72 output,
    // or any other legacy producer) keeps parsing via the untouched XmlAttribute mapping on Body.
    // CmmnXmlSerializer.ReconcileExpressionText is the other half: it keeps Body and Text in sync
    // after Import (so a schema-standard mixed-content document also populates Body) and before
    // Export (so Text - the only thing actually written to the wire once Body is suppressed here -
    // always reflects whatever Body was set to, regardless of whether the Expression came from
    // Import or was hand-built).
    public partial class Expression
    {
        public bool ShouldSerializeBody() => false;
    }
}
