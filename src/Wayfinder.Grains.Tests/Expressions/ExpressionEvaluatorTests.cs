using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Wayfinder.Grains.Tests.Expressions
{
    // Unit tests for ExpressionEvaluator (design 05 section D.2, finding I4).
    // ~~~~~
    // These exist as UNIT tests specifically because P1 made them possible. The predecessor
    // (ExpressionGrain) was a [StatelessWorker] grain, so every behavioural test of it had to stand
    // up an Orleans TestCluster - which is why ExpressionEvaluationTests lives in the integration
    // project and why several branches below were only reachable, if at all, through a whole case
    // instance. The evaluator is now a plain synchronous class with two constructor dependencies,
    // so its binding rules can be pinned directly, in milliseconds, at the level they are written.
    //
    // Deliberately built on the REAL Jint stack (SandboxedJintEngine + Executable), not a mocked
    // IExecutable. The thing under test is almost entirely about what gets BOUND and under which
    // names - a mock recording WithJsonArgument calls would assert that the code calls itself the
    // way it calls itself, and would pass just as happily if the bound object were unreadable from
    // JavaScript. Evaluating a real expression against a real engine is what makes these tests
    // capable of failing for a real reason.
    public class ExpressionEvaluatorTests
    {
        private static ExpressionEvaluator CreateEvaluator() =>
            new ExpressionEvaluator(
                body => new Executable(
                    SandboxedJintEngine.Create(),
                    NullLogger<Executable>.Instance,
                    body),
                NullLogger<ExpressionEvaluator>.Instance);

        private static ExpressionRequest Request(
            string body,
            string contextRef = null,
            JsonNode contextValue = null,
            IReadOnlyDictionary<string, JsonNode> caseFileModel = null,
            bool contextMissing = false,
            Guid caseInstanceId = default) =>
            new ExpressionRequest
            {
                Expression = new Expression { Body = body },
                ContextRef = contextRef,
                ContextValue = contextValue,
                CaseFileModel = caseFileModel,
                ContextMissing = contextMissing,
                CaseInstanceId = caseInstanceId
            };

        // ---------------------------------------------------------------- constructor guards

        [Fact]
        public void Constructor__Given_NullExecutableFactory__Throws()
        {
            var act = () => new ExpressionEvaluator(null, NullLogger<ExpressionEvaluator>.Instance);

            act.Should().Throw<ArgumentNullException>().WithParameterName("executable");
        }

        [Fact]
        public void Constructor__Given_NullLogger__Throws()
        {
            var act = () => new ExpressionEvaluator(_ => null, null);

            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Fact]
        public void ExecuteAsBool__Given_NullRequest__Throws()
        {
            var act = () => CreateEvaluator().ExecuteAsBool(null);

            act.Should().Throw<ArgumentNullException>().WithParameterName("request");
        }

        // ---------------------------------------------------------------- contextRef PRESENT

        // The binding contract carried over from ExpressionGrain unchanged: the value is exposed
        // under BOTH the spec-agnostic "value" alias and the contextRef's own id, so a model may
        // use either without both having to be documented per-model.
        [Theory]
        [InlineData("value.amount > 100")]
        [InlineData("StatusItem.amount > 100")]
        public void ExecuteAsBool__Given_ContextRef__BindsUnderBothNames(string body)
        {
            var result = CreateEvaluator().ExecuteAsBool(
                Request(body, contextRef: "StatusItem", contextValue: JsonNode.Parse("""{"amount":150}""")));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        // Fails CLOSED, and the message carries both the contextRef and the case id - an existing
        // integration test asserts on that text, which is why ExpressionRequest carries
        // CaseInstanceId for diagnostics at all.
        [Fact]
        public void ExecuteAsBool__Given_ContextMissing__FailsClosedWithActionableMessage()
        {
            var caseInstanceId = Guid.NewGuid();

            var result = CreateEvaluator().ExecuteAsBool(
                Request("value.amount > 100", contextRef: "Missing", contextMissing: true, caseInstanceId: caseInstanceId));

            result.IsError.Should().BeTrue();
            result.Value.Should().BeFalse();
            result.Message.Should().Contain("Missing").And.Contain(caseInstanceId.ToString());
        }

        // ---------------------------------------------------------------- contextRef ABSENT (I4)

        // I4, the whole point of the change: Table 5.32 / Table 5.54 say that when contextRef is
        // not specified, "evaluation starts at the CaseFile object ... as its caseFileModel".
        // Before P1 a null contextRef bound NOTHING, so this expression threw a Jint ReferenceError
        // and the sentry could never satisfy.
        [Theory]
        [InlineData("value.ItemA.amount > 100")]
        [InlineData("ItemA.amount > 100")]
        public void ExecuteAsBool__Given_NoContextRef__BindsCaseFileModelUnderBothShapes(string body)
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = JsonNode.Parse("""{"amount":150}""")
            };

            var result = CreateEvaluator().ExecuteAsBool(Request(body, caseFileModel: model));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        // I4's related note, closed: a condition spanning two CaseFileItems was previously
        // inexpressible because the context bound exactly one item.
        [Fact]
        public void ExecuteAsBool__Given_NoContextRef__MakesMultiItemConditionsExpressible()
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = JsonNode.Parse("""{"amount":150}"""),
                ["ItemB"] = JsonNode.Parse("""{"limit":100}""")
            };

            var result = CreateEvaluator().ExecuteAsBool(Request("ItemA.amount > ItemB.limit", caseFileModel: model));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        // The DELIBERATE asymmetry with the contextRef-present branch, pinned so it cannot drift
        // silently (design 05 section D.2): with contextRef ABSENT, a reference to an item that does
        // not exist yields undefined and the expression's own truthiness rules apply - it does NOT
        // fail closed. The caseFileModel legitimately contains only the items that exist, because
        // case-file items are created ad hoc (#16) rather than enumerated from the model.
        [Fact]
        public void ExecuteAsBool__Given_NoContextRefAndUnknownItem__YieldsFalseRatherThanFailingClosed()
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = JsonNode.Parse("""{"amount":150}""")
            };

            var result = CreateEvaluator().ExecuteAsBool(Request("typeof ItemZ === 'undefined'", caseFileModel: model));

            result.IsError.Should().BeFalse("an absent contextRef must not fail closed - that is the contextRef-PRESENT behaviour");
            result.Value.Should().BeTrue();
        }

        // Both empty shapes bind nothing at all, which preserves the pre-P1 behaviour exactly for
        // every expression that never referenced the case file - notably TimerEventListenerBehavior's
        // timerExpression, which passes a null contextRef because a TimerExpression has no
        // contextRef attribute at all. Null and empty are separate branches in BindCaseFileModel.
        [Fact]
        public void ExecuteAsBool__Given_NullCaseFileModel__BindsNothingAndStillEvaluates()
        {
            var result = CreateEvaluator().ExecuteAsBool(Request("1 + 1 > 1", caseFileModel: null));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        [Fact]
        public void ExecuteAsBool__Given_EmptyCaseFileModel__BindsNothingAndStillEvaluates()
        {
            var result = CreateEvaluator().ExecuteAsBool(
                Request("1 + 1 > 1", caseFileModel: new Dictionary<string, JsonNode>()));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        [Fact]
        public void ExecuteAsBool__Given_CaseFileModel__BindsAggregateAndPerItemNamesTogether()
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = JsonNode.Parse("""{"amount":150}""")
            };

            var result = CreateEvaluator().ExecuteAsBool(
                Request("value.ItemA.amount === 150 && ItemA.amount === 150", caseFileModel: model));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        // WHY THE DeepClone IN BindCaseFileModel IS LOAD-BEARING, stated precisely, because the
        // obvious reading is wrong and a single-evaluation test does NOT catch its removal.
        //
        // A JsonNode may have only one parent. BindCaseFileModel assigns into the "value" aggregate
        // and then binds the caller's original node under the item's own id. Within ONE call that
        // ordering is harmless even without the clone - the aggregate takes ownership first and the
        // per-item bind only reads the node - which is why removing `.DeepClone()` leaves every
        // single-evaluation assertion green. Verified by mutation, not assumed.
        //
        // The real failure is ACROSS calls, and it is the case that actually happens: the case grain
        // holds its caseFileModel and evaluates many expressions against it per turn (section D.6.2
        // puts the worst case at ~3N evaluations). Without the clone, the first evaluation re-parents
        // the caller's nodes into its aggregate; the second evaluation's aggregate assignment then
        // throws InvalidOperationException on a node that already has a parent, and the exception
        // escapes Bind rather than degrading to a Failure. Evaluating TWICE against the same
        // dictionary is what pins it.
        [Fact]
        public void ExecuteAsBool__Given_TheSameCaseFileModelEvaluatedTwice__DoesNotReParentTheCallersNodes()
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = JsonNode.Parse("""{"amount":150}""")
            };
            var evaluator = CreateEvaluator();

            var first = evaluator.ExecuteAsBool(Request("ItemA.amount > 100", caseFileModel: model));
            var second = () => evaluator.ExecuteAsBool(Request("ItemA.amount > 100", caseFileModel: model));

            first.Value.Should().BeTrue();
            second.Should().NotThrow(
                "BindCaseFileModel must DeepClone into its aggregate - a JsonNode may have only one " +
                "parent, and the case grain re-evaluates against the same caseFileModel many times per turn");
            second().Value.Should().BeTrue();
        }

        // A modelled item with no value yet. The null-conditional in `item.Value?.DeepClone()` is
        // load-bearing: without it the aggregate throws and every expression in the case fails.
        [Fact]
        public void ExecuteAsBool__Given_CaseFileModelWithNullValue__BindsNullWithoutThrowing()
        {
            var model = new Dictionary<string, JsonNode>
            {
                ["ItemA"] = null
            };

            var result = CreateEvaluator().ExecuteAsBool(Request("value.ItemA === null", caseFileModel: model));

            result.IsError.Should().BeFalse();
            result.Value.Should().BeTrue();
        }

        // ---------------------------------------------------------------- ExecuteAsString

        [Fact]
        public void ExecuteAsString__Given_BoundContext__ReturnsTheString()
        {
            var result = CreateEvaluator().ExecuteAsString(
                Request("value.name", contextRef: "Item", contextValue: JsonNode.Parse("""{"name":"hello"}""")));

            result.IsError.Should().BeFalse();
            result.Value.Should().Be("hello");
        }

        [Fact]
        public void ExecuteAsString__Given_ContextMissing__FoldsBindFailureIntoResult()
        {
            var result = CreateEvaluator().ExecuteAsString(
                Request("value.name", contextRef: "Missing", contextMissing: true));

            result.IsError.Should().BeTrue();
            result.Message.Should().Contain("Missing");
        }

        // ---------------------------------------------------------------- ExecuteAsIso8601

        [Fact]
        public void ExecuteAsIso8601__Given_ValidDuration__Parses()
        {
            var result = CreateEvaluator().ExecuteAsIso8601(Request("'PT15M'"));

            result.IsError.Should().BeFalse();
            result.Value.Should().NotBeNull();
        }

        // The bind-failure path, distinct from the parse-failure path below.
        [Fact]
        public void ExecuteAsIso8601__Given_ContextMissing__FoldsBindFailureIntoResult()
        {
            var result = CreateEvaluator().ExecuteAsIso8601(
                Request("value.when", contextRef: "Missing", contextMissing: true));

            result.IsError.Should().BeTrue();
            result.Message.Should().Contain("Missing");
        }

        // The expression itself throws - so ExecuteAsString fails before any ISO-8601 parse is
        // attempted. A separate branch from "the string parsed badly".
        [Fact]
        public void ExecuteAsIso8601__Given_ThrowingExpression__ReturnsFailureWithoutParsing()
        {
            var result = CreateEvaluator().ExecuteAsIso8601(Request("(void 0).nope"));

            result.IsError.Should().BeTrue();
        }

        // The expression evaluates fine and yields a string that is not an ISO-8601 value: the
        // catch around `new Iso8601(...)` is what stops that surfacing as an unhandled exception
        // out of a behaviour's rule evaluation.
        [Fact]
        public void ExecuteAsIso8601__Given_UnparseableResult__ReturnsFailureRatherThanThrowing()
        {
            var act = () => CreateEvaluator().ExecuteAsIso8601(Request("'not-a-duration'"));

            act.Should().NotThrow();
            act().IsError.Should().BeTrue();
        }
    }
}
