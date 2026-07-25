using System;
using Orleans.Runtime;
using Orleans.Streams;

namespace Wayfinder.Grains.Infrastructure.Extensions
{
    public static class StreamProviderExtensions
    {
        public static IAsyncStream<TEvent> GetCaseEventStream<TEvent>(this IStreamProvider streamProvider, Guid caseInstanceId, string eventSource)
        {
            // Same identity shape as before modern Orleans's StreamId split: namespace is
            // "{EventTypeName}:{eventSource}", key is the case instance id. Keeping both
            // components identical keeps existing subscriptions resolvable.
            var streamId = StreamId.Create($"{typeof(TEvent).Name}:{eventSource}", caseInstanceId);
            return streamProvider.GetStream<TEvent>(streamId);
        }
    }
}
