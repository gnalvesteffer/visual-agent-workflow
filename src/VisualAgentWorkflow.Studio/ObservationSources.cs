using System.Net.Http.Headers;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Observation.Http;

namespace VisualAgentWorkflow.Studio;

/// <summary>Operator-configured destinations only. Browser input cannot create a server-side URL or credential.</summary>
public sealed class ObservationSources : IDisposable
{
    private readonly Dictionary<string, HttpClient> clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IWorkflowRunObserver> observers = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Names => observers.Keys.Order(StringComparer.Ordinal).ToArray();

    public ObservationSources(IConfiguration configuration)
    {
        try
        {
            foreach (var source in configuration.GetSection("WorkflowObservation:Sources").GetChildren())
            {
                if (!Uri.TryCreate(source["BaseAddress"], UriKind.Absolute, out var address) ||
                    address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback))
                    throw new InvalidOperationException("Observation sources require HTTPS, or loopback HTTP for development.");
                var token = source["BearerToken"];
                if (token is not null && (token.Length is < 1 or > 4096 || token.Any(c => c <= ' ' || c > '~')))
                    throw new InvalidOperationException("Invalid observation credential configuration.");
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
                clients.Add(source.Key, client);
                if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                observers.Add(source.Key, new HttpWorkflowRunObserver(client));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IWorkflowRunObserver Get(string name) => observers[name];
    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
    }
}
