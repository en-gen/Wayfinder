using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.Role;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.Case
{
    public class CaseGrain :
        CmmnElementGrain<CaseStore, Interfaces.Model.Case>,
        ICaseGrain,
        IBehaviorHost
    {
        private IPlanItemBehaviorConfigurator BehaviorConfigurator { get; }
        private IMapper Mapper { get; }

        private IPlanItemBehavior _casePlanModel;

        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Address => _address;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.ParentInstanceId => _parentId;
        string IBehaviorHost.InstanceId => _instanceId;
        string IBehaviorHost.DefinitionId => Definition?.Id;

        IBehaviorDefinition IBehaviorHost.Definition => Definition;
        IBehaviorStore IBehaviorHost.State => TentativeState;
        IGrainFactory IBehaviorHost.GrainFactory => GrainFactory;

        void IBehaviorHost.RaiseEvent<TEvent>(TEvent @event) => RaiseEvent(@event);
        Task IBehaviorHost.ConfirmEvents() => ConfirmEvents();

        Task IBehaviorHost.SubscribeTo<TEvent>(
            string eventSourceId,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags) => SubscribeTo(eventSourceId, eventHandler, flags);
        Task IBehaviorHost.UnsubscribeFrom<TEvent>(string eventSourceId) => UnsubscribeFrom<TEvent>(eventSourceId);
        Task IBehaviorHost.Publish<TEvent>(TEvent @event) => PublishEvent(@event);

        IDictionary<string, object> IBehaviorHost.Context => LogContext;
        void IBehaviorHost.LogWithContext(Action<ILogger> logAction) => LogWithContext(logAction);

        #endregion

        public CaseGrain(
            IPlanItemBehaviorConfigurator behaviorConfigurator,
            IMapper mapper,
            ILogger<CaseGrain> logger) :
            base(logger)
        {
            BehaviorConfigurator = behaviorConfigurator;
            Mapper = mapper;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);

            if (State.Defined)
            {
                await PostDefine();
            }
        }

        public async Task Create(string caseDefinitionId)
        {
            var caseDefinitionGrain = GrainFactory.GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId);

            if(!await caseDefinitionGrain.Defined()) throw new InvalidOperationException($"case definition {caseDefinitionId} not found");
            if(State.Defined) throw new InvalidOperationException($"case {_caseInstanceId} has already been created");

            var @case = await caseDefinitionGrain.GetDefinition();
            @case.CasePlanModel.IsCasePlanModel = true;

            RaiseEvent(new CaseCreated
            {
                CaseDefinitionId = @case.Id,
                Definition = @case
            });

            await Task.WhenAll(@case.CaseRoles.Roles
                .Select(x => GrainFactory.GetGrain<IRoleGrain>(_caseInstanceId, x.Id)
                    .Define(caseDefinitionId, x)));
            
            await PostDefine();
        }

        private async Task PostDefine()
        {
            LogContext["CaseDefinitionId"] = State.CaseDefinitionId;
            LogContext["ElementDefinitionId"] = Definition.Id;
            LogContext["ElementScope"] = _scope;
            LogContext["ElementInstanceId"] = _instanceId;

            _casePlanModel = await BehaviorConfigurator.Configure(this, State.Definition.CasePlanModel);
        }

        public Task<CaseSnapshot> GetSnapshot() => Task.FromResult(Mapper.Map<CaseSnapshot>(State));

        public async Task<CaseSnapshot> Trigger(PlanItemTransition transition)
        {
            if (_casePlanModel == null) throw new InvalidOperationException("attempted Trigger on Uninitialized Case");

            await _casePlanModel.Trigger(transition);
            return await GetSnapshot();
        }
    }
}
