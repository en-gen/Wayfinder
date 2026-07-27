using System;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Tests.Utils.Helpers
{
    public static class Rules
    {
        public static readonly Expression TruthyExpression = new Expression
        {
            Id = Guid.NewGuid().ToString(),
            Language = ExpressionLanguage.Jint,
            Body = "true"
        };

        public static readonly Expression FalsyExpression = new Expression
        {
            Id = Guid.NewGuid().ToString(),
            Language = ExpressionLanguage.Jint,
            Body = "false"
        };

        // Distinct Id/Body from the other fixtures purely so Moq setups keyed on
        // (ContextRef, Condition) equality can't accidentally match a different rule's
        // expression - the Body itself is never actually executed by unit tests using this
        // fixture, which stub IExpressionGrain.ExecuteAsBool directly with a Failure(...) result.
        public static readonly Expression ErroringExpression = new Expression
        {
            Id = Guid.NewGuid().ToString(),
            Language = ExpressionLanguage.Jint,
            Body = "undefined.explode()"
        };

        public static readonly RequiredRule IsRequiredRule = new RequiredRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(IsRequiredRule)}",
            Condition = TruthyExpression
        };

        public static readonly RequiredRule NotRequiredRule = new RequiredRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(NotRequiredRule)}",
            Condition = FalsyExpression
        };

        public static readonly RequiredRule ErroringRequiredRule = new RequiredRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(ErroringRequiredRule)}",
            Condition = ErroringExpression
        };

        public static readonly RepetitionRule IsRepeatableRule = new RepetitionRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(IsRepeatableRule)}",
            Condition = TruthyExpression
        };

        public static readonly RepetitionRule NotRepeatableRule = new RepetitionRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(NotRepeatableRule)}",
            Condition = FalsyExpression
        };

        public static readonly ManualActivationRule IsManuallyActivated = new ManualActivationRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(IsManuallyActivated)}",
            Condition = TruthyExpression
        };

        public static readonly ManualActivationRule NotManuallyActivated = new ManualActivationRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(NotManuallyActivated)}",
            Condition = FalsyExpression
        };

        public static readonly ManualActivationRule ErroringManualActivationRule = new ManualActivationRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(ErroringManualActivationRule)}",
            Condition = ErroringExpression
        };

        public static readonly ApplicabilityRule IsApplicable = new ApplicabilityRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(IsApplicable)}",
            Condition = TruthyExpression
        };

        public static readonly ApplicabilityRule NotApplicable = new ApplicabilityRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(NotApplicable)}",
            Condition = FalsyExpression
        };

        public static readonly ApplicabilityRule ErroringApplicabilityRule = new ApplicabilityRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{nameof(ErroringApplicabilityRule)}",
            Condition = ErroringExpression
        };
    }
}
