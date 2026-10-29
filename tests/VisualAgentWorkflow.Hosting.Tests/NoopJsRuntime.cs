using Microsoft.JSInterop;

namespace VisualAgentWorkflow.Hosting.Tests;

internal sealed class NoopJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        ValueTask.FromResult(default(TValue)!);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
        object?[]? args) => ValueTask.FromResult(default(TValue)!);
}
