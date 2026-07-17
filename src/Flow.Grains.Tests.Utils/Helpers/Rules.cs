using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Tests.Utils.Helpers
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
    }
}
