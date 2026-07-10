using System;

namespace Flow.Grains.Tests.Integration.Conformance
{
    // Structured echo of the spec table/row (or deviation id) a scenario pins, stacked alongside
    // the prose citation every scenario already carries in its own comments/Should().Be(...,
    // "because...") messages. Purely documentation today (nothing reflects over it) - see
    // Conformance/COVERAGE.md for the authoritative spec-row -> scenario map this attribute
    // echoes; it exists so the citation travels with the test method itself (visible from an IDE
    // "Find Usages"/test explorer without opening the coverage doc), not to replace that doc.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ConformanceCitationAttribute : Attribute
    {
        public string Reference { get; }

        public ConformanceCitationAttribute(string reference)
        {
            Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        }
    }
}
