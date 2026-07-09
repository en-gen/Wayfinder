using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Mapping;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Plan.CmmnElement;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Plan.CaseFileItem
{
    // 8.3 - CaseFileItem Lifecycle
    // ~~~~~
    // "Most of the execution semantics is described by the lifecycle of important CMMNElement
    // instances. In particular the lifecycle for Task, Stage, Milestone, EventListener, and
    // CaseFileItem instances describe the majority of the execution semantics." This grain is the
    // CaseFileItem half of that statement - PlanItem-family elements already had a runtime (see
    // PlanItemGrain/SentryGrain); CaseFileItem instances did not (deviation D2), so
    // SentryGrain.HandleCaseFileItemTransitioned and TimerEventListenerBehavior's
    // CaseFileItemStartTrigger subscription both existed already but could never fire - nothing
    // published CaseFileItemTransitionedEvent. This grain is that publisher.
    //
    // Every mutating operation below publishes CaseFileItemTransitionedEvent via the inherited
    // CmmnElementGrain.PublishEvent, exactly as PlanItemGrain's behaviors do for
    // PlanItemTransitionedEvent (see BaseBehavior.HandleTransitioned). PublishEvent keys the
    // stream on Definition.Id, which is exactly the CaseFileItem model element's spec id - the
    // same id that CaseFileItemOnPart.SourceRef and CaseFileItemStartTrigger.SourceRef reference
    // as an IDREF (see Spec.CMMN.MODEL.cs). No new stream-key plumbing was needed: reusing the
    // inherited publish path is what makes SentryGrain's and TimerEventListenerBehavior's existing
    // (already-subscribing) handlers receive these events for the first time.
    //
    // Note SentryGrain.HandleCaseFileItemTransitioned matches purely on
    // "x.SourceRef.Equals(@event.SourceDefinitionId)" - unlike HandlePlanItemTransitioned, it does
    // NOT additionally filter by @event.SourceScope. This reflects 5.3.1: a Case has exactly one
    // CaseFile, so CaseFileItem identity is case-global rather than nested under a plan-item
    // scope - this grain's address is namespaced under a fixed "casefile" scope (see
    // CaseFileItemGrainFactoryExtensions) rather than under any particular Stage/PlanItem address.
    public class CaseFileItemGrain :
        CmmnElementGrain<CaseFileItemStore, Interfaces.Model.CaseFileItem>,
        ICaseFileItemGrain
    {
        public CaseFileItemGrain(ILogger<CaseFileItemGrain> logger) :
            base(logger)
        {
        }

        // Table 8.2: create (Ø -> Available). Define() (CmmnElementGrain's Define, called via
        // base.Define below) raises CmmnElementDefined<CaseFileItem> which is this store's "Ø"
        // entry point - CaseFileItemStore.CaseFileItemState already defaults to Available, so no
        // additional state-transition event is raised here, only the initial Value.
        public async Task Create(string caseDefinitionId, Interfaces.Model.CaseFileItem definition, JsonNode value)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (State.Defined) throw new InvalidOperationException($"CaseFileItem {_address} has already been created");

            await base.Define(caseDefinitionId, definition);

            RaiseEvent(new ValueChanged
            {
                Value = value
            });

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.Create);
        }

        public override Task Define(string caseDefinitionId, Interfaces.Model.CaseFileItem definition) =>
            throw new InvalidOperationException($"{nameof(CaseFileItemGrain)} must be created via {nameof(Create)}, which also establishes the initial Value");

        public Task Update(JsonNode value) =>
            ChangeValue(value, Interfaces.Model.CaseFileItemTransition.Update);

        public Task Replace(JsonNode value) =>
            ChangeValue(value, Interfaces.Model.CaseFileItemTransition.Replace);

        private async Task ChangeValue(JsonNode value, Interfaces.Model.CaseFileItemTransition standardEvent)
        {
            EnsureAvailable();

            RaiseEvent(new ValueChanged
            {
                Value = value
            });

            await ConfirmEvents();

            await PublishTransition(standardEvent);
        }

        public async Task AddChild(string childCaseFileItemId)
        {
            if (string.IsNullOrWhiteSpace(childCaseFileItemId)) throw new ArgumentException("must not be empty", nameof(childCaseFileItemId));
            EnsureAvailable();

            RaiseEvent(new ChildAdded
            {
                ChildCaseFileItemId = childCaseFileItemId
            });

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.AddChild);
        }

        public async Task RemoveChild(string childCaseFileItemId)
        {
            if (string.IsNullOrWhiteSpace(childCaseFileItemId)) throw new ArgumentException("must not be empty", nameof(childCaseFileItemId));
            EnsureAvailable();

            RaiseEvent(new ChildRemoved
            {
                ChildCaseFileItemId = childCaseFileItemId
            });

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.RemoveChild);
        }

        public async Task AddReference(string targetCaseFileItemId)
        {
            if (string.IsNullOrWhiteSpace(targetCaseFileItemId)) throw new ArgumentException("must not be empty", nameof(targetCaseFileItemId));
            EnsureAvailable();

            RaiseEvent(new ReferenceAdded
            {
                TargetCaseFileItemId = targetCaseFileItemId
            });

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.AddReference);
        }

        public async Task RemoveReference(string targetCaseFileItemId)
        {
            if (string.IsNullOrWhiteSpace(targetCaseFileItemId)) throw new ArgumentException("must not be empty", nameof(targetCaseFileItemId));
            EnsureAvailable();

            RaiseEvent(new ReferenceRemoved
            {
                TargetCaseFileItemId = targetCaseFileItemId
            });

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.RemoveReference);
        }

        // Table 8.2: delete (Available -> Discarded). Terminal.
        public async Task Delete()
        {
            EnsureAvailable();

            RaiseEvent(new Discarded());

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.Delete);
        }

        public Task<CaseFileItemSnapshot> GetSnapshot() => Task.FromResult(State.ToSnapshot());

        // Table 8.1/8.2: every mutating operation other than create is only defined From Available;
        // Discarded is a terminal state with no outgoing transitions ("A CaseFileItem instance in
        // this state is considered deleted and is not available to Case workers or expressions").
        private void EnsureAvailable()
        {
            if (!State.Defined) throw new InvalidOperationException($"CaseFileItem {_address} has not been created");

            if (State.CaseFileItemState == Interfaces.Plan.CaseFileItem.CaseFileItemState.Discarded)
            {
                throw new InvalidOperationException($"CaseFileItem {_address} is Discarded and accepts no further operations");
            }
        }

        private Task PublishTransition(Interfaces.Model.CaseFileItemTransition standardEvent) =>
            PublishEvent(new CaseFileItemTransitionedEvent(_scope, Definition.Id, standardEvent));
    }
}
