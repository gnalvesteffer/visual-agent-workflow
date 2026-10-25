using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Providers.OpenAI;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioModelProvider(string ProviderId, string Version, string DisplayName, string Model,
    string Endpoint, int TimeoutSeconds, string ProtectedApiKey, bool AllowInsecureLoopback = false, bool AllowInsecureHttp = false);
public sealed record StudioModelProviderView(string ProviderId, string Version, string DisplayName, string Model,
    string Endpoint, int TimeoutSeconds, bool HasCredential, bool AllowInsecureHttp);

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
        saved = File.Exists(path) ? JsonSerializer.Deserialize<List<StudioModelProvider>>(File.ReadAllText(path)) ?? [] : [];
        Refresh();
    }

    public IReadOnlyList<StudioModelProviderView> List()
    {
        lock (gate) return saved.Select(provider => new StudioModelProviderView(provider.ProviderId, provider.Version,
            provider.DisplayName, provider.Model, provider.Endpoint, provider.TimeoutSeconds,
            ReadApiKey(provider).Length > 0, provider.AllowInsecureHttp || provider.AllowInsecureLoopback)).ToArray();
    }

    public void Add(string id, string version, string name, string model, string endpoint, int timeoutSeconds, string apiKey,
        bool allowInsecureHttp, string? credentialSourceVersion = null, bool removeApiKey = false)
    {
        var uri = Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ? parsed : null;
        var options = new OpenAIProviderOptions
        {
            ProviderId = id,
            Version = version,
            DisplayName = name,
            Model = model,
            Endpoint = uri!,
            InvocationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            AllowInsecureHttp = allowInsecureHttp
        };
        lock (gate)
        {
            if (saved.Any(provider => provider.ProviderId == id && provider.Version == version) || configured.Any(provider => provider.Descriptor.ProviderId == id && provider.Descriptor.Version == version))
                throw new ArgumentException("That provider ID and version already exist. Use a new version to preserve published workflows.");
            var previous = credentialSourceVersion is null
                ? saved.LastOrDefault(provider => provider.ProviderId == id)
                : saved.SingleOrDefault(provider => provider.ProviderId == id && provider.Version == credentialSourceVersion)
                    ?? throw new InvalidOperationException("The provider version being edited was removed. Refresh the page and try again.");
            var credential = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : removeApiKey ? "" :
                previous is not null ? ReadApiKey(previous) : "";
            OpenAIProviders.ValidateOptions(options, credential);
            var updated = saved.Append(new StudioModelProvider(id, version, name, model, uri!.AbsoluteUri,
                timeoutSeconds, protector.Protect(credential), AllowInsecureHttp: allowInsecureHttp)).ToList();
            Persist(updated);
            saved = updated;
            Refresh();
        }
    }

    public bool Delete(string id, string version)
    {
        lock (gate)
        {
            var updated = saved.Where(provider => provider.ProviderId != id || provider.Version != version).ToList();
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
                Version = provider.Version,
                DisplayName = provider.DisplayName,
                Model = provider.Model,
                Endpoint = new Uri(provider.Endpoint),
                InvocationTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),
                AllowInsecureHttp = provider.AllowInsecureHttp || provider.AllowInsecureLoopback
            };
            return OpenAIProviders.Create(options, () => ReadApiKey(provider));
        });
        executor.ReplaceProviders(configured.Concat(dynamic));
    }
}
