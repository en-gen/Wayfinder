using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Wayfinder.Grains.Tests.Utils.Helpers
{
    // ADO #174 - a positive synchronization point for "this must NOT fire" assertions on
    // CmmnElementGrain-backed grains (Sentry, PlanItem, ...): several of that family's event
    // handlers unconditionally journal a marker event (e.g. SentryGrain.HandleOnPartOccurred
    // always raises OnPartOccurred before it even looks at the IfPart) as the very first step of
    // deciding whether to fire. Because that decision is made synchronously, later in the SAME
    // method invocation, before that one method's own ConfirmEvents() call, the marker event
    // becoming visible in the grain's confirmed journal proves the decision for that occurrence
    // has already been made and committed - not just "hasn't published yet within some arbitrary
    // window". Polling for the marker is therefore a strictly stronger and equally-fast substitute
    // for the fixed-sleep-then-check idiom this issue is retiring: it settles the instant the real
    // work is done, and it does not silently pass just because a cascade happened to be slow.
    //
    // Only usable where the grain's own code path guarantees the marker is raised regardless of
    // outcome - it is not a substitute where the "should not happen" path raises nothing at all
    // (e.g. a standalone-IfPart Sentry's clean-FALSE evaluation, or an OnPart whose standardEvent
    // does not even match); those sites have no marker to poll for and still need a scaled window.
    public static class JournalPolling
    {
        public static async Task<bool> UntilJournaled<TEvent>(
            Func<Task<IReadOnlyList<object>>> getJournaledEvents, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                var events = await getJournaledEvents();
                if (events.OfType<TEvent>().Any()) return true;

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return (await getJournaledEvents()).OfType<TEvent>().Any();
        }
    }
}
