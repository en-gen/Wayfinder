using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionConfirmation
{
    // Issue #160 test-support only - never referenced by production code or by any other test
    // fixture. A minimal, self-contained IGrainStorage (no wrapping/decoration of the real
    // MemoryGrainStorage - simpler and has no dependency on however that provider happens to be
    // DI-registered) whose WriteStateAsync sleeps for a fixed, generous delay before completing.
    //
    // Why this exists: RepetitionChildConfirmationIntegrationTests needs to deterministically
    // catch StageBehavior.HandleChildRepeated's raised-but-not-yet-persisted events before they
    // land in storage. Empirically (see that test class's remarks and the timing probe used to
    // develop it), NEITHER awaiting the client-side stream publish NOR awaiting the production
    // Trigger(...) cascade reliably indicates "the subscriber's handler, including any
    // ConfirmEvents call, has completed" - both return in a handful of milliseconds while the
    // actual persist-to-storage step (whatever currently causes it, confirmed or eventually
    // opportunistic) lands anywhere from ~30ms to ~200ms later, and IManagementGrain.
    // ForceActivationCollection(TimeSpan.Zero)'s own completion latency turned out to be just as
    // variable - so racing the two directly is not reliable in either direction.
    // Stretching the storage write to several seconds removes that race entirely: it is
    // overwhelmingly longer than any realistic scheduling jitter, so forcing collection
    // immediately after the triggering publish is now guaranteed to land while the write is still
    // pending. Host.ConfirmEvents() (the #160 fix) explicitly awaits that same pending write, so
    // the fixed handler cannot possibly finish (and cannot make the activation look idle again)
    // until well after the delay elapses - meaning the assertions in that test only need to
    // distinguish "did HandleChildRepeated's own confirm survive an immediate forced deactivation"
    // from "was it never confirmed at all", exactly the #160 defect, with no timing guesswork.
    public class DelayedInMemoryGrainStorage : IGrainStorage
    {
        public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(3);

        private readonly ConcurrentDictionary<string, (object State, string ETag)> _store = new();

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_store.TryGetValue(Key(stateName, grainId), out var entry))
            {
                grainState.State = (T)entry.State;
                grainState.ETag = entry.ETag;
                grainState.RecordExists = true;
            }

            return Task.CompletedTask;
        }

        public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            await Task.Delay(WriteDelay);

            var newETag = Guid.NewGuid().ToString();
            _store[Key(stateName, grainId)] = (grainState.State, newETag);
            grainState.ETag = newETag;
            grainState.RecordExists = true;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _store.TryRemove(Key(stateName, grainId), out _);
            grainState.ETag = null;
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }

        private static string Key(string stateName, GrainId grainId) => $"{stateName}/{grainId}";
    }
}
