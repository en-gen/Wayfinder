using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Xunit;
using CaseFileItemModel = Wayfinder.Grains.Interfaces.Model.CaseFileItem;

namespace Wayfinder.Grains.Tests.Integration.Expressions
{
    // D1 - Expression context binder
    // ~~~~~
    // ExpressionContext + ExpressionEvaluator are the single chokepoint every IfPart/rule/
    // applicability evaluation call site funnels through (SentryGrain.EvaluateIfPart,
    // BaseBehavior.EvaluateRule, PlanningTableGrain.EvaluateApplicabilityRule,
    // TimerEventListenerBehavior.EvaluateTimerExpression). These tests exercise the binder
    // directly rather than only through a Sentry, so the contextRef resolution/binding/failure
    // behaviour is pinned independent of any one caller's plumbing.
    //
    // Formerly ExpressionGrainTests. The evaluator is no longer a grain (OQ-9, measured in #257),
    // so the subject is CONSTRUCTED rather than addressed - which is itself the point. It still
    // needs the cluster, because binding a contextRef means reading a real CaseFileItemGrain.
    // Every assertion the grain-era file made is preserved; the contextRef-absent cases are new,
    // and are what finding I4 opened.
    [Collection(ClusterCollection.Name)]
    public class ExpressionEvaluationTests
    {
        private readonly IClusterClient _clusterClient;
        private readonly IExpressionEvaluator _evaluator;

        public ExpressionEvaluationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            // Mirrors ServiceCollectionExtensions.AddRuleExecutor's factory exactly: a fresh
            // sandboxed Jint realm per executable, which is the production shape.
            _evaluator = new ExpressionEvaluator(
                expression => new Executable(
                    SandboxedJintEngine.Create(),
                    NullLogger<Executable>.Instance,
                    expression),
                NullLogger<ExpressionEvaluator>.Instance);

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        private IExpressionContext ContextFor(Guid caseInstanceId, params string[] caseFileItemIds) =>
            new ExpressionContext(
                _evaluator,
                _clusterClient,
                caseInstanceId,
                () => new CaseModelPin { CaseFileItemIds = caseFileItemIds });

        // 5.4.6.4 IfPart / this engine's identifier contract: the bound CaseFileItem value is
        // exposed under the spec-agnostic "value" alias.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_ContextRefWithObjectValue__Then_ConditionSeesValueByGenericAlias
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await ContextFor(caseInstanceId).EvaluateAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // Same binding, but the condition references the contextRef's own id directly - the
        // spec-natural alias (the identifier the modeler used to declare the context) rather than
        // the generic "value" alias. Both aliases refer to the same bound CaseFileItem value.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_ContextRefWithObjectValue__Then_ConditionSeesValueByContextRefAlias
            (Guid caseInstanceId)
        {
            const string caseFileItemId = "TheCaseFileItem";
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "TheCaseFileItem.amount > 100" };

