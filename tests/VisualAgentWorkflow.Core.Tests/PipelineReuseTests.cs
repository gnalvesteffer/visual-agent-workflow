using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class PipelineReuseTests
{
    private static WorkflowDefinition Definition(string id = "source", int count = 2)
    {
        var nodes = new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") };
        var edges = new Dictionary<string, ConnectionDefinition>();
        var previous = "input";
        for (var i = 0; i < count; i++)
        {
            var nodeId = $"step-{i}";
            nodes[nodeId] = new(i % 2 == 0 ? NodeTypes.Uppercase : NodeTypes.Reverse, "1.0.0");
            edges[$"link-{i}"] = new(new(previous, "out"), new(nodeId, "in"), "data"); previous = nodeId;
        }
        edges["last"] = new(new(previous, "out"), new("output", "in"), "data");
        return new("1.0", id, "input", nodes.ToImmutableSortedDictionary(), edges.ToImmutableSortedDictionary());
    }

    [Fact]
    public void ExtractsConsecutiveSectionWithoutChangingSource()
    {
        var source = Definition(count: 4);
        var hash = WorkflowJson.ComputeHash(source);
        var layout = WorkflowLayout.Create(source);
        layout = layout with { Nodes = layout.Nodes.SetItem("step-1", layout.Nodes["step-1"] with { Label = "Reusable reverse", Comment = "Why this exists" }),
            Sections = layout.Sections.Add("group", new("Process text", ["step-1", "step-2"])) };
        var extracted = PipelineReuse.Extract(source, layout, ["step-1", "step-2"], "reusable-text");
        Assert.Equal(4, extracted.Definition.Nodes.Count);
        Assert.Equal("Reusable reverse", extracted.Layout.Nodes["step-1"].Label);
        Assert.Equal("Process text", extracted.Layout.Sections["group"].Label);
        Assert.Equal(hash, WorkflowJson.ComputeHash(source));
        Assert.Equal(new[] { "step-1", "step-2" }, PipelineEditing.OrderedNodes(extracted.Definition).Skip(1).SkipLast(1));
    }

    [Fact]
    public void RejectsEmptyNonconsecutiveTerminalAndUnknownSelections()
    {
        var source = Definition(count: 3); var layout = WorkflowLayout.Create(source);
        foreach (var ids in new[] { Array.Empty<string>(), ["step-0", "step-2"], ["input"], ["missing"] })
            Assert.Throws<ArgumentException>(() => PipelineReuse.Extract(source, layout, ids, "reusable"));
        Assert.Throws<ArgumentException>(() => PipelineReuse.Extract(source, layout, ["step-0"], ".."));
    }

    [Fact]
    public void InsertionCopiesPinnedVersionWithFreshIdentitiesAndLayout()
    {
        var source = Definition(); var sourceLayout = WorkflowLayout.Create(source);
        sourceLayout = sourceLayout with
        {
            Nodes = sourceLayout.Nodes.SetItem("step-0", sourceLayout.Nodes["step-0"] with { Label = "Pinned label", Comment = "A note" }),
            Wires = sourceLayout.Wires.Add("link-1", new("curve", [new(new(600, 240), 45)])),
            Sections = sourceLayout.Sections.Add("section", new("Reusable section", ["step-0", "step-1"]))
        };
        var revision = WorkflowRevision.Create(source, "test");
        var published = new PublishedWorkflowRevision(3, revision);
        var target = Definition("target", 1); var layout = WorkflowLayout.Create(target);
        var inserted = PipelineReuse.Insert(target, layout, "input", published, sourceLayout);
        var again = PipelineReuse.Insert(inserted.Definition, inserted.Layout, "input", published, sourceLayout);
        var copied = inserted.Definition.Nodes.Where(p => p.Value.Origin is not null).ToArray();
        Assert.Equal(2, copied.Length);
        Assert.Equal(7, again.Definition.Nodes.Count);
        Assert.Equal(4, again.Definition.Nodes.Values.Count(n => n.Origin is not null));
        Assert.All(copied, item =>
        {
            Assert.DoesNotContain(item.Key, source.Nodes.Keys);
            Assert.Equal(revision.RevisionId, item.Value.Origin!.RevisionId);
            Assert.Equal(3, item.Value.Origin.Version);
            Assert.Equal(revision.ContentHash, item.Value.Origin.ContentHash);
        });
        Assert.Contains(inserted.Layout.Nodes.Values, n => n.Label == "Pinned label" && n.Comment == "A note");
        Assert.Contains(inserted.Layout.Wires.Values, w => w.Points.Length == 1 && w.Points[0].Angle == 45);
        Assert.Equal("Reusable section", inserted.Layout.Sections.Single().Value.Label);
        Assert.Equal("1.3", inserted.Definition.SchemaVersion);
        var changedSource = PipelineEditing.ChangeOperation(source, "step-0", NodeTypes.Reverse);
        Assert.Equal(NodeTypes.Uppercase, copied.Single(p => p.Value.Origin!.NodeId == "step-0").Value.Type);
        Assert.NotEqual(WorkflowJson.ComputeHash(changedSource), revision.ContentHash);
    }

    [Fact]
    public void InsertionPreservesRequirementsButDoesNotCarryPermissionGrants()
    {
        var source = Definition() with { SchemaVersion = "1.2" };
        var request = new ToolBindingRequest("customer.read", new string('a', 64));
        source = source with { Nodes = source.Nodes
            .SetItem("step-0", new(NodeTypes.McpTool, "1.0.0", request))
            .SetItem("step-1", new(NodeTypes.Agent, "1.0.0", Agent: new("host-provider", "2.0", "Instructions", [request]))) };
        var inserted = PipelineReuse.Insert(Definition("target"), WorkflowLayout.Create(Definition("target")), "input",
            new(1, WorkflowRevision.Create(source, "test")), WorkflowLayout.Create(source));
        var imported = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(inserted.Definition, inserted.Layout));
        Assert.Contains(new PackageRequirement("mcp-tool", request.BindingId, "", request.ContractHash), imported.Requirements);
        Assert.Contains(new PackageRequirement("agent-provider", "host-provider", "2.0"), imported.Requirements);
        Assert.Equal(1, imported.Requirements.Count(r => r.Kind == "mcp-tool"));
        Assert.DoesNotContain("grants", WorkflowJson.Serialize(inserted.Definition));
        Assert.Equal(request, inserted.Definition.Nodes.Values.Single(n => n.Type == NodeTypes.McpTool).Tool);
    }

    [Fact]
    public void RejectsOverLimitInsertAndPreservesOldHashesAndSchemaThroughEdits()
    {
        var target = Definition("large", 253); var source = Definition();
        Assert.Throws<ArgumentException>(() => PipelineReuse.Insert(target, WorkflowLayout.Create(target), "input",
            new(1, WorkflowRevision.Create(source, "test")), WorkflowLayout.Create(source)));
        Assert.DoesNotContain("origin", WorkflowJson.Serialize(source));
        var inserted = PipelineReuse.Insert(source, WorkflowLayout.Create(source), "input", new(1, WorkflowRevision.Create(source, "test")), WorkflowLayout.Create(source));
        var id = inserted.Definition.Nodes.First(p => p.Value.Origin is not null).Key;
        var replaced = PipelineEditing.ReplaceNode(inserted.Definition, id, new(NodeTypes.Agent, "1.0.0", Agent: new("demo", "1", "", [])));
        Assert.Equal("1.3", replaced.SchemaVersion);
        WorkflowValidator.ThrowIfInvalid(replaced);
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(inserted.Definition with { SchemaVersion = "1.0" }));
    }

    [Fact]
    public void PackagesRoundTripAndRejectTamperingUnknownFieldsAndDuplicateProperties()
    {
        var definition = Definition(); var layout = WorkflowLayout.Create(definition);
        var json = WorkflowPackageJson.Serialize(definition, layout);
        var read = WorkflowPackageJson.Deserialize(json);
        Assert.Equal(json, WorkflowPackageJson.Serialize(read.Definition, read.Layout));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["layout"]!["nodes"]!["step-0"]!["label"] = "tampered";
        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Deserialize(root.ToJsonString()));
        root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["requirements"] = new System.Text.Json.Nodes.JsonArray();
        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Deserialize(root.ToJsonString()));
        Assert.Throws<JsonException>(() => WorkflowPackageJson.Deserialize(json.Replace("\"formatVersion\":", "\"grants\": true, \"formatVersion\":")));
        Assert.Throws<JsonException>(() => WorkflowPackageJson.Deserialize(json.Replace("\"formatVersion\": \"1.0\",", "\"formatVersion\": \"1.0\", \"formatVersion\": \"1.0\",")));
        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Deserialize(new string(' ', WorkflowPackageJson.MaximumBytes + 1)));
    }

    [Fact]
    public void PackageImportAcceptsAndNormalizesHistoricalWindowsHashes()
    {
        var definition = Definition();
        var layout = WorkflowLayout.Create(definition);
        var root = System.Text.Json.Nodes.JsonNode.Parse(WorkflowPackageJson.Serialize(definition, layout))!;
        root["definitionHash"] = LegacyWindowsHash(WorkflowJson.Serialize(definition));
        root["layoutHash"] = LegacyWindowsHash(WorkflowLayoutJson.Serialize(layout, definition));

        var imported = WorkflowPackageJson.Deserialize(root.ToJsonString());

        Assert.Equal(WorkflowJson.ComputeHash(imported.Definition), imported.DefinitionHash);
        Assert.Equal(WorkflowLayoutJson.ComputeHash(imported.Layout, imported.Definition), imported.LayoutHash);

        static string LegacyWindowsHash(string json) => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                json.Replace("\n", "\r\n", StringComparison.Ordinal))));
    }
}
