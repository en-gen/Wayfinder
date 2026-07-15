using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Model.Interchange;

namespace Flow.Grains.Interchange
{
    // ADO #20 - the capability lint / honesty gate.
    // ~~~~~
    // Walks an imported model and reports constructs this engine cannot yet faithfully run.
    // Every rule below is backed by a specific runtime gap verified by reading the engine (not
    // guessed from the README's prose, which this work item found to be stale in places - see
    // this work item's report):
    //
    //  1. ProcessTask / CaseTask / DecisionTask (Unsupported) - PlanItemBehaviorConfiguratorService.
    //     Configure (Services/PlanItemBehaviorConfigurator/PlanItemBehaviorConfiguratorService.cs)
    //     falls through to the generic `case BaseTask task: new TaskBehavior<BaseTask>(...)` for
    //     all three. TaskBehavior (Plan/PlanItem/Behaviors/TaskBehavior.cs) implements only the
    //     generic Task/Stage lifecycle (5.24/8.7-8.8: entry/exit criteria, repetition, required,
    //     manual activation, blocking/non-blocking completion) and never reads ProcessRef/CaseRef/
    //     DecisionRef/ParameterMapping anywhere in that file - the task's defining behavior
    //     (invoke a process/case/decision) never happens. A blocking instance (the default) sits
    //     in Active forever with nothing to progress it; a non-blocking one silently
    //     auto-completes as if the referenced work had actually run.
    //
    //  2. An Expression whose language attribute is explicitly set to something other than
    //     ExpressionLanguage.Jint (Unsupported, per expression) - confirmed in the engine's own
    //     words: SentryGrain.cs's class remarks ("D10 remainder - 'no expression-language
    //     dispatch': verified still open, NOT closed here. Expression.Language (5.4.7/Table 5.33)
    //     is read and stored on the model but Executable.cs... always evaluates via Jint
    //     regardless of its value - no XPath/FEEL/JUEL dispatch exists"). Confirmed independently
    //     by reading ExpressionGrain.BuildExecutable, which reads only expression.Body.
    //
    //  3. Definitions.ExpressionLanguage (the document-level default, spec §5.1.2/Table 5.3) set
    //     to a non-Jint value (Degraded, one finding per document) - same root cause as #2, but
    //     framed as a deviation rather than a dead construct: every expression in the document
    //     still runs (via Jint, always), so the document is not broken, it just silently does not
    //     honor the default language it declares for itself.
    //
    //  4. (formerly PlanItemOnPart.ExitCriterionRef populated) - closed by Bug #82: SentryGrain's
    //     D10 remarks. PlanItemTransitionedEvent.ExitCriterionRef is now populated at its one
    //     construction site (BaseBehavior.HandleTransitioned, fed via Stateless's own
    //     parameterized-trigger mechanism - see PlanItemStateMachine.FireAsync(PlanItemTransition,
    //     string)), so a PlanItemOnPart naming an ExitCriterion is a live, runnable construct, not
    //     a dead one - nothing left here to flag.
    //
    //  5. CaseFileItem hierarchy - a non-empty Children, or a set SourceRef/TargetRefs
    //     (Unsupported) - CaseFileItemGrain (Plan/CaseFileItem/CaseFileItemGrain.cs) addresses and
    //     matches transitions purely by the item's own id (see that file's own comments on
    //     SourceRef matching CaseFileItemOnPart/CaseFileItemStartTrigger); nothing in that grain
    //     reads Children, SourceRef, or TargetRefs on the CaseFileItem model itself - each
    //     CaseFileItem is an independently addressed grain with no parent/child propagation.
    //
    // Deliberately NOT linted: whether a referenced CaseFileItem will actually exist at evaluation
    // time (ExpressionGrain.BuildExecutable's documented fail-closed behavior for a missing
    // ContextRef) is a runtime data condition, not a static model property - there is nothing to
    // walk here that would tell them apart. Also not linted: Import/StructureRef/ImportRef
    // cross-file resolution - out of scope for this MVP (see CmmnXmlSerializer's remarks), and
    // structurally inert either way (the engine never dereferences them, imported or not).
    public static class CmmnCapabilityLint
    {
        public static CmmnCapabilityLintReport Lint(Definitions definitions)
        {
            if (definitions is null) throw new ArgumentNullException(nameof(definitions));

            var findings = new List<CmmnCapabilityFinding>();

            if (IsNonJint(definitions.ExpressionLanguage))
            {
                findings.Add(new CmmnCapabilityFinding(
                    CmmnCapabilitySeverity.Degraded,
                    definitions.Id,
                    nameof(Definitions),
                    $"declares a document-level default expression language of '{definitions.ExpressionLanguage}', " +
                    "but ExpressionGrain always evaluates every expression through the sandboxed Jint executor " +
                    "regardless (M-EXPR - see SentryGrain.cs's D10 remarks) - expressions that omit their own " +
                    $"language attribute will silently run as Jint scripts, not '{definitions.ExpressionLanguage}'"));
            }

            foreach (var @case in definitions.Cases)
            {
                findings.AddRange(LintCase(@case));
            }

            return new CmmnCapabilityLintReport(findings);
        }

