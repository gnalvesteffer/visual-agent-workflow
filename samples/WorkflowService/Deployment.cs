using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace WorkflowService;

// Operator-owned deployment configuration, not an importable workflow permission manifest.
internal sealed record Deployment(string PackageFile, Guid RevisionId, string DefinitionHash, string Author, DateTimeOffset CreatedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static async Task<WorkflowRevision> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var configurationPath = Path.GetFullPath(path);
        var text = await ReadBoundedAsync(configurationPath, 16_384, cancellationToken);
        var deployment = JsonSerializer.Deserialize<Deployment>(text, Json) ?? throw new InvalidDataException("Deployment required.");
        if (string.IsNullOrWhiteSpace(deployment.PackageFile) || string.IsNullOrWhiteSpace(deployment.DefinitionHash))
            throw new InvalidDataException("Deployment artifact and content pin are required.");
        var packagePath = Path.GetFullPath(deployment.PackageFile, Path.GetDirectoryName(configurationPath)!);
        var package = WorkflowPackageJson.Deserialize(await ReadBoundedAsync(packagePath, WorkflowPackageJson.MaximumBytes, cancellationToken));
        return WorkflowRevision.Rehydrate(deployment.RevisionId, package.Definition, deployment.Author, null,
            deployment.CreatedAt, deployment.DefinitionHash);
    }

    private static async Task<string> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        // Read at most limit+1 even if a file changes after opening it.
        await using var stream = File.OpenRead(path);
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException("Deployment file exceeds its size limit.");
        return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
    }
}
