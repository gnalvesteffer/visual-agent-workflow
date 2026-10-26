using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Providers.OpenAI;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioModelProvider(string ProviderId, string DisplayName, string Model,
    string Endpoint, int TimeoutSeconds, string ProtectedApiKey, bool AllowInsecureLoopback = false, bool AllowInsecureHttp = false,
    int? ContextWindowTokens = null, decimal? InputCostPerMillionTokens = null, decimal? OutputCostPerMillionTokens = null);
public sealed record StudioModelProviderView(string ProviderId, string DisplayName, string Model,
    string Endpoint, int TimeoutSeconds, bool HasCredential, bool AllowInsecureHttp, int? ContextWindowTokens,
    decimal? InputCostPerMillionTokens, decimal? OutputCostPerMillionTokens);

/// <summary>Local Studio settings. Credentials are protected at rest and never returned by the settings view.</summary>
public sealed class StudioModelProviderStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly AgentNodeExecutor executor;
    private readonly INodeToolBroker tools;
    private readonly IReadOnlyList<AgentProviderRegistration> configured;
    private List<StudioModelProvider> saved;

    public StudioModelProviderStore(string directory, IDataProtectionProvider protection, AgentNodeExecutor agentExecutor,
        INodeToolBroker broker, IEnumerable<AgentProviderRegistration> hostProviders)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "model-providers.json");
        protector = protection.CreateProtector("VisualAgentWorkflow.Studio.ModelProviders.v1");
        executor = agentExecutor;
        tools = broker;
        configured = hostProviders.ToArray();
        saved = File.Exists(path) ? (JsonSerializer.Deserialize<List<StudioModelProvider>>(File.ReadAllText(path)) ?? [])
            .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal).Select(group => group.Last()).ToList() : [];
        Refresh();
    }

    public IReadOnlyList<StudioModelProviderView> List()
    {
        lock (gate) return saved.Select(provider => new StudioModelProviderView(provider.ProviderId,
            provider.DisplayName, provider.Model, provider.Endpoint, provider.TimeoutSeconds,
            ReadApiKey(provider).Length > 0, provider.AllowInsecureHttp || provider.AllowInsecureLoopback,
            provider.ContextWindowTokens, provider.InputCostPerMillionTokens, provider.OutputCostPerMillionTokens)).ToArray();
    }

    public void Save(string id, string name, string model, string endpoint, int timeoutSeconds, string apiKey,
        bool allowInsecureHttp, bool removeApiKey = false,
        int? contextWindowTokens = null, decimal? inputCostPerMillionTokens = null, decimal? outputCostPerMillionTokens = null)
    {
        var uri = Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ? parsed : null;
        var options = new OpenAIProviderOptions
        {
            ProviderId = id,
            DisplayName = name,
            Model = model,
            Endpoint = uri!,
            InvocationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            AllowInsecureHttp = allowInsecureHttp,
            ContextWindowTokens = contextWindowTokens,
            InputCostPerMillionTokens = inputCostPerMillionTokens,
            OutputCostPerMillionTokens = outputCostPerMillionTokens
        };
        lock (gate)
        {
            if (configured.Any(provider => provider.Descriptor.ProviderId == id))
                throw new ArgumentException("That model connection is supplied by host configuration and cannot be edited in Studio.");
            var previous = saved.SingleOrDefault(provider => provider.ProviderId == id);
            var credential = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : removeApiKey ? "" :
                previous is not null ? ReadApiKey(previous) : "";
            OpenAIProviders.ValidateOptions(options, credential);
            var replacement = new StudioModelProvider(id, name, model, uri!.AbsoluteUri,
                timeoutSeconds, protector.Protect(credential), AllowInsecureHttp: allowInsecureHttp,
                ContextWindowTokens: contextWindowTokens, InputCostPerMillionTokens: inputCostPerMillionTokens,
                OutputCostPerMillionTokens: outputCostPerMillionTokens);
            var updated = saved.ToList();
            if (previous is null) updated.Add(replacement);
            else updated[updated.FindIndex(provider => provider.ProviderId == id)] = replacement;
            Persist(updated);
            saved = updated;
            Refresh();
        }
    }

    public bool Delete(string id)
    {
        lock (gate)
        {
            var updated = saved.Where(provider => provider.ProviderId != id).ToList();
            if (updated.Count == saved.Count) return false;
            Persist(updated);
            saved = updated;
            Refresh();
            return true;
        }
    }

    private void Persist(IReadOnlyList<StudioModelProvider> providers)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(providers));
        File.Move(temporary, path, true);
    }

    private string ReadApiKey(StudioModelProvider provider)
    {
        var key = protector.Unprotect(provider.ProtectedApiKey);
        return provider.AllowInsecureLoopback && key == "lm-studio" ? "" : key;
    }

    private void Refresh()
    {
        var dynamic = saved.Select(provider =>
        {
            var options = new OpenAIProviderOptions
            {
                ProviderId = provider.ProviderId,
                DisplayName = provider.DisplayName,
                Model = provider.Model,
                Endpoint = new Uri(provider.Endpoint),
                InvocationTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),
                AllowInsecureHttp = provider.AllowInsecureHttp || provider.AllowInsecureLoopback,
                ContextWindowTokens = provider.ContextWindowTokens,
                InputCostPerMillionTokens = provider.InputCostPerMillionTokens,
                OutputCostPerMillionTokens = provider.OutputCostPerMillionTokens
            };
            return OpenAIProviders.Create(options, () => ReadApiKey(provider));
        });
        executor.ReplaceProviders(configured.Concat(dynamic));
    }
}
