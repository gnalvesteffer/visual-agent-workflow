using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace VisualAgentWorkflow.Blazor.Components;

/// <summary>Hosts choose recovery UI. Unexpected exception messages are not copied into logs or markup.</summary>
public sealed class WorkflowErrorBoundary : ErrorBoundary
{
    [Microsoft.AspNetCore.Components.Inject] public ILogger<WorkflowErrorBoundary> Logger { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError("Workflow UI failed with {ExceptionType}.", exception.GetType().Name);
        return Task.CompletedTask;
    }
}