        public static CmmnCapabilityLintReport Lint(Case @case)
        {
            if (@case is null) throw new ArgumentNullException(nameof(@case));

            return new CmmnCapabilityLintReport(LintCase(@case).ToList());
        }

        private static IEnumerable<CmmnCapabilityFinding> LintCase(Case @case)
        {
            var casePlanModel = @case.CasePlanModel;
            if (casePlanModel == null) yield break;

            var allDefinitions = casePlanModel.PlanItemDefinitionsNested.ToList();
            var allSentries = allDefinitions.OfType<PlanFragment>().SelectMany(f => f.Sentries).ToList();

            // Rule 1 - model-only task kinds.
            foreach (var definition in allDefinitions)
            {
                if (definition is ProcessTask or CaseTask or DecisionTask)
                {
                    yield return new CmmnCapabilityFinding(
                        CmmnCapabilitySeverity.Unsupported,
                        definition.Id,
                        definition.GetType().Name,
                        $"{definition.GetType().Name} has no specific runtime behavior - " +
                        "PlanItemBehaviorConfiguratorService gives it only the generic Task lifecycle " +
                        "(TaskBehavior<BaseTask>); the referenced process/case/decision is never invoked");
                }
            }

            // Rule 2 - non-Jint expression language, per expression.
            foreach (var owned in CollectExpressions(allDefinitions, allSentries))
            {
                if (IsNonJint(owned.Expression.Language))
                {
                    yield return new CmmnCapabilityFinding(
                        CmmnCapabilitySeverity.Unsupported,
                        owned.OwnerId,
                        owned.OwnerKind,
                        $"Expression declares language '{owned.Expression.Language}', but ExpressionGrain." +
                        "BuildExecutable always evaluates Body through the sandboxed Jint executor regardless " +
                        "of the declared language (M-EXPR) - this expression will either fail to parse as " +
                        $"JavaScript or silently produce JavaScript semantics instead of '{owned.Expression.Language}'");
                }
            }

            // Rule 4 (PlanItemOnPart.ExitCriterionRef) removed - closed by Bug #82, see this
            // class's remarks above.

            // Rule 5 - CaseFileItem hierarchy is structural only.
            if (@case.CaseFileModel != null)
            {
                foreach (var item in @case.CaseFileModel.CaseFileItem.SelectMany(FlattenCaseFileItem))
                {
                    var hasChildren = item.Children != null && item.Children.CaseFileItem.Count > 0;
                    var hasParentLink = !string.IsNullOrWhiteSpace(item.SourceRef) || (item.TargetRefs?.Length ?? 0) > 0;

                    if (hasChildren || hasParentLink)
                    {
                        yield return new CmmnCapabilityFinding(
                            CmmnCapabilitySeverity.Unsupported,
                            item.Id,
                            nameof(CaseFileItem),
                            "CaseFileItem hierarchy (children/sourceRef/targetRefs) is structurally imported but " +
                            "CaseFileItemGrain has no parent/child propagation - each CaseFileItem is an " +
                            "independently addressed grain keyed only by its own id");
                    }
                }
            }
        }

        private static IEnumerable<CaseFileItem> FlattenCaseFileItem(CaseFileItem item)
        {
            yield return item;

            if (item.Children == null) yield break;

            foreach (var descendant in item.Children.CaseFileItem.SelectMany(FlattenCaseFileItem))
            {
                yield return descendant;
            }
        }

