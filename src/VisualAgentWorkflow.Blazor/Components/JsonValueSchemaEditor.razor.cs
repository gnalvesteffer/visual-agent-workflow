using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class JsonValueSchemaEditor
{
    private static JsonValueSchema AnySchema => new("any");

    [Parameter, EditorRequired] public JsonValueSchema Schema { get; set; } = AnySchema;
    [Parameter, EditorRequired] public EventCallback<JsonValueSchema> SchemaChanged { get; set; }
    [Parameter, EditorRequired] public string EditorLabel { get; set; } = "JSON value";
    [Parameter] public int Depth { get; set; }
    [Parameter] public bool Disabled { get; set; }

    private Task ChangeType(ChangeEventArgs args)
    {
        var type = args.Value?.ToString() ?? "any";
        JsonValueSchema updated = type switch
        {
            "object" => new("object") { Properties = ImmutableArray<JsonValueProperty>.Empty },
            "array" => new("array") { Items = AnySchema },
            _ => new(type)
        };
        return SchemaChanged.InvokeAsync(updated);
    }

    private Task ChangePropertyName(int index, ChangeEventArgs args) => ChangeProperty(index,
        property => property with { Name = args.Value?.ToString() ?? "" });

    private Task ChangePropertyPointer(int index, ChangeEventArgs args) => ChangeProperty(index,
        property => property with { Pointer = args.Value?.ToString() ?? "" });

    private Task ChangeOptional(int index, ChangeEventArgs args) => ChangeProperty(index,
        property => property with { Optional = args.Value is bool value && value });

    private Task ChangePropertySchema(int index, JsonValueSchema schema) => ChangeProperty(index,
        property => property with { Schema = schema });

    private Task ChangeProperty(int index, Func<JsonValueProperty, JsonValueProperty> update)
    {
        var properties = Schema.Properties ?? ImmutableArray<JsonValueProperty>.Empty;
        properties = properties.SetItem(index, update(properties[index]));
        return SchemaChanged.InvokeAsync(Schema with { Properties = properties });
    }

    private Task ChangeItems(JsonValueSchema schema) => SchemaChanged.InvokeAsync(Schema with { Items = schema });

    private Task AddProperty()
    {
        var properties = Schema.Properties ?? [];
        var name = "value" + (properties.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (properties.Any(property => property.Name == name)) name += "_";
        return SchemaChanged.InvokeAsync(Schema with
        {
            Properties = properties.Add(new(name, "/" + name, AnySchema))
        });
    }

    private Task RemoveProperty(int index) => SchemaChanged.InvokeAsync(Schema with
    { Properties = (Schema.Properties ?? []).RemoveAt(index) });
}
