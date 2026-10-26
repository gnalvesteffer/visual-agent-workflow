using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class StudioModelProviderStoreTests
{
    [Fact]
    public async Task SavingAnExistingConnectionIdEditsTheCurrentModelEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-model-connections-" + Guid.NewGuid().ToString("N"));
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDataProtection();
        using var services = serviceCollection.BuildServiceProvider();
        var broker = new McpToolBroker([], new ExplicitToolAuthorizationPolicy([]));
        var executor = new AgentNodeExecutor([], broker);
        try
        {
            var store = new StudioModelProviderStore(directory,
                services.GetRequiredService<IDataProtectionProvider>(), executor, broker, []);

            store.Save("unsloth.local", "Unsloth", "model-a", "https://models.example/v1", 45, "first-key", false,
                contextWindowTokens: 32_768, inputCostPerMillionTokens: 0.2m, outputCostPerMillionTokens: 0.8m);
            store.Save("unsloth.local", "Unsloth local", "model-b", "https://models.example/v1", 60, "", false,
                contextWindowTokens: 65_536, inputCostPerMillionTokens: 0.3m, outputCostPerMillionTokens: 1.2m);

            var connection = Assert.Single(store.List());
            var registration = Assert.Single(await executor.ListProvidersAsync());
            Assert.Equal("unsloth.local", connection.ProviderId);
            Assert.Equal("model-b", connection.Model);
            Assert.Equal(60, connection.TimeoutSeconds);
            Assert.True(connection.HasCredential);
            Assert.Equal(65_536, connection.ContextWindowTokens);
            Assert.Equal(0.3m, connection.InputCostPerMillionTokens);
            Assert.Equal(1.2m, connection.OutputCostPerMillionTokens);
            Assert.Equal("model-b", registration.ModelId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
