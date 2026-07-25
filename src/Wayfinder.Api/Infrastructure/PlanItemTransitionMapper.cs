using System;
using ContractsTransition = Wayfinder.Contracts.V1.PlanItemTransition;
using DomainTransition = Wayfinder.Grains.Interfaces.Model.PlanItemTransition;

namespace Wayfinder.Api.Infrastructure
{
    // ADO #32 - the transport-edge translation TriggerCaseCommand's remarks call for: a
    // TriggerCaseRequest's V1 (wire) transition becomes the domain enum the command carries. An
    // explicit switch (not a blind cast) - the two enums are a deliberate 1:1 mirror today (see
    // Wayfinder.Contracts.V1.PlanItemTransition's remarks), but a cast would silently keep "working" even
    // if they ever drifted out of lockstep, hiding exactly the bug this mapper exists to catch.
    public static class PlanItemTransitionMapper
    {
        public static DomainTransition ToDomain(ContractsTransition transition) => transition switch
        {
            ContractsTransition.Close => DomainTransition.Close,
            ContractsTransition.Complete => DomainTransition.Complete,
            ContractsTransition.Create => DomainTransition.Create,
            ContractsTransition.Disable => DomainTransition.Disable,
            ContractsTransition.Enable => DomainTransition.Enable,
            ContractsTransition.Exit => DomainTransition.Exit,
            ContractsTransition.Fault => DomainTransition.Fault,
            ContractsTransition.ManualStart => DomainTransition.ManualStart,
            ContractsTransition.Occur => DomainTransition.Occur,
            ContractsTransition.ParentResume => DomainTransition.ParentResume,
            ContractsTransition.ParentSuspend => DomainTransition.ParentSuspend,
            ContractsTransition.Reactivate => DomainTransition.Reactivate,
            ContractsTransition.Reenable => DomainTransition.Reenable,
            ContractsTransition.Resume => DomainTransition.Resume,
            ContractsTransition.Start => DomainTransition.Start,
            ContractsTransition.Suspend => DomainTransition.Suspend,
            ContractsTransition.Terminate => DomainTransition.Terminate,
            ContractsTransition.ParentTerminate => DomainTransition.ParentTerminate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(transition), transition, $"Unmapped {nameof(ContractsTransition)}"),
        };
    }
}