        private readonly struct OwnedExpression
        {
            public OwnedExpression(Expression expression, string ownerId, string ownerKind)
            {
                Expression = expression;
                OwnerId = ownerId;
                OwnerKind = ownerKind;
            }

            public Expression Expression { get; }
            public string OwnerId { get; }
            public string OwnerKind { get; }
        }

        // Every Expression reachable from a Case, paired with the id/kind of the construct that
        // owns it (for a legible finding message). Bounded scope, documented in this class's
        // remarks: PlanningTable.ApplicabilityRules is only checked on the Stage's own top-level
        // PlanningTable, not recursively through nested DiscretionaryItem/PlanningTable choices -
        // deep planning-table nesting is a narrow corner this MVP does not walk.
        private static IEnumerable<OwnedExpression> CollectExpressions(
            IReadOnlyList<PlanItemDefinition> definitions,
            IReadOnlyList<Sentry> sentries)
        {
            foreach (var definition in definitions)
            {
                foreach (var owned in ControlExpressions(definition.DefaultControl, definition.Id, definition.GetType().Name))
                {
                    yield return owned;
                }

                switch (definition)
                {
                    case TimerEventListener timer when timer.TimerExpression != null:
                        yield return new OwnedExpression(timer.TimerExpression, timer.Id, nameof(TimerEventListener));
                        break;

                    case ProcessTask processTask:
                        if (processTask.ProcessRefExpression != null)
                        {
                            yield return new OwnedExpression(processTask.ProcessRefExpression, processTask.Id, nameof(ProcessTask));
                        }
                        foreach (var mapping in processTask.ParameterMapping.Where(m => m.Transformation != null))
                        {
                            yield return new OwnedExpression(mapping.Transformation, mapping.Id, nameof(ParameterMapping));
                        }
                        break;

                    case CaseTask caseTask:
                        if (caseTask.CaseRefExpression != null)
                        {
                            yield return new OwnedExpression(caseTask.CaseRefExpression, caseTask.Id, nameof(CaseTask));
                        }
                        foreach (var mapping in caseTask.ParameterMapping.Where(m => m.Transformation != null))
                        {
                            yield return new OwnedExpression(mapping.Transformation, mapping.Id, nameof(ParameterMapping));
                        }
                        break;

                    case DecisionTask decisionTask:
                        if (decisionTask.DecisionRefExpression != null)
                        {
                            yield return new OwnedExpression(decisionTask.DecisionRefExpression, decisionTask.Id, nameof(DecisionTask));
                        }
                        foreach (var mapping in decisionTask.ParameterMapping.Where(m => m.Transformation != null))
                        {
                            yield return new OwnedExpression(mapping.Transformation, mapping.Id, nameof(ParameterMapping));
                        }
                        break;

                    case Stage { PlanningTable: not null } stage:
                        foreach (var rule in stage.PlanningTable.ApplicabilityRules.Where(r => r.Condition != null))
                        {
                            yield return new OwnedExpression(rule.Condition, rule.Id, nameof(ApplicabilityRule));
                        }
                        break;
                }
            }

            foreach (var sentry in sentries.Where(s => s.IfPart?.Condition != null))
            {
                yield return new OwnedExpression(sentry.IfPart.Condition, sentry.Id, nameof(Sentry));
            }
        }

        private static IEnumerable<OwnedExpression> ControlExpressions(PlanItemControl control, string ownerId, string ownerKind)
        {
            if (control == null) yield break;

            if (control.RepetitionRule?.Condition != null)
            {
                yield return new OwnedExpression(control.RepetitionRule.Condition, ownerId, ownerKind);
            }
            if (control.RequiredRule?.Condition != null)
            {
                yield return new OwnedExpression(control.RequiredRule.Condition, ownerId, ownerKind);
            }
            if (control.ManualActivationRule?.Condition != null)
            {
                yield return new OwnedExpression(control.ManualActivationRule.Condition, ownerId, ownerKind);
            }
        }

        private static bool IsNonJint(string language) =>
            !string.IsNullOrWhiteSpace(language) && language != ExpressionLanguage.Jint;
    }
}
