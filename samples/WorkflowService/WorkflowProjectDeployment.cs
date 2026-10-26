using System.Security.Cryptography;
using System.Text;
using VisualAgentWorkflow.Core;

namespace WorkflowService;

internal static class WorkflowProjectDeployment
{
    public static async Task<WorkflowRevision> LoadAsync(string path, string workflowId, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        if (stream.Length is <= 0 or > WorkflowProjectPackageJson.MaximumArchiveBytes)
            throw new InvalidDataException("Workflow release archive size is outside the supported limit.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var release = WorkflowProjectPackageJson.Read(bytes);
        if (!release.Workflows.TryGetValue(workflowId, out var package))
            throw new InvalidDataException("The requested workflow is not an entry point in this release.");

        // This sample derives a stable host revision identity from the immutable release digest and selected entry.
        // Production hosts may instead bind the artifact digest to an operator-owned deployment revision.
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"{release.ArtifactHash}\n{workflowId}"));
        var revisionId = new Guid(identity.AsSpan(0, 16));
        return WorkflowRevision.Rehydrate(revisionId, package.Definition, "workflow-project-package", null,
            DateTimeOffset.UtcNow, package.DefinitionHash);
    }
}
