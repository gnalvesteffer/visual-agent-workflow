using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor;

public static class PipelineTemplates
{
    public static WorkflowDefinition Create(string operation)
    {
        string[] types = operation switch
        {
            "uppercase" => [NodeTypes.Uppercase],
            "reverse" => [NodeTypes.Reverse],
            "uppercase-reverse" => [NodeTypes.Uppercase, NodeTypes.Reverse],
            _ => throw new ArgumentException("Unknown pipeline template.", nameof(operation))
        };
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        var connections = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        var previous = "input";
        for (var i = 0; i < types.Length; i++)
        {
            var id = $"step-{i + 1}";
            nodes[id] = new(types[i], "1.0.0");
            connections[$"link-{i + 1}"] = new(new(previous, "out"), new(id, "in"), "data");
            previous = id;
        }
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        connections["link-output"] = new(new(previous, "out"), new("output", "in"), "data");
        return new("1.0", "text-pipeline", "input", nodes.ToImmutable(), connections.ToImmutable());
    }
}
