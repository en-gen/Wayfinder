using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseFileItemModel = Wayfinder.Grains.Interfaces.Model.CaseFileItem;

namespace Wayfinder.Grains.Tests.Integration.Expressions
{
    // D1 - Expression context binder
    // ~~~~~
    // ExpressionGrain.BuildExecutable is the single chokepoint every IfPart/rule/applicability
    // evaluation call site funnels through (SentryGrain.EvaluateIfPart, BaseBehavior.EvaluateRule,
    // PlanningTableGrain.EvaluateApplicabilityRule - see ExpressionGrain's class remarks). These
    // tests exercise the binder directly rather than only through a Sentry, so the contextRef
    // resolution/binding/failure behavior is pinned independent of any one caller's plumbing.
    [Collection(ClusterCollection.Name)]
    public class ExpressionGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public ExpressionGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // 5.4.6.4 IfPart / this work item's identifier contract: the bound CaseFileItem value is
        // exposed under the spec-agnostic "value" alias.
        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_ContextRefWithObjectValue__Then_ConditionSeesValueByGenericAlias
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        // Same binding, but the condition references the contextRef's own id directly - the
        // spec-natural alias (the identifier the modeler used to declare the context) rather than
        // the generic "value" alias. Both aliases refer to the same bound CaseFileItem value.
        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_ContextRefWithObjectValue__Then_ConditionSeesValueByContextRefAlias
            (Guid caseInstanceId)
        {
            const string caseFileItemId = "TheCaseFileItem";
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "TheCaseFileItem.amount > 100" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_ContextRefWithObjectValue__When_ConditionFalse__Then_FalseResult
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 50}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeFalse();
        }

        // A CaseFileItem's Value is a bare JsonNode of any shape (5.3.2) - not necessarily an
        // object. Binding must not assume object-shapedness (see IExecutable.WithJsonArgument's
        // remarks on why WithArgument/WithContext could not be reused as-is for this).
        [Theory, AutoData]
        public async Task ExecuteAsString__Given_ContextRefWithScalarValue__Then_ConditionSeesScalar
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonValue.Create("filed"));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsString(caseFileItemId, expression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().Be("filed");
        }

        // Deliberate simplification (documented in ExpressionGrain.BuildExecutable): a contextRef
        // naming a CaseFileItem that was never created is treated as an expression failure rather
        // than binding a JS null and letting a member-access expression throw its own TypeError.
        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_ContextRefToNeverCreatedCaseFileItem__Then_FailureResult
            (Guid caseInstanceId, string neverCreatedCaseFileItemId)
        {
            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount > 100" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(neverCreatedCaseFileItemId, expression);

            result.IsError.Should().BeTrue();
            result.Message.Should().Contain(neverCreatedCaseFileItemId);
        }

        // A hostile or broken expression must fail, not throw out of the grain call and not hang -
        // pins the sandbox budget (SandboxedJintEngine) is still in effect once context is bound.
        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_ContextRefBound__When_ExpressionThrows__Then_FailureResult
            (Guid caseInstanceId, string caseFileItemId)
        {
            await CreateCaseFileItem(caseInstanceId, caseFileItemId, JsonNode.Parse("""{"amount": 150}"""));

            var expression = new Expression { Language = ExpressionLanguage.Jint, Body = "value.amount.nonExistent.deeper" };

            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(caseFileItemId, expression);

            result.IsError.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task ExecuteAsBool__Given_NullContextRef__Then_EvaluatesWithoutBinding
            (Guid caseInstanceId)
        {
            var result = await _clusterClient.GetGrain<IExpressionGrain>(caseInstanceId)
                .ExecuteAsBool(null, Rules.TruthyExpression);

            result.IsError.Should().BeFalse(result.Message);
            result.Value.Should().BeTrue();
        }

        private async Task CreateCaseFileItem(Guid caseInstanceId, string caseFileItemId, JsonNode value) =>
            await _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId)
                .Create($"case-{caseInstanceId}", new CaseFileItemModel { Id = caseFileItemId }, value);
    }
}
