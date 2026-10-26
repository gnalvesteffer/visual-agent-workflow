using System.Text;
using VisualAgentWorkflow.Core;

namespace WorkflowService;

internal static class PackageCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: WorkflowService pack PROJECT_ID PROJECT_NAME OUTPUT.vawpkg WORKFLOW.package.json [...]");
            return 1;
        }

        try
        {
            var packages = new List<WorkflowPackage>(args.Length - 4);
            foreach (var path in args.Skip(4))
                packages.Add(WorkflowPackageJson.Deserialize(await ReadBoundedAsync(path, WorkflowPackageJson.MaximumBytes)));
            var artifact = WorkflowProjectPackageJson.CreateArchive(args[1], args[2], packages);
            await File.WriteAllBytesAsync(Path.GetFullPath(args[3]), artifact);
            Console.WriteLine($"Created {WorkflowProjectPackageJson.FileExtension} release ({artifact.Length} bytes, {packages.Count} workflow(s), SHA-256 {WorkflowProjectPackageJson.ComputeHash(artifact)}).");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Package creation failed: {error.GetType().Name}.");
            return 1;
        }
    }

    private static async Task<string> ReadBoundedAsync(string path, int maximumBytes)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException("Workflow package exceeds the supported size.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, count);
    }
}
