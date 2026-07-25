using System;
using Wayfinder.Contracts.V1;
using Wayfinder.Grains.Interfaces.Plan.CaseFileItem;
using ContractActorPrincipalType = Wayfinder.Contracts.V1.ActorPrincipalType;
using ContractTransition = Wayfinder.Contracts.V1.CaseFileItemTransition;
using DomainActorPrincipalType = Wayfinder.Grains.Interfaces.ActorPrincipalType;
using DomainTransition = Wayfinder.Grains.Interfaces.Model.CaseFileItemTransition;

namespace Wayfinder.Application.CaseFileItems
{
    // ADO #58 - projects the domain CaseFileItemVersionDescriptor down to the wire-shaped
    // Wayfinder.Contracts.V1.CaseFileItemVersionView, mirroring CaseViewProjector's own explicit-switch
    // anti-corruption pattern (not a same-ordinal cast) for every domain<->contract enum mapping in
    // this codebase.
    internal static class CaseFileItemHistoryProjector
    {
        public static CaseFileItemVersionView ToView(CaseFileItemVersionDescriptor descriptor) =>
            new()
            {
                Version = descriptor.Version,
                UpdatedUtc = descriptor.UpdatedUtc,
                ActorPrincipalId = descriptor.ActorPrincipalId,
                ActorPrincipalType = ToContract(descriptor.ActorPrincipalType),
                ActorOnBehalfOf = descriptor.ActorOnBehalfOf,
                Transition = descriptor.Transition.HasValue ? ToContract(descriptor.Transition.Value) : null
            };

        private static ContractActorPrincipalType ToContract(DomainActorPrincipalType type) => type switch
        {
            DomainActorPrincipalType.User => ContractActorPrincipalType.User,
            DomainActorPrincipalType.Client => ContractActorPrincipalType.Client,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, $"Unmapped {nameof(DomainActorPrincipalType)} value.")
        };

        private static ContractTransition ToContract(DomainTransition transition) => transition switch
        {
            DomainTransition.AddChild => ContractTransition.AddChild,
            DomainTransition.AddReference => ContractTransition.AddReference,
            DomainTransition.Create => ContractTransition.Create,
            DomainTransition.Delete => ContractTransition.Delete,
            DomainTransition.RemoveChild => ContractTransition.RemoveChild,
            DomainTransition.RemoveReference => ContractTransition.RemoveReference,
            DomainTransition.Replace => ContractTransition.Replace,
            DomainTransition.Update => ContractTransition.Update,
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, $"Unmapped {nameof(DomainTransition)} value.")
        };
    }
}
