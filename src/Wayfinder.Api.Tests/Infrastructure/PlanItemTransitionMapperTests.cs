using System;
using Wayfinder.Api.Infrastructure;
using FluentAssertions;
using Xunit;
using ContractsTransition = Wayfinder.Contracts.V1.PlanItemTransition;
using DomainTransition = Wayfinder.Grains.Interfaces.Model.PlanItemTransition;

namespace Wayfinder.Api.Tests.Infrastructure
{
    public class PlanItemTransitionMapperTests
    {
        [Theory]
        [InlineData(ContractsTransition.Close, DomainTransition.Close)]
        [InlineData(ContractsTransition.Complete, DomainTransition.Complete)]
        [InlineData(ContractsTransition.Create, DomainTransition.Create)]
        [InlineData(ContractsTransition.Disable, DomainTransition.Disable)]
        [InlineData(ContractsTransition.Enable, DomainTransition.Enable)]
        [InlineData(ContractsTransition.Exit, DomainTransition.Exit)]
        [InlineData(ContractsTransition.Fault, DomainTransition.Fault)]
        [InlineData(ContractsTransition.ManualStart, DomainTransition.ManualStart)]
        [InlineData(ContractsTransition.Occur, DomainTransition.Occur)]
        [InlineData(ContractsTransition.ParentResume, DomainTransition.ParentResume)]
        [InlineData(ContractsTransition.ParentSuspend, DomainTransition.ParentSuspend)]
        [InlineData(ContractsTransition.Reactivate, DomainTransition.Reactivate)]
        [InlineData(ContractsTransition.Reenable, DomainTransition.Reenable)]
        [InlineData(ContractsTransition.Resume, DomainTransition.Resume)]
        [InlineData(ContractsTransition.Start, DomainTransition.Start)]
        [InlineData(ContractsTransition.Suspend, DomainTransition.Suspend)]
        [InlineData(ContractsTransition.Terminate, DomainTransition.Terminate)]
        [InlineData(ContractsTransition.ParentTerminate, DomainTransition.ParentTerminate)]
        public void ToDomain_Given_EveryWireTransition_Then_MapsToTheIdenticallyNamedDomainMember(
            ContractsTransition wire, DomainTransition expected)
        {
            PlanItemTransitionMapper.ToDomain(wire).Should().Be(expected);
        }

        [Fact]
        public void ToDomain_Given_UnmappedValue_Then_Throws()
        {
            var action = () => PlanItemTransitionMapper.ToDomain((ContractsTransition)(-1));

            action.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