            var result = await ContextFor(caseInstanceId).EvaluateAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_ContextRefWithObjectValue__When_ConditionFalse__Then_FalseResult
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 50}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await ContextFor(caseInstanceId).EvaluateAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeFalse();
        }

        // A CaseFileItem's Value is a bare JsonNode of any shape (5.3.2) - not necessarily an
        // object. Binding must not assume object-shapedness (see IExecutable.WithJsonArgument's
        // remarks on why WithArgument/WithContext could not be reused as-is for this).
        [Theory, AutoData]
        public async Task EvaluateAsString__Given_ContextRefWithScalarValue__Then_ConditionSeesScalar
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonValue.Create("filed"));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value" };

            var result = await ContextFor(caseInstanceId).EvaluateAsString(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().Be("filed");
        }

        // Deliberate, and deliberately asymmetric with the contextRef-absent case below (see
        // ExpressionEvaluator.Bind): a contextRef naming a CaseFileItem that was never created is
        // treated as an expression failure rather than binding a JS null and letting a
        // member-access expression throw its own TypeError.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_ContextRefToNeverCreatedCaseFileItem__Then_FailureResult
            (Guid caseInstanceId, string neverCreatedCaseFileItemId)
        {
            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await ContextFor(caseInstanceId).EvaluateAsBool(neverCreatedCaseFileItemId, expression);

            result.IsError.Should().BeTrue();
            result.Message.Should().Contain(neverCreatedCaseFileItemId);
        }

        // A hostile or broken expression must fail, not throw out of the call and not hang - pins
        // that the sandbox budget (SandboxedJintEngine) is still in effect once context is bound.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_ContextRefBound__When_ExpressionThrows__Then_FailureResult
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount.nonExistent.deeper" };

            var result = await ContextFor(caseInstanceId).EvaluateAsBool(caseFileItemId, expression);

            result.IsError.Should().BeTrue();
        }

        // No contextRef and no declared caseFileModel: nothing to bind, which is exactly the
        // pre-I4 behaviour and the shape a literal timerExpression takes.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_NullContextRefAndNoCaseFileModel__Then_EvaluatesWithoutBinding
            (Guid caseInstanceId)
        {
            var result = await ContextFor(caseInstanceId).EvaluateAsBool(null, Rules.TruthyExpression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // I4 - Table 5.32 IfPart.contextRef / Table 5.54 RepetitionRule.contextRef: "If not
        // specified, evaluation starts at the CaseFile object that is referenced by the Case as its
        // caseFileModel." Before this change the expression below produced a Jint ReferenceError,
        // so the condition errored and the sentry could never satisfy.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_NullContextRef__Then_BindsCaseFileItemsByDefinitionId
            (Guid caseInstanceId)
        {
            await CreateCaseFileItem(caseInstanceId, "ItemA", JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "ItemA.amount > 100" };

            var result = await ContextFor(caseInstanceId, "ItemA").EvaluateAsBool(null, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // The same caseFileModel binding under the generic "value" alias, so one syntax works
        // whether contextRef names the item or is omitted.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_NullContextRef__Then_BindsCaseFileModelUnderValueAlias
            (Guid caseInstanceId)
        {
            await CreateCaseFileItem(caseInstanceId, "ItemA", JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.ItemA.amount > 100" };

            var result = await ContextFor(caseInstanceId, "ItemA").EvaluateAsBool(null, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // I4's related note from the adversarial review - "the expression context binds exactly one
        // CFI; a condition over two CaseFileItems is inexpressible" - closed for the
        // contextRef-absent case.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_NullContextRef__Then_MultiItemConditionIsExpressible
            (Guid caseInstanceId)
        {
            await CreateCaseFileItem(caseInstanceId, "ItemA", JsonNode.Parse("""{"amount": 150}"""));
            await CreateCaseFileItem(caseInstanceId, "ItemB", JsonNode.Parse("""{"limit": 100}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "ItemA.amount > ItemB.limit" };

            var result = await ContextFor(caseInstanceId, "ItemA", "ItemB").EvaluateAsBool(null, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // The documented asymmetry, pinned. With contextRef ABSENT a reference to an item that was
        // never created yields undefined and the expression's own truthiness rules apply - it does
        // NOT fail closed the way an explicit contextRef to a missing item does (compare
        // EvaluateAsBool__Given_ContextRefToNeverCreatedCaseFileItem__Then_FailureResult above).
        // The caseFileModel binding legitimately contains only the items that exist, because
        // case-file items are created ad hoc (#16) rather than instantiated from the model.
        [Theory, AutoData]
        public async Task EvaluateAsBool__Given_NullContextRef__When_DeclaredItemNeverCreated__Then_UndefinedNotFailure
            (Guid caseInstanceId)
        {
            await CreateCaseFileItem(caseInstanceId, "ItemA", JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "typeof ItemB === 'undefined'" };

            var result = await ContextFor(caseInstanceId, "ItemA", "ItemB").EvaluateAsBool(null, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        private async Task CreateCaseFileItem(Guid caseInstanceId, string caseFileItemId, JsonNode value) =>
            await _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId)
                .Create($"case-{caseInstanceId}", new CaseFileItemModel { Id = caseFileItemId }, value);
    }
}
