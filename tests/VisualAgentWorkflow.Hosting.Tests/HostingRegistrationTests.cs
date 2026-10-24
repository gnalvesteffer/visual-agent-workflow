using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class HostingRegistrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-di-" + Guid.NewGuid());

    [Fact]
    public void RegistrationIsLazyIdempotentAndDoesNotRequireCatalogOrProviders()
    {
        var services = new ServiceCollection();
        services.AddLocalWorkflowRuntime(directory);
        services.AddLocalWorkflowRuntime(Path.Combine(directory, "."));
        Assert.False(Directory.Exists(directory));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var runtime = provider.GetRequiredService<LocalWorkflowRuntime>();
        Assert.Same(runtime, provider.GetRequiredService<IWorkflowClient>());
        Assert.Same(runtime, provider.GetRequiredService<IWorkflowRunControl>());
        Assert.Same(runtime, Assert.Single(provider.GetServices<IHostedService>()));
        Assert.Null(provider.GetService<IWorkflowCatalog>());
        Assert.Null(provider.GetService<INodeToolBroker>());
        Assert.Null(provider.GetService<IAgentNodeExecutor>());
        Assert.Throws<InvalidOperationException>(() => services.AddLocalWorkflowRuntime(directory + "-other"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyJournalPathsAreRejectedBeforeServiceResolution(string path)
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddLocalWorkflowRuntime(path));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task GenericHostOwnsOneRuntimeAndCancelsItsPausedContinuationOnStop()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLocalWorkflowRuntime(directory);
        using var host = builder.Build();
        await host.StartAsync();
        var runtime = host.Services.GetRequiredService<LocalWorkflowRuntime>();
        var controls = host.Services.GetRequiredService<IWorkflowRunControl>();
        var run = await controls.StartPausedRunAsync(PipelineTemplates.Create("uppercase"), "abc");
        Assert.Equal("paused", run.Status);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(deadline.Token);
        var stored = await runtime.GetRunAsync(run.Header.RunId);
        Assert.NotNull(stored);
        Assert.True(stored.IsTerminal);
        Assert.Contains(stored.Status, new[] { "cancelled", "interrupted" });
        Assert.DoesNotContain(stored.Events, e => e.Kind == ExecutionEventKinds.NodeStarted);
        await Assert.ThrowsAsync<WorkflowRunControlException>(() => controls.ResumeRunAsync(run.Header.RunId));
    }

    [Fact]
    public void ExplicitHostClientsAndJournalsArePreserved()
    {
        var services = new ServiceCollection();
        var journal = new FileRunJournal(directory);
        var client = new UnavailableClient();
        services.AddSingleton(journal);
        services.AddSingleton<IWorkflowClient>(client);
        services.AddSingleton<IWorkflowRunControl>(client);
        services.AddLocalWorkflowRuntime(directory);
        using var provider = services.BuildServiceProvider();
        Assert.Same(journal, provider.GetRequiredService<FileRunJournal>());
        Assert.Same(client, provider.GetRequiredService<IWorkflowClient>());
        Assert.Same(client, provider.GetRequiredService<IWorkflowRunControl>());
        Assert.Single(provider.GetServices<IHostedService>());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class UnavailableClient : IWorkflowClient, IWorkflowRunControl
    {
        public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) => throw new NotSupportedException();
        public Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input) => throw new NotSupportedException();
        public Task<RunSnapshot?> GetRunAsync(Guid runId) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => throw new NotSupportedException();
        public Task CancelRunAsync(Guid runId) => throw new NotSupportedException();
        public Task PauseRunAsync(Guid runId) => throw new NotSupportedException();
        public Task ResumeRunAsync(Guid runId) => throw new NotSupportedException();
    }
}
