using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Studio authoring metadata for one immutable graph-function revision.</summary>
public sealed record StudioGraphFunctionAsset(string FormatVersion, GraphFunctionCatalogEntry Item, WorkflowLayout Layout)
{
    public const string CurrentFormatVersion = "1.0";

    public static StudioGraphFunctionAsset Create(GraphFunctionCatalogEntry item, WorkflowLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var definition = item.Function.Definition;
        var draft = Draft(definition);
        var normalizedLayout = layout is null
            ? WorkflowLayout.Create(draft)
            : WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(layout, draft), draft);
        return new(CurrentFormatVersion, item, normalizedLayout);
    }

    public static StudioGraphFunctionAsset Deserialize(ReadOnlySpan<byte> bytes, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Project graph function must be a JSON object.");

        if (!document.RootElement.EnumerateObject().Any(property =>
            property.Name.Equals("formatVersion", StringComparison.OrdinalIgnoreCase)))
        {
            var legacyItem = JsonSerializer.Deserialize<GraphFunctionCatalogEntry>(bytes, options)
                ?? throw new InvalidDataException("Project graph function is empty.");
            return Create(legacyItem);
        }

        var asset = JsonSerializer.Deserialize<StudioGraphFunctionAsset>(bytes, options)
            ?? throw new InvalidDataException("Project graph function is empty.");
        if (asset.FormatVersion != CurrentFormatVersion || asset.Item?.Function?.Definition is null || asset.Layout is null)
            throw new InvalidDataException("Project graph function is incomplete or uses an unsupported asset format version.");
        return Create(asset.Item, asset.Layout);
    }

    public static WorkflowDefinition Draft(GraphFunctionDefinition definition) => new(
        WorkflowSchemaVersions.PureGraphFunctions,
        "function-editor",
        definition.Nodes.Keys.FirstOrDefault() ?? throw new ArgumentException("Graph function must contain nodes.", nameof(definition)),
        definition.Nodes.ToImmutableSortedDictionary(StringComparer.Ordinal),
        definition.Connections.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Graph function asset contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
        }
    }
}
