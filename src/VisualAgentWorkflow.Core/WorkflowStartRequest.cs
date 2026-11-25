using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// A host-built request to start one named interface operation (Decision 0080 first slice). It is data only:
/// no run, authorization, or actor identity lives here. <see cref="FieldsJson"/> is a bounded JSON object of
/// named field values.
/// </summary>
public sealed record WorkflowStartRequest(string OperationId, string InterfaceId, Guid InterfaceRevisionId,
    string FieldsJson)
{
    /// <summary>Optional pin; when set it must equal the implementation revision being resolved.</summary>
    public Guid? ImplementationRevisionId { get; init; }
}

public sealed record WorkflowStartRequestError(string Code, string Path, string Message);

/// <summary>
/// Either a typed error list or the mapped entry input plus the resolved operation. The entry input is a compact
/// JSON object of every declared field in declared order (even for one field), valid for the JSON Input node that
/// every schema-1.1 operation binds to; it is exactly the text a caller would pass to a start today.
/// </summary>
public sealed record WorkflowStartRequestResult(ImmutableArray<WorkflowStartRequestError> Errors,
    string? EntryInput, WorkflowInterfaceResolvedOperation? Operation)
{
    public bool Succeeded => Errors.IsEmpty && EntryInput is not null;
}

/// <summary>Pure validation and mapping of a <see cref="WorkflowStartRequest"/>; never starts anything or throws on bad input.</summary>
public static class WorkflowStartRequestValidator
{
    /// <summary>Same bound the runtime applies to any entry input.</summary>
    public const int MaximumEntryInputCharacters = 16_384;
    public const int MaximumDepth = 32;

    public static WorkflowStartRequestResult Validate(WorkflowStartRequest request,
        WorkflowInterfaceImplementationRevision implementation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(implementation);
        if (!IsId(request.OperationId))
            return Fail("unknown_operation", "operationId", "The operation id is not a valid identifier.");
        if (request.ImplementationRevisionId is { } pinned && pinned != implementation.RevisionId)
            return Fail("stale_implementation_revision", "implementationRevisionId",
                "The pinned implementation revision is not the one being resolved.");

        WorkflowInterfaceResolvedOperation operation;
        try
        {
            var iface = implementation.Definition.Interface;
            if (!string.Equals(request.InterfaceId, iface.Definition.InterfaceId, StringComparison.Ordinal))
                return Fail("unknown_interface", "interfaceId", "The interface id does not match the implementation.");
            if (request.InterfaceRevisionId != iface.RevisionId)
                return Fail("stale_interface_revision", "interfaceRevisionId",
                    "The pinned interface revision is not the one this implementation conforms to.");
            operation = WorkflowInterfaceResolver.ResolveOperation(implementation, request.OperationId);
        }
        catch (Exception error) when (error is ArgumentException or NullReferenceException)
        {
            // ResolveOperation re-validates the revision; distinguish an unknown operation from a corrupt revision.
            return Fail(IsDeclared(implementation, request.OperationId) ? "invalid_implementation" : "unknown_operation",
                "operationId", "The operation cannot be resolved against this implementation revision.");
        }

        if (operation.Signature.Inputs is not { } fields || fields.IsDefaultOrEmpty)
            return Fail("no_field_contract", "operationId", "The operation declares no named input fields.");

        var errors = ImmutableArray.CreateBuilder<WorkflowStartRequestError>();
        var json = request.FieldsJson;
        if (json is null || json.Length == 0)
            return Fail("invalid_request", "fields", "Fields must be a JSON object.");
        if (json.Length > MaximumEntryInputCharacters)
            return Fail("too_large", "fields", $"Fields exceed {MaximumEntryInputCharacters} characters.");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth }); }
        catch (JsonException) { return Fail("invalid_request", "fields", "Fields must be valid JSON no deeper than 32 levels."); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Fail("invalid_request", "fields", "Fields must be a JSON object.");

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var declared = fields.ToDictionary(field => field.FieldId, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var path = "fields." + Truncate(property.Name);
                if (!declared.ContainsKey(property.Name))
                    errors.Add(new("unknown_field", path, "The field is not declared by the operation."));
                else if (!values.TryAdd(property.Name, property.Value))
                    errors.Add(new("duplicate_field", path, "The field appears more than once."));
            }

            foreach (var field in fields)
            {
                if (!values.TryGetValue(field.FieldId, out var value))
                    errors.Add(new("missing_field", "fields." + field.FieldId, "A required field is missing."));
                else if (!Matches(field.DataType, value))
                    errors.Add(new("wrong_type", "fields." + field.FieldId, $"The field must be of type {field.DataType}."));
            }
            if (errors.Count > 0) return new(errors.ToImmutable(), null, operation);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var field in fields)
                {
                    writer.WritePropertyName(field.FieldId);
                    var value = values[field.FieldId];
                    switch (field.DataType)
                    {
                        case WorkflowDataTypes.Integer: writer.WriteNumberValue(value.GetInt64()); break;
                        case WorkflowDataTypes.Number: writer.WriteNumberValue(value.GetDouble()); break;
                        default: value.WriteTo(writer); break;
                    }
                }
                writer.WriteEndObject();
            }
            var mapped = Encoding.UTF8.GetString(buffer.ToArray());
            return mapped.Length > MaximumEntryInputCharacters
                ? Fail("too_large", "fields", $"The mapped entry input exceeds {MaximumEntryInputCharacters} characters.", operation)
                : new(ImmutableArray<WorkflowStartRequestError>.Empty, mapped, operation);
        }
    }

    private static bool Matches(string dataType, JsonElement value) => dataType switch
    {
        WorkflowDataTypes.String => value.ValueKind == JsonValueKind.String,
        WorkflowDataTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        WorkflowDataTypes.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        WorkflowDataTypes.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d),
        WorkflowDataTypes.Json => value.ValueKind != JsonValueKind.Null,
        _ => false
    };

    private static bool IsDeclared(WorkflowInterfaceImplementationRevision implementation, string operationId)
    {
        try { return implementation.Definition.Operations.ContainsKey(operationId); }
        catch (NullReferenceException) { return false; }
    }

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "…";

    private static bool IsId(string? value) => value is { Length: > 0 and <= WorkflowInterfaceJson.MaximumIdLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static WorkflowStartRequestResult Fail(string code, string path, string message,
        WorkflowInterfaceResolvedOperation? operation = null) =>
        new([new(code, path, message)], null, operation);
}
