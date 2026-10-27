using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Opt-in registration of the single-process local runtime, without a catalog or UI.</summary>
public static class WorkflowHostingExtensions
{
    /// <summary>
    /// Registers one backend-owned runtime and its client/control aliases. The host owns start/stop,
    /// exclusive runtime ownership of the selected store, authorization, tool brokers and agent providers.
    /// Repeated calls with the same normalized path are harmless; conflicting paths are rejected.
    /// Existing store, journal, runtime and client registrations are preserved.
    /// </summary>
    public static IServiceCollection AddLocalWorkflowRuntime(this IServiceCollection services, string journalDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        var directory = Path.GetFullPath(journalDirectory);
        var previous = services.FirstOrDefault(d => d.ServiceType == typeof(Registration))?.ImplementationInstance as Registration;
        if (previous is not null)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(previous.Directory, directory, comparison))
                throw new InvalidOperationException("A local runtime is already registered with another journal directory.");
            return services;
        }
        services.AddSingleton(new Registration(directory));
        services.AddLogging();
        services.TryAddSingleton<ParallelCollaborationRuntimeOptions>();
        services.TryAddSingleton(_ => new FileRunJournal(directory));
        services.TryAddSingleton<IWorkflowRunStore>(provider => provider.GetRequiredService<FileRunJournal>());
        services.TryAddSingleton(provider => new LocalWorkflowRuntime(
            provider.GetRequiredService<IWorkflowRunStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalWorkflowRuntime>>(),
            provider.GetService<INodeToolBroker>(), provider.GetService<IAgentNodeExecutor>(),
            provider.GetRequiredService<ParallelCollaborationRuntimeOptions>()));
        services.TryAddSingleton<IWorkflowClient>(provider => provider.GetRequiredService<LocalWorkflowRuntime>());
        services.TryAddSingleton<IWorkflowRunControl>(provider => provider.GetRequiredService<LocalWorkflowRuntime>());
        services.TryAddSingleton<IWorkflowApprovalClient>(provider => provider.GetRequiredService<LocalWorkflowRuntime>());
        services.AddHostedService(provider => provider.GetRequiredService<LocalWorkflowRuntime>());
        return services;
    }

    private sealed record Registration(string Directory);
}
