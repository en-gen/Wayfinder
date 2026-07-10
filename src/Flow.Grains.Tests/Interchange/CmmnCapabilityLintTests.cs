using System.Linq;
using Flow.Grains.Interchange;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Model.Interchange;
using FluentAssertions;
using Xunit;

namespace Flow.Grains.Tests.Interchange
{
    // ADO #20 - the capability lint is the honesty gate: each test here pins ONE specific,
    // verified runtime gap (see CmmnCapabilityLint's class remarks for the evidence behind each
    // rule) to exactly the finding it must produce, plus one "clean model" test proving the lint
    // does not cry wolf over the fully-supported shape the flagship test actually deploys.
    public class CmmnCapabilityLintTests
    {
        [Theory]
        [InlineData(typeof(ProcessTask))]
        [InlineData(typeof(CaseTask))]
        [InlineData(typeof(DecisionTask))]
        public void Lint__Given_ModelOnlyTaskKind__Then_ReturnsUnsupportedFinding(System.Type taskType)
        {
            var task = (PlanItemDefinition)System.Activator.CreateInstance(taskType);
            task.Id = "Task1";

            var @case = MinimalCase(stage => stage.PlanItemDefinitions.Add(task));

            var report = CmmnCapabilityLint.Lint(@case);

            report.HasUnsupported.Should().BeTrue();
            report.Findings.Should().ContainSingle(f =>
                f.Severity == CmmnCapabilitySeverity.Unsupported &&
                f.ElementId == "Task1" &&
                f.ElementKind == taskType.Name);
        }

        [Fact]
        public void Lint__Given_ExpressionWithNonJintLanguage__Then_ReturnsUnsupportedFinding()
        {
            var sentry = new Sentry
            {
                Id = "S1",
                IfPart = new IfPart
                {
                    ContextRef = "TheItem",
                    Condition = new Expression { Language = "http://www.w3.org/1999/XPath", Body = "true()" }
                }
            };

            var @case = MinimalCase(stage => stage.Sentries.Add(sentry));

            var report = CmmnCapabilityLint.Lint(@case);

            report.Findings.Should().ContainSingle(f =>
                f.Severity == CmmnCapabilitySeverity.Unsupported &&
                f.ElementId == "S1" &&
                f.ElementKind == nameof(Sentry) &&
                f.Message.Contains("XPath"));
        }

        [Fact]
        public void Lint__Given_ExpressionWithUnsetLanguage__Then_ReturnsNoFinding()
        {
            // Unset language is the normal/expected path for this engine (ExpressionGrain always
            // evaluates via Jint regardless) - must NOT be flagged, or every ordinary model would
            // false-positive.
            var sentry = new Sentry
            {
                Id = "S1",
                IfPart = new IfPart { ContextRef = "TheItem", Condition = new Expression { Body = "true" } }
            };

            var @case = MinimalCase(stage => stage.Sentries.Add(sentry));

            var report = CmmnCapabilityLint.Lint(@case);

            report.HasFindings.Should().BeFalse();
        }

        [Fact]
        public void Lint__Given_DefinitionsWithNonJintDefaultExpressionLanguage__Then_ReturnsDegradedFinding()
        {
            var definitions = new Definitions
            {
                Id = "Definitions_1",
                ExpressionLanguage = "http://www.w3.org/1999/XPath"
            };

            var report = CmmnCapabilityLint.Lint(definitions);

            report.HasUnsupported.Should().BeFalse("the document still runs - every expression evaluates via Jint regardless");
            report.Findings.Should().ContainSingle(f =>
                f.Severity == CmmnCapabilitySeverity.Degraded &&
                f.ElementId == "Definitions_1" &&
                f.ElementKind == nameof(Definitions));
        }

        [Fact]
        public void Lint__Given_PlanItemOnPartWithExitCriterionRef__Then_ReturnsUnsupportedFinding()
        {
            var sentry = new Sentry
            {
                Id = "S1",
                OnParts =
                {
                    new PlanItemOnPart
                    {
                        SourceRef = "OtherPlanItem",
                        StandardEvent = PlanItemTransition.Exit,
                        ExitCriterionRef = "SomeExitCriterion"
                    }
                }
            };

            var @case = MinimalCase(stage => stage.Sentries.Add(sentry));

            var report = CmmnCapabilityLint.Lint(@case);

            report.Findings.Should().ContainSingle(f =>
                f.Severity == CmmnCapabilitySeverity.Unsupported &&
                f.ElementId == "S1" &&
                f.ElementKind == nameof(Sentry) &&
                f.Message.Contains("exitCriterionRef"));
        }

        [Fact]
        public void Lint__Given_CaseFileItemHierarchy__Then_ReturnsUnsupportedFindingForParentAndChild()
        {
            var child = new CaseFileItem { Id = "ChildItem", SourceRef = "ParentItem" };
            var parent = new CaseFileItem { Id = "ParentItem", Children = new Children { CaseFileItem = { child } } };

            var @case = MinimalCase();
            @case.CaseFileModel = new CaseFile { CaseFileItem = { parent } };

            var report = CmmnCapabilityLint.Lint(@case);

            var hierarchyFindings = report.Findings.Where(f => f.ElementKind == nameof(CaseFileItem)).ToList();
            hierarchyFindings.Should().HaveCount(2, "the parent (non-empty Children) and the child (SourceRef set) are each independently flagged");
            hierarchyFindings.Select(f => f.ElementId).Should().BeEquivalentTo(new[] { "ParentItem", "ChildItem" });
            hierarchyFindings.Should().OnlyContain(f => f.Severity == CmmnCapabilitySeverity.Unsupported);
        }

        [Fact]
        public void Lint__Given_FlagshipShapedModel__Then_ReturnsNoFindings()
        {
            // Mirrors CasePlanModelInstantiationIntegrationTests' shape exactly: Milestone +
            // Sentry (CaseFileItemOnPart + IfPart, Jint), nothing model-only. Proves the lint
            // does not cry wolf on the one shape this work item's flagship actually deploys.
            var entrySentry = new Sentry
            {
                Id = "EntrySentry",
                OnParts = { new CaseFileItemOnPart { SourceRef = "TheCaseFileItem", StandardEvent = CaseFileItemTransition.Update } },
                IfPart = new IfPart
                {
                    ContextRef = "TheCaseFileItem",
                    Condition = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" }
                }
            };
            var milestone = new Milestone { Id = "MilestoneA" };
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = milestone.Id,
                EntryCriteria = { new EntryCriterion { SentryRef = entrySentry.Id } }
            };

            var @case = new Case
            {
                Id = "case-1",
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    Sentries = { entrySentry },
                    PlanItemDefinitions = { milestone },
                    PlanItems = { planItem }
                }
            };

            var report = CmmnCapabilityLint.Lint(@case);

            report.HasFindings.Should().BeFalse();
        }

        private static Case MinimalCase(System.Action<Stage> configureStage = null)
        {
            var stage = new Stage { Id = "CPM" };
            configureStage?.Invoke(stage);

            return new Case
            {
                Id = "case-1",
                CaseRoles = new CaseRoles(),
                CasePlanModel = stage
            };
        }
    }
}
