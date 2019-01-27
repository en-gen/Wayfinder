using System;
using Orleans.Streams;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class StreamProviderExtensions
    {
        public static IAsyncStream<TEvent> GetCaseEventStream<TEvent>(this IStreamProvider streamProvider, Guid caseInstanceId, string eventSource)
        {
            return streamProvider.GetStream<TEvent>(caseInstanceId, $"{typeof(TEvent).Name}:{eventSource}");
        }
    }
}
