using System.Collections.Generic;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Expressions
{
    // The bound evaluation context (design 05 §D.2). A PLAIN type on purpose: it is never sent
    // over the wire, so it carries no [GenerateSerializer]/[Id(n)]. OQ-9 was measured (#257) and
    // resolved to "in-process service" - the grain hop this request shape was originally designed
    // to make cheap costs more than the Jint work it wraps, and the bound-request shape is only
    // cheaper than the old per-call fetch at SMALL contexts (it crosses over between 25 and 200
    // bound properties). So IExpressionGrain was deleted rather than rewritten, and nothing here
    // is serialized at all.
    //
    // Everything the evaluator needs is resolved by the CALLER before the call - see
    // ExpressionContext, which is what actually performs the case-file reads. The evaluator itself
    // makes no outbound calls of any kind (finding I-4; the architecture test in
    // Wayfinder.Grains.Tests/Expressions/ExpressionEvaluatorArchitectureTests.cs enforces it).
    public sealed class ExpressionRequest
    {
        public Expression Expression { get; init; }

        // Optional IDREF to a CaseFileItem (5.4.6.4 IfPart.contextRef, 5.4.11.x rule contextRef).
        // Null means "not specified", which is NOT the same as "specified but missing" - see
        // ContextMissing and CaseFileModel below.
        public string ContextRef { get; init; }

        // The bound value of ContextRef, read by the caller. Null when ContextRef is null or when
        // ContextMissing is true.
        public JsonNode ContextValue { get; init; }

        // Bound when ContextRef is null: the case's EXISTING case-file items keyed by definition
        // id (finding I4 / Table 5.32 - "If not specified, evaluation starts at the CaseFile object
        // that is referenced by the Case as its caseFileModel"). Null/empty simply binds nothing.
        public IReadOnlyDictionary<string, JsonNode> CaseFileModel { get; init; }

        // ContextRef names a CaseFileItem that has not been created for this case instance. Fails
        // closed - see ExpressionEvaluator.Bind for the full rationale and the deliberate asymmetry
        // with the ContextRef-absent case.
        public bool ContextMissing { get; init; }

        // Diagnostics only: preserves the case instance id in the fail-closed message the old
        // ExpressionGrain produced ("context CaseFileItem 'X' has not been created for case Y").
        // The evaluator reads it for that message and nothing else - it is data on the request, not
        // a handle to anything.
        public System.Guid CaseInstanceId { get; init; }
    }
}
