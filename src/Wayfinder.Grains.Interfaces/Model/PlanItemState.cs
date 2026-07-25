using System.Collections.Generic;

namespace Flow.Grains.Interfaces.Model
{
    public enum PlanItemState
    {
        Uninitialized,

        Available,
        Enabled,
        Disabled,
        Active,
        Suspended,
        Failed,
        Completed,
        Closed,
        Terminated
    }

    public static class PlanItemStateExtensions
    {
        private static readonly ISet<PlanItemState> TerminalStates = new HashSet<PlanItemState>
        {
            PlanItemState.Disabled,
            PlanItemState.Completed,
            PlanItemState.Terminated,
            PlanItemState.Failed
        };

        public static bool IsTerminal(this PlanItemState state) => TerminalStates.Contains(state);
    }
}
