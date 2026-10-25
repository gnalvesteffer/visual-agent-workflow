using System.Text;
using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowDesigner
{
    private string reusableWorkflowId = "";
    private Guid? reusableRevisionId;
    private bool ReusableHasControlNodes => reusableVersions.FirstOrDefault(v => v.Revision.RevisionId == reusableRevisionId) is { } selected &&
        WorkflowGraph.HasControlNodes(selected.Revision.Definition);
    private string insertAfterNode = "";
    private string reusableSectionId = "";
    private string reusablePipelineId = "pipeline-" + Guid.NewGuid().ToString("N")[..8];
    private IReadOnlyList<PublishedWorkflowRevision> reusableVersions = [];
    private IReadOnlyList<PackageRequirement> reusableRequirements = [];
    private IReadOnlyList<PackageRequirement> packageRequirements = [];
    private WorkflowDefinition? packagedDefinition;
    private WorkflowLayout? packagedLayout;
    private string packageDownloadHref = "";

    private string PackageDownloadHref
    {
        get
        {
            var viewed = preview?.Revision.Definition ?? definition;
            var presentation = preview is not null ? previewLayout ?? WorkflowLayout.Create(preview.Revision.Definition) : layout;
            if (viewed is not null && presentation is not null &&
                (!ReferenceEquals(viewed, packagedDefinition) || !ReferenceEquals(presentation, packagedLayout)))
            {
                packageDownloadHref = "data:application/json;base64," + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(WorkflowPackageJson.Serialize(viewed, presentation)));
                packagedDefinition = viewed; packagedLayout = presentation;
            }
            return packageDownloadHref;
        }
    }

    private void ResetReuse()
    {
        reusableWorkflowId = ""; reusableRevisionId = null; reusableVersions = []; reusableRequirements = [];
        packageRequirements = []; reusableSectionId = ""; insertAfterNode = "";
        reusablePipelineId = "pipeline-" + Guid.NewGuid().ToString("N")[..8];
    }

    private Task ChooseReusableWorkflow(ChangeEventArgs args) => Perform(async current =>
    {
        var id = args.Value?.ToString() ?? "";
        reusableWorkflowId = id; reusableRevisionId = null; reusableVersions = []; reusableRequirements = [];
        if (id.Length == 0) return;
        var publications = await current.Catalog.ListRevisionsAsync(id).WaitAsync(current.Token);
        if (!Current(current)) return;
        reusableVersions = publications;
        SelectReusableVersion(publications.FirstOrDefault()?.Revision.RevisionId);
    });

    private void ChooseReusableVersion(ChangeEventArgs args) =>
        SelectReusableVersion(Guid.TryParse(args.Value?.ToString(), out var id) ? id : null);

    private void SelectReusableVersion(Guid? id)
    {
        var selected = reusableVersions.FirstOrDefault(p => p.Revision.RevisionId == id);
        reusableRevisionId = selected?.Revision.RevisionId;
        reusableRequirements = selected is null ? [] : WorkflowPackageJson.Requirements(selected.Revision.Definition);
    }

    private Task SaveReusableSection() => Perform(async current =>
    {
        if (current.Catalog is not IWorkflowDesignCatalog designs || definition is null || layout is null ||
            !layout.Sections.TryGetValue(reusableSectionId, out var section)) return;
        PipelineComposition extracted;
        try { extracted = PipelineReuse.Extract(definition, layout, section.NodeIds, reusablePipelineId); }
        catch (ArgumentException)
        {
            error = "Choose a section containing consecutive processing steps only, and a pipeline ID using letters, digits, dots, dashes, or underscores.";
            return;
        }
        try { await designs.SaveDesignAsync(extracted.Definition, extracted.Layout, 0).WaitAsync(current.Token); }
        catch (WorkflowConflictException)
        {
            if (Current(current)) error = "That reusable pipeline ID already exists. Choose another ID; your current draft is unchanged.";
            return;
        }
        var drafts = await current.Catalog.ListDraftsAsync().WaitAsync(current.Token);
        if (!Current(current)) return;
        workflows = drafts;
        message = $"Reusable draft '{extracted.Definition.WorkflowId}' created. Save current edits, then open it from Saved workflow and publish before reuse.";
        reusablePipelineId = "pipeline-" + Guid.NewGuid().ToString("N")[..8];
    });

    private Task InsertReusablePipeline() => Perform(async current =>
    {
        if (definition is null || layout is null || preview is not null || reusableRevisionId is not { } id) return;
        var consumer = definition; var presentation = layout;
        var after = consumer.Nodes.TryGetValue(insertAfterNode, out var node) && node.Type != NodeTypes.Output
            ? insertAfterNode : consumer.EntryNodeId;
        var source = await current.Catalog.GetRevisionAsync(id).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        if (source.Revision.Definition.WorkflowId != reusableWorkflowId) throw new ArgumentException("Source selection changed.");
        var sourceLayout = current.Catalog is IWorkflowDesignCatalog designs
            ? await designs.GetRevisionLayoutAsync(id).WaitAsync(current.Token) : null;
        var next = PipelineReuse.Insert(consumer, presentation, after, source,
            sourceLayout ?? WorkflowLayout.Create(source.Revision.Definition));
        if (!Current(current)) return;
        ApplyComposition(next);
        message = $"Inserted version {source.Version} of '{source.Revision.Definition.WorkflowId}'. Review its requirements, then save and publish explicitly.";
    });

    private Task InsertReusableWorkflowCall() => Perform(async current =>
    {
        if (definition is null || layout is null || preview is not null || reusableRevisionId is not { } id) return;
        var source = await current.Catalog.GetRevisionAsync(id).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        if (source.Revision.Definition.WorkflowId != reusableWorkflowId || source.Revision.Definition.WorkflowId == definition.WorkflowId)
            throw new ArgumentException("Choose a different published workflow revision.");
        var sourceVersion = reusableVersions.Single(item => item.Revision.RevisionId == id);
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(source.Revision.Definition.WorkflowId, id, sourceVersion.Version,
                source.Revision.ContentHash, source.Revision.Definition)
        };
        var after = definition.Nodes.TryGetValue(insertAfterNode, out var selected) && selected.Type != NodeTypes.Output
            ? insertAfterNode : definition.EntryNodeId;
        var output = WorkflowPorts.Outputs(definition.Nodes[after]).FirstOrDefault()?.Id;
        if (output is null) throw new ArgumentException("Choose a node with an available output pin.");
        var next = GraphEditing.InsertNodeAfter(definition, after, output, call);
        if (!Current(current)) return;
        ApplyComposition(new(next, layout));
        message = $"Added a call to '{source.Revision.Definition.WorkflowId}' version {sourceVersion.Version}. This child revision is embedded and pinned in this workflow.";
    });

    private void ApplyComposition(PipelineComposition next)
    {
        if (definition is null || layout is null) return;
        var nextHash = WorkflowJson.ComputeHash(next.Definition) + ":" + WorkflowLayoutJson.ComputeHash(next.Layout, next.Definition);
        if (nextHash == DesignHash) return;
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(next.Definition, next.Layout); error = null; message = null;
    }

    private void ExportPackage()
    {
        var viewed = preview?.Revision.Definition ?? definition!;
        var presentation = preview is not null ? previewLayout ?? WorkflowLayout.Create(viewed) : layout!;
        jsonText = WorkflowPackageJson.Serialize(viewed, presentation);
        packageRequirements = WorkflowPackageJson.Requirements(viewed);
        message = "Package copied into JSON editor. Review user-authored text before sharing; host permissions are not included.";
    }

    private Task ImportPackage() => Perform(_ =>
    {
        if (preview is not null || definition is null || Catalog is not IWorkflowDesignCatalog) return Task.CompletedTask;
        var package = WorkflowPackageJson.Deserialize(jsonText);
        ApplyComposition(new(package.Definition with { WorkflowId = definition.WorkflowId }, package.Layout));
        packageRequirements = package.Requirements;
        message = "Package imported locally with the current workflow ID. Review requirements, then save and publish explicitly.";
        return Task.CompletedTask;
    });

    private string RequirementAvailability(PackageRequirement requirement) => requirement.Kind switch
    {
        "node" => "Supported implementation",
        "mcp-tool" => McpRequirementAvailability(requirement),
        "agent-provider" => providers.Any(p => p.ProviderId == requirement.Id && p.Version == requirement.Version)
            ? "Matching host provider listed" : "Matching host provider not listed",
        "agent-profile" => AgentProfileLibrary?.List().Any(item => item.Profile.Id == requirement.Id && item.Profile.Version == requirement.Version && item.Profile.ContentHash == requirement.ContractHash) == true
            ? "Matching local profile listed" : "Exact profile is embedded in this workflow",
        _ => "Unknown requirement"
    };

    private string McpRequirementAvailability(PackageRequirement requirement)
    {
        var binding = bindings.FirstOrDefault(candidate => candidate.BindingId == requirement.Id);
        if (binding is null) return "Matching host binding not listed";
        if (binding.ContractHash != requirement.ContractHash) return "MCP input/output schema changed";
        if (requirement.DescriptionHash is not null && binding.DescriptionHash != requirement.DescriptionHash)
            return "MCP tool description changed";
        return "Matching host binding listed";
    }
}
