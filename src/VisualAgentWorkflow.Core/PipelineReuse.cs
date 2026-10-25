using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record PipelineComposition(WorkflowDefinition Definition, WorkflowLayout Layout);

/// <summary>Version-pinned, copy-on-insert reuse. No executable source loading or implicit capability grant.</summary>
public static class PipelineReuse
{
    public static PipelineComposition Extract(WorkflowDefinition source, WorkflowLayout layout,
        IEnumerable<string> nodeIds, string workflowId)
    {
        WorkflowValidator.ThrowIfInvalid(source);
        WorkflowLayoutJson.Validate(layout, source);
        if (string.IsNullOrWhiteSpace(workflowId) || workflowId.Length > 128 || !char.IsAsciiLetterOrDigit(workflowId[0]) ||
            !workflowId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Choose a reusable pipeline ID containing only letters, digits, dots, dashes, or underscores.");
        var members = nodeIds.ToHashSet(StringComparer.Ordinal);
        var order = PipelineEditing.OrderedNodes(source);
        var selection = order.Where(members.Contains).ToArray();
        if (selection.Length == 0 || selection.Length != members.Count ||
            selection.Any(id => source.Nodes[id].Type is NodeTypes.Input or NodeTypes.Output))
            throw new ArgumentException("Select processing nodes only.");
        if (selection.Any(id => source.Nodes[id].Type == NodeTypes.JsonSelectString))
            throw new ArgumentException("JSON input selectors cannot yet be extracted into reusable string pipelines.");
        var start = order.IndexOf(selection[0]);
        if (!order.Skip(start).Take(selection.Length).SequenceEqual(selection))
            throw new ArgumentException("A reusable section must contain consecutive steps in execution order.");
        var input = "input-" + Guid.NewGuid().ToString("N");
        var output = "output-" + Guid.NewGuid().ToString("N");
        var nodes = selection.ToImmutableSortedDictionary(id => id, id => source.Nodes[id], StringComparer.Ordinal)
            .Add(input, new(NodeTypes.Input, "1.0.0")).Add(output, new(NodeTypes.Output, "1.0.0"));
        var definition = PipelineEditing.Reconnect(source with
        {
            WorkflowId = workflowId, EntryNodeId = input, Nodes = nodes,
            Connections = source.Connections.Where(p => members.Contains(p.Value.Source.NodeId) && members.Contains(p.Value.Target.NodeId))
                .ToImmutableSortedDictionary(StringComparer.Ordinal)
        }, new[] { input }.Concat(selection).Append(output).ToArray());
        var presentation = layout.Reconcile(definition);
        var first = layout.Nodes[selection[0]].Position;
        var last = layout.Nodes[selection[^1]].Position;
        presentation = presentation with { Nodes = presentation.Nodes
            .SetItem(input, new(new(Math.Max(0, first.X - 360), first.Y)))
            .SetItem(output, new(new(Math.Min(WorkflowLayoutJson.MaximumCoordinate, last.X + 360), last.Y))) };
        WorkflowLayoutJson.Validate(presentation, definition);
        return new(definition, presentation);
    }

    public static PipelineComposition Insert(WorkflowDefinition target, WorkflowLayout layout, string afterNodeId,
        PublishedWorkflowRevision published, WorkflowLayout sourceLayout)
    {
        WorkflowValidator.ThrowIfInvalid(target);
        WorkflowLayoutJson.Validate(layout, target);
        ArgumentNullException.ThrowIfNull(published);
        var source = published.Revision.Definition;
        WorkflowLayoutJson.Validate(sourceLayout, source);
        var sourceOrder = PipelineEditing.OrderedNodes(source);
        var steps = sourceOrder.Skip(1).SkipLast(1).ToArray();
        if (steps.Any(id => source.Nodes[id].Type == NodeTypes.JsonSelectString))
            throw new ArgumentException("JSON input selectors cannot yet be inserted as reusable string pipelines.");
        var order = PipelineEditing.OrderedNodes(target).ToList();
        var position = order.IndexOf(afterNodeId);
        if (position < 0 || position == order.Count - 1) throw new ArgumentException("Insert after a non-output node.");
        if (steps.Length == 0 || target.Nodes.Count + steps.Length > 256)
            throw new ArgumentException("Reusable pipelines require processing steps and must fit the 256-node limit.");
        if (published.Version < 1) throw new ArgumentException("A published version is required.");
        var ids = steps.ToDictionary(id => id, _ => "node-" + Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        var targetParameters = target.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var parameterMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameterId in steps.SelectMany(id => new[] { source.Nodes[id].ParameterId }
            .Concat(source.Nodes[id].Agent?.UserMessage?.Parts.Where(part => part.Kind == "parameter").Select(part => part.Value) ?? [])
            .Concat(source.Nodes[id].TextTemplate?.Parts.Where(part => part.Kind == "parameter").Select(part => part.Value) ?? []))
            .Where(id => id is not null).Distinct(StringComparer.Ordinal))
        {
            var sourceParameter = source.Parameters![parameterId!];
            var destinationId = parameterId!;
            if (targetParameters.TryGetValue(destinationId, out var existing) && existing != sourceParameter)
            {
                var sourcePrefix = string.Concat(source.WorkflowId.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '-'));
                var prefix = sourcePrefix[..Math.Min(24, sourcePrefix.Length)];
                var parameterSuffix = parameterId!.Length > 28 ? parameterId[..28] : parameterId;
                destinationId = $"{prefix}-{parameterSuffix}";
                var suffix = 2;
                while (targetParameters.TryGetValue(destinationId, out existing) && existing != sourceParameter)
                    destinationId = $"{prefix}-{parameterSuffix}-{suffix++}";
            }
            if (!targetParameters.ContainsKey(destinationId)) targetParameters = targetParameters.Add(destinationId, sourceParameter);
            parameterMap[parameterId!] = destinationId;
        }
        var nodes = target.Nodes;
        var targetSkills = target.Skills ?? ImmutableSortedDictionary<string, WorkflowSkill>.Empty;
        var targetProfiles = target.AgentProfiles ?? ImmutableSortedDictionary<string, AgentProfile>.Empty;
        foreach (var id in steps)
        {
            var sourceNode = source.Nodes[id];
            foreach (var reference in sourceNode.Agent?.Skills ?? [])
            {
                var key = reference.Id + "@" + reference.Version;
                var skill = source.Skills![key];
                if (targetSkills.TryGetValue(key, out var existingSkill) && existingSkill != skill)
                    throw new ArgumentException("The target has a different skill with the same ID and version.");
                targetSkills = targetSkills.SetItem(key, skill);
            }
            foreach (var reference in sourceNode.Agent?.Delegates ?? [])
            {
                var key = reference.Id + "@" + reference.Version;
                var profile = source.AgentProfiles![key];
                if (targetProfiles.TryGetValue(key, out var existingProfile) && existingProfile.ContentHash != profile.ContentHash)
                    throw new ArgumentException("The target has a different agent profile with the same ID and version.");
                targetProfiles = targetProfiles.SetItem(key, profile);
                foreach (var skill in profile.Skills ?? [])
                {
                    var skillKey = skill.Id + "@" + skill.Version;
                    if (targetSkills.TryGetValue(skillKey, out var existingSkill) && existingSkill != skill)
                        throw new ArgumentException("The target has a different skill with the same ID and version.");
                    targetSkills = targetSkills.SetItem(skillKey, skill);
                }
            }
            nodes = nodes.Add(ids[id], sourceNode with
            {
                ParameterId = sourceNode.ParameterId is { } parameterId ? parameterMap[parameterId] : null,
                TextTemplate = sourceNode.TextTemplate is { } textTemplate ? new(textTemplate.Parts.Select(part =>
                    part.Kind == "parameter" ? part with { Value = parameterMap[part.Value] } : part).ToImmutableArray()) : null,
                Agent = sourceNode.Agent is { UserMessage: { } prompt } agent ? agent with
                { UserMessage = new(prompt.Parts.Select(part => part.Kind == "parameter" ? part with { Value = parameterMap[part.Value] } : part).ToImmutableArray()) } : sourceNode.Agent,
                Origin = new(source.WorkflowId, published.Revision.RevisionId, published.Version, published.Revision.ContentHash, id)
            });
        }
        order.InsertRange(position + 1, steps.Select(id => ids[id]));
        var definition = PipelineEditing.Reconnect(target with
        {
            SchemaVersion = target.SchemaVersion == "1.17" || source.SchemaVersion == "1.17" ? "1.17" : target.SchemaVersion == "1.16" || source.SchemaVersion == "1.16" ? "1.16" : target.SchemaVersion == "1.15" || source.SchemaVersion == "1.15" ? "1.15" : target.SchemaVersion == "1.14" || source.SchemaVersion == "1.14" ? "1.14" : target.SchemaVersion == "1.13" || source.SchemaVersion == "1.13" ? "1.13" : target.SchemaVersion == "1.12" || source.SchemaVersion == "1.12" ? "1.12" : target.SchemaVersion == "1.11" || source.SchemaVersion == "1.11" ? "1.11" : target.SchemaVersion == "1.10" || source.SchemaVersion == "1.10" ? "1.10" : target.SchemaVersion == "1.9" || source.SchemaVersion == "1.9" ? "1.9" :
                target.SchemaVersion == "1.8" || source.SchemaVersion == "1.8" ? "1.8" :
                target.SchemaVersion == "1.7" || source.SchemaVersion == "1.7" ? "1.7" :
                target.SchemaVersion == "1.6" || parameterMap.Count > 0 ? "1.6" : "1.3",
            Nodes = nodes,
            Parameters = targetParameters.Count == 0 ? null : targetParameters,
            Skills = targetSkills.Count == 0 ? null : targetSkills,
            AgentProfiles = targetProfiles.Count == 0 ? null : targetProfiles
        }, order);
        var presentation = layout.Reconcile(definition);
        var points = steps.Select(id => sourceLayout.Nodes[id].Position)
            .Concat(source.Connections.Where(edge => ids.ContainsKey(edge.Value.Source.NodeId) && ids.ContainsKey(edge.Value.Target.NodeId))
                .Where(edge => sourceLayout.Wires.ContainsKey(edge.Key))
                .SelectMany(edge => sourceLayout.Wires[edge.Key].Points.Select(point => point.Position))).ToArray();
        var minX = points.Min(p => p.X); var minY = points.Min(p => p.Y);
        var anchor = layout.Nodes[afterNodeId].Position;
        var originX = Math.Min(anchor.X + 360, WorkflowLayoutJson.MaximumCoordinate - (points.Max(p => p.X) - minX));
        var originY = Math.Min(anchor.Y + 80, WorkflowLayoutJson.MaximumCoordinate - (points.Max(p => p.Y) - minY));
        GraphPoint Translate(GraphPoint p) => new(Math.Clamp(originX + p.X - minX, 0, WorkflowLayoutJson.MaximumCoordinate),
            Math.Clamp(originY + p.Y - minY, 0, WorkflowLayoutJson.MaximumCoordinate));
        var nodeLayout = presentation.Nodes;
        foreach (var id in steps) nodeLayout = nodeLayout.SetItem(ids[id], sourceLayout.Nodes[id] with { Position = Translate(sourceLayout.Nodes[id].Position) });
        var wires = presentation.Wires;
        foreach (var (id, edge) in source.Connections)
        {
            if (!ids.ContainsKey(edge.Source.NodeId) || !ids.ContainsKey(edge.Target.NodeId) || !sourceLayout.Wires.TryGetValue(id, out var route)) continue;
            var newId = definition.Connections.Single(p => p.Value.Source.NodeId == ids[edge.Source.NodeId] && p.Value.Target.NodeId == ids[edge.Target.NodeId]).Key;
            wires = wires.SetItem(newId, route with { Points = route.Points.Select(p => p with { Position = Translate(p.Position) }).ToImmutableArray() });
        }
        var sections = presentation.Sections;
        foreach (var section in sourceLayout.Sections.Values)
        {
            var members = section.NodeIds.Where(ids.ContainsKey).Select(id => ids[id]).ToImmutableArray();
            if (members.Length > 0) sections = sections.Add("section-" + Guid.NewGuid().ToString("N"), section with { NodeIds = members });
        }
        presentation = presentation with { Nodes = nodeLayout, Wires = wires, Sections = sections };
        WorkflowLayoutJson.Validate(presentation, definition);
        return new(definition, presentation);
    }
}
