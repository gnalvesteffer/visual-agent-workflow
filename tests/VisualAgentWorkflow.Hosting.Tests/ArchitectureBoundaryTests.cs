using System.Xml.Linq;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Blazor.Components;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Project_dependencies_stay_inside_their_architectural_layers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !root.GetFiles("*.slnx").Any()) root = root.Parent;
        Assert.NotNull(root);
        var allowed = new Dictionary<string, string[]>
        {
            ["Core"] = [],
            ["Blazor"] = ["Core"],
            ["AgentFramework"] = ["Core"],
            ["Observation.Http"] = ["Core"],
            ["Mcp"] = ["Core"],
            ["Hosting"] = ["Core", "AgentFramework"],
            ["Studio"] = ["Core", "Blazor", "Hosting", "AgentFramework", "Mcp", "Observation.Http"]
        };
        foreach (var (layer, dependencies) in allowed)
        {
            var project = XDocument.Load(Path.Combine(root.FullName, "src", $"VisualAgentWorkflow.{layer}", $"VisualAgentWorkflow.{layer}.csproj"));
            var references = project.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
                .ToArray();
            Assert.All(references, reference => Assert.Contains(reference, dependencies.Select(name => $"VisualAgentWorkflow.{name}")));
            if (layer == "Core") Assert.Empty(project.Descendants("PackageReference"));
        }
    }

    [Fact]
    public void Contracts_and_embeddable_components_do_not_reference_execution_infrastructure()
    {
        var forbidden = new[] { "VisualAgentWorkflow.Hosting", "VisualAgentWorkflow.AgentFramework", "VisualAgentWorkflow.Mcp", "Microsoft.Agents", "ModelContextProtocol", "Microsoft.Data.Sqlite" };
        foreach (var assembly in new[] { typeof(WorkflowDefinition).Assembly, typeof(WorkflowStudio).Assembly })
            Assert.All(assembly.GetReferencedAssemblies(), reference =>
                Assert.DoesNotContain(forbidden, prefix => reference.Name!.StartsWith(prefix, StringComparison.Ordinal)));
    }
}
