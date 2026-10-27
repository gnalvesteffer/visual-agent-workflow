using System.Collections.Concurrent;

namespace VisualAgentWorkflow.Core;

/// <summary>Correlates bounded HTTP usage fields with the model call currently being recorded.</summary>
public static class ProviderUsageCapture
{
    private static readonly AsyncLocal<Guid?> CurrentCall = new();
    private static readonly ConcurrentDictionary<Guid, byte> ActiveCalls = new();
    private static readonly ConcurrentDictionary<Guid, long> CacheWriteCounts = new();

    public static IDisposable Begin(Guid modelCallId)
    {
        var previous = CurrentCall.Value;
        ActiveCalls[modelCallId] = 0;
        CurrentCall.Value = modelCallId;
        return new CaptureScope(modelCallId, previous);
    }

    public static void RecordCacheWriteInputTokens(long count)
    {
        if (count >= 0 && CurrentCall.Value is { } modelCallId && ActiveCalls.ContainsKey(modelCallId))
            CacheWriteCounts[modelCallId] = count;
    }

    public static bool TryTakeCacheWriteInputTokens(out long count)
    {
        count = 0;
        return CurrentCall.Value is { } modelCallId && CacheWriteCounts.TryRemove(modelCallId, out count);
    }

    private sealed class CaptureScope(Guid modelCallId, Guid? previous) : IDisposable
    {
        public void Dispose()
        {
            CacheWriteCounts.TryRemove(modelCallId, out _);
            ActiveCalls.TryRemove(modelCallId, out _);
            CurrentCall.Value = previous;
        }
    }
}
