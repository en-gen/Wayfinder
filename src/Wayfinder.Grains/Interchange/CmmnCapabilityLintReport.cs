using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Grains.Interchange
{
    // Severity rubric for a CmmnCapabilityLint finding (see that class's remarks for the exact
    // rule set and the runtime evidence behind each rule).
    public enum CmmnCapabilitySeverity
    {
        // Runs, but deviates from spec in a documented, deliberate way (e.g. a document-level
        // default the engine never actually consults).
        Degraded,

        // Would silently not run at all: the construct's defining behavior never executes, or the
        // event it depends on can never occur.
        Unsupported
    }

    // One capability-lint finding: a specific construct in an imported model that this engine
    // cannot yet faithfully run.
    public sealed class CmmnCapabilityFinding
    {
        public CmmnCapabilityFinding(CmmnCapabilitySeverity severity, string elementId, string elementKind, string message)
        {
            if (string.IsNullOrWhiteSpace(elementKind)) throw new ArgumentException("must not be blank", nameof(elementKind));
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("must not be blank", nameof(message));

            Severity = severity;
            ElementId = elementId;
            ElementKind = elementKind;
            Message = message;
        }

        public CmmnCapabilitySeverity Severity { get; }

        // The CmmnElement.Id of the offending construct, or null for a document-level finding
        // (e.g. Definitions.ExpressionLanguage).
        public string ElementId { get; }

        // The CLR type name of the offending construct (e.g. "ProcessTask", "Sentry", "Definitions").
        public string ElementKind { get; }

        public string Message { get; }

        public override string ToString() =>
            ElementId is null
                ? $"[{Severity}] {ElementKind}: {Message}"
                : $"[{Severity}] {ElementKind} '{ElementId}': {Message}";
    }

    // Data, not an exception: the capability lint's honesty-gate output (ADO #20 - "importing a
    // model the engine can't faithfully run must be loud, never silent"). Callers decide what to
    // do with a non-empty report - refuse to deploy, deploy with a warning banner, log and
    // proceed - CmmnCapabilityLint itself never throws and never blocks anything.
    public sealed class CmmnCapabilityLintReport
    {
        public CmmnCapabilityLintReport(IReadOnlyList<CmmnCapabilityFinding> findings)
        {
            Findings = findings ?? throw new ArgumentNullException(nameof(findings));
        }

        public IReadOnlyList<CmmnCapabilityFinding> Findings { get; }

        public bool HasFindings => Findings.Count > 0;

        public bool HasUnsupported => Findings.Any(f => f.Severity == CmmnCapabilitySeverity.Unsupported);

        public bool HasDegraded => Findings.Any(f => f.Severity == CmmnCapabilitySeverity.Degraded);
    }
}
