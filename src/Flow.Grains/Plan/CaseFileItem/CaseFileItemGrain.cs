using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Mapping;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Plan.CmmnElement;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

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
    // D3 addendum: PublishTransition below ALSO publishes every transition to a second, case-wide
    // stream (CaseFileItemAddress.CaseWideSentinel), for standalone-IfPart Sentries (0 OnParts) to
    // subscribe to - see CaseFileItemAddress.CaseWideSentinel's and SentryGrain's remarks.
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
        // Resolved lazily (see EnsureCaseNotClosed) and cached for the lifetime of this
        // activation: the scope needed to address this CaseFileItem's owning ICaseGrain (its
        // CasePlanModel.Id, per the convention every existing caller of GetGrain<ICaseGrain>
        // already follows - see e.g. CaseLifecycleIntegrationTests/ConformanceHarness) is not
        // otherwise derivable from this grain's own compound key, but does not change once the
        // owning case is defined.
        private string _caseScope;

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

            // Table 8.2 lists create as a CaseFileItem transition like any other - 8.4.1/Table
            // 8.5's Closed lockdown (see EnsureCaseNotClosed) therefore applies here too, not
            // only to mutations of an already-existing CaseFileItem.
            await EnsureCaseNotClosed(caseDefinitionId);

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
            await EnsureAvailable();

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
            await EnsureAvailable();

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
            await EnsureAvailable();

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
            await EnsureAvailable();

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
            await EnsureAvailable();

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
            await EnsureAvailable();

            RaiseEvent(new Discarded());

            await ConfirmEvents();

            await PublishTransition(Interfaces.Model.CaseFileItemTransition.Delete);
        }

        public Task<CaseFileItemSnapshot> GetSnapshot() => Task.FromResult(State.ToSnapshot());

        // Table 8.1/8.2: every mutating operation other than create is only defined From Available;
        // Discarded is a terminal state with no outgoing transitions ("A CaseFileItem instance in
        // this state is considered deleted and is not available to Case workers or expressions").
        private async Task EnsureAvailable()
        {
            if (!State.Defined) throw new InvalidOperationException($"CaseFileItem {_address} has not been created");

            if (State.CaseFileItemState == Interfaces.Plan.CaseFileItem.CaseFileItemState.Discarded)
            {
                throw new InvalidOperationException($"CaseFileItem {_address} is Discarded and accepts no further operations");
            }

            await EnsureCaseNotClosed(State.CaseDefinitionId);
        }

        // ADO #69 - 8.4.1/Table 8.5 Closed: "Terminal state. In this state no new activity is
        // allowed in the Case." Mirrors CaseGrain.Trigger's own Closed guard (same exception
        // type, same "is Closed" wording) so the read-only lockdown holds at the CaseFileItem's
        // own public surface too, not only at the Case's Trigger surface.
        //
        // Resolving the owning ICaseGrain is deliberately tolerant of an unresolvable/undefined
        // case: per this grain's own class remarks (work item #16), a CaseFileItem is usable
        // standalone, without a fully modeled/instantiated Case - a caller that has not (yet)
        // defined or created the owning case has nothing to lock down against, so the guard is a
        // no-op rather than a hard dependency on that modeling layer existing.
        private async Task EnsureCaseNotClosed(string caseDefinitionId)
        {
            var caseScope = await ResolveCaseScope(caseDefinitionId);
            if (caseScope == null) return;

            var caseGrain = GrainFactory.GetGrain<Interfaces.Plan.Case.ICaseGrain>(_caseInstanceId, caseScope);
            if (!await caseGrain.Defined()) return;

            var caseSnapshot = await caseGrain.GetSnapshot();

            if (caseSnapshot.PlanItemState == Interfaces.Model.PlanItemState.Closed)
            {
                throw new InvalidOperationException($"case {_caseInstanceId} is Closed; CaseFileItem {_address} cannot be mutated");
            }
        }

        // The ICaseGrain compound key extension is the owning case's CasePlanModel.Id (the
        // convention every existing caller already follows - see e.g.
        // CaseLifecycleIntegrationTests.CreateEmptyCase/ConformanceHarness.DeployAndCreate), which
        // this grain has no way to derive from its own compound key. Cached once resolved: it
        // cannot change for a given caseDefinitionId, so later calls in this activation's
        // lifetime skip the ICaseDefinitionGrain round trip.
        private async Task<string> ResolveCaseScope(string caseDefinitionId)
        {
            if (_caseScope != null) return _caseScope;
            if (string.IsNullOrEmpty(caseDefinitionId)) return null;

            var caseDefinition = await GrainFactory
                .GetGrain<Interfaces.Plan.Case.ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .GetDefinition();

            _caseScope = caseDefinition?.CasePlanModel?.Id;
            return _caseScope;
        }

        // D3 - see CaseFileItemAddress.CaseWideSentinel's remarks for why every transition is ALSO
        // published to a fixed, case-wide stream: a standalone-IfPart Sentry (no OnParts) has no
        // specific CaseFileItem sourceRef to subscribe to per 8.5's last sentence, and needs to
        // re-evaluate its IfPart against every CaseFileItem event regardless of which item
        // transitioned. Two distinct publishes (not one call with two subscribers merged) because
        // the per-item stream (keyed on Definition.Id) and the case-wide stream (keyed on the fixed
        // sentinel) are genuinely different Orleans streams with different keys - PublishEvent
        // (inherited from CmmnElementGrain) only ever targets Definition.Id, so the case-wide
        // publish needs its own call.
        private async Task PublishTransition(Interfaces.Model.CaseFileItemTransition standardEvent)
        {
            var @event = new CaseFileItemTransitionedEvent(_scope, Definition.Id, standardEvent);

            await Task.WhenAll(
                PublishEvent(@event),
                GetCaseWideEventStream<CaseFileItemTransitionedEvent>().OnNextAsync(@event));
        }

        private IAsyncStream<TEvent> GetCaseWideEventStream<TEvent>() =>
            this.GetStreamProvider("Default")
                .GetCaseEventStream<TEvent>(_caseInstanceId, CaseFileItemAddress.CaseWideSentinel);
    }
}
