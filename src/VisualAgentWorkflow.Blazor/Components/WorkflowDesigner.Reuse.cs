using System.Collections.Immutable;
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
    private string sequenceCallOutputId = "";
    private string newReusableWorkflowId = "workflow-" + Guid.NewGuid().ToString("N")[..8];
    private IReadOnlyList<PublishedWorkflowRevision> reusableVersions = [];
    private IReadOnlyList<PackageRequirement> reusableRequirements = [];
    private IReadOnlyList<PackageRequirement> packageRequirements = [];
    private WorkflowDefinition? packagedDefinition;
    private WorkflowLayout? packagedLayout;
    private string packageDownloadHref = "";
    private string upgradeCallNodeId = "";
    private Guid? upgradeRevisionId;
    private IReadOnlyList<PublishedWorkflowRevision> upgradeVersions = [];
    private WorkflowLayout? upgradeChildLayout;
    private bool upgradeChildLayoutIsDerived;
    private ImmutableArray<WorkflowChange> upgradeChanges = [];
    private ImmutableArray<string> upgradeRequirementChanges = [];
    private ImmutableArray<WorkflowCallParameterMapping> droppedUpgradeMappings = [];
    private string? upgradeReviewError;
    private string upgradeReviewBaseHash = "";
    private bool upgradeReviewOpen;
    private bool upgradeReviewValid;

    private WorkflowCallConfiguration? SelectedCallForUpgrade => definition is not null &&
        definition.Nodes.TryGetValue(upgradeCallNodeId, out var selectedNode) ? selectedNode.WorkflowCall : null;
    private PublishedWorkflowRevision? SelectedUpgradeRevision => upgradeVersions.FirstOrDefault(item => item.Revision.RevisionId == upgradeRevisionId);

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
        packageRequirements = []; reusableSectionId = ""; insertAfterNode = ""; sequenceCallOutputId = "";
        ResetCallUpgrade();
        newReusableWorkflowId = "workflow-" + Guid.NewGuid().ToString("N")[..8];
    }

    private void ResetCallUpgrade()
    {
        upgradeCallNodeId = ""; upgradeRevisionId = null; upgradeVersions = [];
        upgradeChildLayout = null; upgradeChildLayoutIsDerived = false;
        upgradeChanges = []; upgradeRequirementChanges = []; droppedUpgradeMappings = [];
        upgradeReviewError = null; upgradeReviewBaseHash = ""; upgradeReviewOpen = false; upgradeReviewValid = false;
    }

    private Task SelectWorkflowCallForUpgrade(ChangeEventArgs args) => Perform(async current =>
    {
        upgradeCallNodeId = args.Value?.ToString() ?? "";
        upgradeRevisionId = null; upgradeVersions = []; upgradeChildLayout = null; upgradeChildLayoutIsDerived = false; ClearUpgradeReview();
        var call = SelectedCallForUpgrade;
        if (call is null) return;
        var available = await current.Catalog.ListRevisionsAsync(call.WorkflowId).WaitAsync(current.Token);
        var storedLayout = current.Catalog is IWorkflowDesignCatalog designs
            ? await designs.GetRevisionLayoutAsync(call.RevisionId).WaitAsync(current.Token)
            : null;
        if (!Current(current) || SelectedCallForUpgrade?.RevisionId != call.RevisionId) return;
        upgradeVersions = available.OrderByDescending(item => item.Version).ToArray();
        upgradeChildLayout = storedLayout ?? WorkflowLayout.Create(call.Definition);
        upgradeChildLayoutIsDerived = storedLayout is null;
        upgradeRevisionId = upgradeVersions.FirstOrDefault(item => item.Revision.RevisionId != call.RevisionId)?.Revision.RevisionId;
    });

    private void SelectUpgradeRevision(ChangeEventArgs args)
    {
        upgradeRevisionId = Guid.TryParse(args.Value?.ToString(), out var revisionId) ? revisionId : null;
        ClearUpgradeReview();
    }

    private void ClearUpgradeReview()
    {
        upgradeChanges = []; upgradeRequirementChanges = []; droppedUpgradeMappings = [];
        upgradeReviewError = null; upgradeReviewBaseHash = ""; upgradeReviewOpen = false; upgradeReviewValid = false;
    }

    private bool IsSequenceCallDraft => definition?.SchemaVersion == WorkflowSchemaVersions.SequenceTransformChainGraph;

    private ImmutableArray<SequencePathTopology> SequenceCallPaths => definition is { } current &&
        IsSequenceCallDraft && WorkflowValidator.Validate(current).IsEmpty
        ? SequencePathTopologyAnalyzer.Analyze(current).Paths.Where(path => path.IsDirectMcpPath).ToImmutableArray()
        : [];

    private bool CanAuthorSequenceWorkflowCall => !IdeMode && preview is null && !busy &&
        reusableRevisionId is not null && SequenceCallPaths.Any();

    private WorkflowCallUpgradeResult UpgradeSelectedCall(PublishedWorkflowRevision target) =>
        definition?.SchemaVersion == WorkflowSchemaVersions.SequenceTransformChainGraph
            ? WorkflowCallEditing.UpgradeSequenceWorkflowCallDraft(definition, upgradeCallNodeId, target)
            : WorkflowCallEditing.Upgrade(definition!, upgradeCallNodeId, target);

    private void ReviewWorkflowCallUpgrade()
    {
        if (definition is null || upgradeCallNodeId.Length == 0 || SelectedCallForUpgrade is not { } call ||
            SelectedUpgradeRevision is not { } target) return;

        upgradeChanges = WorkflowDifference.Compare(call.Definition, target.Revision.Definition);
        upgradeRequirementChanges = RequirementChanges(call.Definition, target.Revision.Definition);
        upgradeReviewBaseHash = WorkflowJson.ComputeDraftHash(definition);
        upgradeReviewOpen = true;
        upgradeReviewValid = false;
        upgradeReviewError = null;
        try
        {
            var result = UpgradeSelectedCall(target);
            droppedUpgradeMappings = result.DroppedParameterMappings;
            upgradeReviewValid = true;
        }
        catch (ArgumentException exception)
        {
            upgradeReviewError = exception.Message;
        }
    }

    private void AcceptWorkflowCallUpgrade()
    {
        if (!upgradeReviewOpen || !upgradeReviewValid || preview is not null || definition is null || layout is null ||
            SelectedUpgradeRevision is not { } target) return;
        if (WorkflowJson.ComputeDraftHash(definition) != upgradeReviewBaseHash)
        {
            upgradeReviewError = "The parent draft changed after this review. Review the upgrade again before applying it.";
            upgradeReviewValid = false;
            return;
        }

        try
        {
            var result = UpgradeSelectedCall(target);
            ApplyComposition(new(result.Definition, layout));
            upgradeChildLayout = WorkflowLayout.Create(target.Revision.Definition);
            upgradeChildLayoutIsDerived = true;
            message = result.DroppedParameterMappings.IsEmpty
                ? $"Updated the call to '{target.Revision.Definition.WorkflowId}' version {target.Version}. Undo edit restores its previous pin. Save and publish the draft to make the change active."
                : $"Updated the call to version {target.Version}. {result.DroppedParameterMappings.Length} incompatible parameter mapping(s) were removed; those child parameters now use their defaults. Undo edit restores the previous pin and mappings.";
            ClearUpgradeReview();
        }
        catch (ArgumentException exception)
        {
            upgradeReviewError = exception.Message;
            upgradeReviewValid = false;
        }
    }

    private static ImmutableArray<string> RequirementChanges(WorkflowDefinition before, WorkflowDefinition after)
    {
        var previous = WorkflowPackageJson.Requirements(before).ToDictionary(RequirementKey, StringComparer.Ordinal);
        var next = WorkflowPackageJson.Requirements(after).ToDictionary(RequirementKey, StringComparer.Ordinal);
        var removed = previous.Keys.Except(next.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key => "Removed · " + RequirementSummary(previous[key]));
        var added = next.Keys.Except(previous.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key => "Added · " + RequirementSummary(next[key]));
        return removed.Concat(added).ToImmutableArray();
    }

    private static string RequirementLabel(PackageRequirement requirement) =>
        $"{requirement.Kind} · {requirement.Id}" + (requirement.Version.Length == 0 ? "" : " " + requirement.Version);

    private static string RequirementSummary(PackageRequirement requirement) =>
        RequirementLabel(requirement) +
        (requirement.ContractHash is null ? "" : " · " + requirement.ContractHash[..8]);

    private static string RequirementKey(PackageRequirement requirement) =>
        $"{requirement.Kind}|{requirement.Id}|{requirement.Version}|{requirement.ContractHash}|{requirement.DescriptionHash}";

    private string WorkflowCallLabel(string nodeId)
    {
        var label = layout?.Nodes.TryGetValue(nodeId, out var nodeLayout) == true && !string.IsNullOrWhiteSpace(nodeLayout.Label)
            ? nodeLayout.Label : nodeId;
        return definition?.Nodes.GetValueOrDefault(nodeId)?.WorkflowCall is { } call
            ? $"{label} · {call.WorkflowId} v{call.Version}"
            : $"{label} · Missing pinned revision";
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
        WorkflowComposition extracted;
        try { extracted = WorkflowReuse.Extract(definition, layout, section.NodeIds, newReusableWorkflowId); }
        catch (ArgumentException)
        {
            error = "Choose a section containing consecutive processing steps only, and a workflow ID using letters, digits, dots, dashes, or underscores.";
            return;
        }
        try { await designs.SaveDesignAsync(extracted.Definition, extracted.Layout, 0).WaitAsync(current.Token); }
        catch (WorkflowConflictException)
        {
            if (Current(current)) error = "That reusable workflow ID already exists. Choose another ID; your current draft is unchanged.";
            return;
        }
        var drafts = await current.Catalog.ListDraftsAsync().WaitAsync(current.Token);
        if (!Current(current)) return;
        workflows = drafts;
        message = $"Reusable draft '{extracted.Definition.WorkflowId}' created. Save current edits, then open it from Saved workflow and publish before reuse.";
        newReusableWorkflowId = "workflow-" + Guid.NewGuid().ToString("N")[..8];
    });

    private Task InsertReusableWorkflow() => Perform(async current =>
    {
        if (definition is null || layout is null || preview is not null || reusableRevisionId is not { } id) return;
        var consumer = definition; var presentation = layout;
        var after = consumer.Nodes.TryGetValue(insertAfterNode, out var node) && node.Type != NodeTypes.Output
            ? insertAfterNode : consumer.EntryNodeId;
        var source = await current.Catalog.GetRevisionAsync(id).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        if (source.Revision.Definition.WorkflowId != reusableWorkflowId) throw new ArgumentException("Source selection changed.");
        var sourceLayout = current.Catalog is IWorkflowDesignCatalog designs
            ? await designs.GetRevisionLayoutAsync(id).WaitAsync(current.Token) : null;
        var next = WorkflowReuse.Insert(consumer, presentation, after, source,
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
        ApplyComposition(new(next, layout.Reconcile(next)));
        message = $"Added a call to '{source.Revision.Definition.WorkflowId}' version {sourceVersion.Version}. This child revision is embedded and pinned in this workflow.";
    });

    private Task InsertSequenceWorkflowCall() => Perform(async current =>
    {
        if (!CanAuthorSequenceWorkflowCall || definition is null || layout is null || reusableRevisionId is not { } id ||
            !SequenceCallPaths.Any(path => path.OutputId == sequenceCallOutputId)) return;
        var source = await current.Catalog.GetRevisionAsync(id).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        if (source.Revision.Definition.WorkflowId != reusableWorkflowId || source.Revision.Definition.WorkflowId == definition.WorkflowId)
            throw new ArgumentException("Choose a different published workflow revision.");
        var next = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(definition, definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Sequence).Key,
            sequenceCallOutputId, source);
        if (!Current(current)) return;
        ApplyComposition(new(next, layout.Reconcile(next)));
        message = $"Added the pinned call to Sequence output '{sequenceCallOutputId}'. Save this schema 1.49 draft, then promote the WorkflowCall draft before publishing or running.";
    });

    private void ApplyComposition(WorkflowComposition next)
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
        "workflow-revision" => "Exact child revision is embedded",
        "mcp-tool" => McpRequirementAvailability(requirement),
        "agent-provider" => providers.Any(p => p.ProviderId == requirement.Id)
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
