using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>An immutable interface contract. It defines signatures only; it carries no implementation or authority.</summary>
public sealed record WorkflowInterfaceDefinition(
    string InterfaceId,
    string DisplayName,
    string Description,
    ImmutableSortedDictionary<string, WorkflowInterfaceOperation> Operations)
{
    /// <summary>Independent wire-format version for interface assets.</summary>
    public string SchemaVersion { get; init; } = WorkflowInterfaceJson.AssetSchemaVersion;
}

/// <summary>A named workflow operation with the single value input/output boundaries supported today.</summary>
public sealed record WorkflowInterfaceOperation(string DisplayName, string InputDataType, string OutputDataType)
{
    /// <summary>Named required fields for interface schema 1.1. Absent on schema 1.0 operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<WorkflowInterfaceDataField>? Inputs { get; init; }

    /// <summary>Named required fields for interface schema 1.1. Absent on schema 1.0 operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<WorkflowInterfaceDataField>? Outputs { get; init; }
}

/// <summary>A named, required typed field in a schema-1.1 workflow interface operation.</summary>
public sealed record WorkflowInterfaceDataField(string FieldId, string DisplayName, string DataType);

/// <summary>An immutable, independently versioned interface contract revision.</summary>
public sealed record WorkflowInterfaceRevision(Guid RevisionId, int Version,
    WorkflowInterfaceDefinition Definition, string ContentHash)
{
    public static WorkflowInterfaceRevision Create(WorkflowInterfaceDefinition definition, int version = 1,
        Guid? revisionId = null)
    {
        WorkflowInterfaceJson.ThrowIfInvalid(definition);
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        var normalized = WorkflowInterfaceJson.NormalizeDefinition(definition);
        var revision = new WorkflowInterfaceRevision(revisionId ?? Guid.NewGuid(), version, normalized,
            WorkflowInterfaceJson.ComputeHash(normalized));
        revision.Validate();
        return revision;
    }

    public void Validate()
    {
        if (RevisionId == Guid.Empty || Version < 1)
            throw new ArgumentException("An interface revision requires a non-empty identity and positive version.");
        WorkflowInterfaceJson.ThrowIfInvalid(Definition);
        if (!WorkflowInterfaceJson.MatchesHash(Definition, ContentHash))
            throw new ArgumentException("The interface revision content does not match its hash.");
    }
}

/// <summary>Exact immutable workflow revision selected to implement one interface operation.</summary>
public sealed record WorkflowInterfaceWorkflowPin(string WorkflowId, Guid RevisionId, int Version,
    string ContentHash, WorkflowDefinition Definition)
{
    public static WorkflowInterfaceWorkflowPin From(PublishedWorkflowRevision published)
    {
        ArgumentNullException.ThrowIfNull(published);
        return new(published.Revision.Definition.WorkflowId, published.Revision.RevisionId,
            published.Version, published.Revision.ContentHash, published.Revision.Definition);
    }

    /// <summary>Captures the exact validated workflow definition selected from a project source.</summary>
    public static WorkflowInterfaceWorkflowPin From(WorkflowDefinition definition, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        WorkflowValidator.ThrowIfInvalid(definition);
        var snapshot = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        var revision = WorkflowRevision.Create(snapshot, "studio-interface-authoring");
        return new(snapshot.WorkflowId, revision.RevisionId, version, revision.ContentHash, snapshot);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkflowId) || RevisionId == Guid.Empty || Version < 1 ||
            Definition is null || Definition.WorkflowId != WorkflowId ||
            WorkflowJson.ComputeHash(Definition) != ContentHash)
            throw new ArgumentException("The workflow implementation pin is incomplete or does not match its snapshot.");
    }
}

/// <summary>
/// Static conformance binding for every operation in an interface revision. Reusing one exact
/// workflow revision for more than one operation is allowed when its boundary types match.
/// </summary>
public sealed record WorkflowInterfaceImplementation(
    string ImplementationId,
    WorkflowInterfaceRevision Interface,
    ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin> Operations);

/// <summary>An immutable, independently versioned workflow-interface implementation revision.</summary>
public sealed record WorkflowInterfaceImplementationRevision(Guid RevisionId, int Version,
    WorkflowInterfaceImplementation Definition, string ContentHash)
{
    public static WorkflowInterfaceImplementationRevision Create(WorkflowInterfaceImplementation definition,
        int version = 1, Guid? revisionId = null)
    {
        WorkflowInterfaceJson.ThrowIfInvalid(definition);
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        var normalized = WorkflowInterfaceJson.NormalizeImplementation(definition);
        var revision = new WorkflowInterfaceImplementationRevision(revisionId ?? Guid.NewGuid(), version, normalized,
            WorkflowInterfaceJson.ComputeImplementationHash(normalized));
        revision.Validate();
        return revision;
    }

    public void Validate()
    {
        if (RevisionId == Guid.Empty || Version < 1)
            throw new ArgumentException("An implementation revision requires a non-empty identity and positive version.");
        WorkflowInterfaceJson.ThrowIfInvalid(Definition);
        if (!WorkflowInterfaceJson.MatchesImplementationHash(Definition, ContentHash))
            throw new ArgumentException("The implementation revision content does not match its hash.");
    }
}

/// <summary>A statically selected operation and the exact workflow revision that implements it.</summary>
public sealed record WorkflowInterfaceResolvedOperation(
    WorkflowInterfaceRevision Interface,
    string ImplementationId,
    Guid ImplementationRevisionId,
    int ImplementationVersion,
    string ImplementationContentHash,
    string OperationId,
    WorkflowInterfaceOperation Signature,
    WorkflowInterfaceWorkflowPin Workflow);

public sealed record WorkflowInterfaceConformanceIssue(string Code, string Path, string Message);

/// <summary>Resolves statically pinned interface operations without consulting an asset catalog.</summary>
public static class WorkflowInterfaceResolver
{
    /// <summary>
    /// Reads the declared signature for pin projection without validating the embedded
    /// snapshot. Authoring and rendering can request this frequently; executable
    /// validation and runtime must use <see cref="ResolveOperation"/> instead.
    /// </summary>
    public static bool TryGetOperationSignature(WorkflowInterfaceCallConfiguration configuration,
        out WorkflowInterfaceOperation signature)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        signature = null!;
        if (string.IsNullOrWhiteSpace(configuration.OperationId)) return false;
        try
        {
            return configuration.Implementation.Definition.Interface.Definition.Operations.TryGetValue(
                    configuration.OperationId, out signature!) && signature is not null;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    public static WorkflowInterfaceResolvedOperation ResolveOperation(
        WorkflowInterfaceImplementationRevision implementationRevision, string operationId)
    {
        ArgumentNullException.ThrowIfNull(implementationRevision);
        implementationRevision.Validate();
        if (string.IsNullOrWhiteSpace(operationId) ||
            !implementationRevision.Definition.Interface.Definition.Operations.TryGetValue(operationId, out var signature))
            throw new ArgumentException("The requested operation is not declared by this interface revision.", nameof(operationId));
        if (!implementationRevision.Definition.Operations.TryGetValue(operationId, out var workflow))
            throw new ArgumentException("The requested operation has no implementation in this revision.", nameof(operationId));

        return new(implementationRevision.Definition.Interface, implementationRevision.Definition.ImplementationId,
            implementationRevision.RevisionId, implementationRevision.Version, implementationRevision.ContentHash,
            operationId, signature, workflow);
    }
}

/// <summary>Canonical JSON, hashing, and structural conformance for workflow interface assets.</summary>
public static class WorkflowInterfaceJson
{
    public const string AssetSchemaVersion = "1.0";
    public const string FieldContractSchemaVersion = "1.1";
    public const int MaximumOperations = 32;
    public const int MaximumFieldsPerDirection = 16;
    public const int MaximumIdLength = 128;
    public const int MaximumDisplayNameLength = 160;
    public const int MaximumDescriptionLength = 2_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = true
    };

    public static string Serialize(WorkflowInterfaceDefinition definition)
    {
        ThrowIfInvalid(definition);
        return JsonSerializer.Serialize(NormalizeDefinition(definition), Options).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static WorkflowInterfaceDefinition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 262_144)
            throw new ArgumentException("Workflow interface exceeds the 256 KiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var definition = JsonSerializer.Deserialize<WorkflowInterfaceDefinition>(json, Options)
            ?? throw new JsonException("A workflow interface definition is required.");
        ThrowIfInvalid(definition);
        return NormalizeDefinition(definition);
    }

    public static string Serialize(WorkflowInterfaceRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        return JsonSerializer.Serialize(revision with { Definition = NormalizeDefinition(revision.Definition) }, Options)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static WorkflowInterfaceRevision DeserializeRevision(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 262_144)
            throw new ArgumentException("Workflow interface exceeds the 256 KiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var revision = JsonSerializer.Deserialize<WorkflowInterfaceRevision>(json, Options)
            ?? throw new JsonException("A workflow interface revision is required.");
        revision.Validate();
        return revision with { Definition = NormalizeDefinition(revision.Definition) };
    }

    public static string ComputeHash(WorkflowInterfaceDefinition definition) =>
        WorkflowJson.ComputeTextHash(Serialize(definition));

    public static bool MatchesHash(WorkflowInterfaceDefinition definition, string? hash)
    {
        if (hash is null) return false;
        try { return string.Equals(ComputeHash(definition), hash, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    public static string SerializeImplementation(WorkflowInterfaceImplementation implementation)
    {
        ThrowIfInvalid(implementation);
        return JsonSerializer.Serialize(NormalizeImplementation(implementation), Options).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static WorkflowInterfaceImplementation DeserializeImplementation(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2_097_152)
            throw new ArgumentException("Workflow interface implementation exceeds the 2 MiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var implementation = JsonSerializer.Deserialize<WorkflowInterfaceImplementation>(json, Options)
            ?? throw new JsonException("A workflow interface implementation is required.");
        ThrowIfInvalid(implementation);
        return NormalizeImplementation(implementation);
    }

    public static string ComputeImplementationHash(WorkflowInterfaceImplementation implementation) =>
        WorkflowJson.ComputeTextHash(SerializeImplementation(implementation));

    public static string Serialize(WorkflowInterfaceImplementationRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        return JsonSerializer.Serialize(revision with { Definition = NormalizeImplementation(revision.Definition) }, Options)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static WorkflowInterfaceImplementationRevision DeserializeImplementationRevision(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2_097_152)
            throw new ArgumentException("Workflow interface implementation exceeds the 2 MiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var revision = JsonSerializer.Deserialize<WorkflowInterfaceImplementationRevision>(json, Options)
            ?? throw new JsonException("A workflow interface implementation revision is required.");
        revision.Validate();
        return revision with { Definition = NormalizeImplementation(revision.Definition) };
    }

    public static bool MatchesImplementationHash(WorkflowInterfaceImplementation implementation, string? hash)
    {
        if (hash is null) return false;
        try { return string.Equals(ComputeImplementationHash(implementation), hash, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    public static ImmutableArray<WorkflowInterfaceConformanceIssue> ValidateConformance(
        WorkflowInterfaceImplementation? implementation)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowInterfaceConformanceIssue>();
        void Add(string code, string path, string message) => issues.Add(new(code, path, message));
        if (implementation is null)
        {
            Add("interface.implementation.missing", "interfaceImplementation", "A workflow interface implementation is required.");
            return issues.ToImmutable();
        }

        try
        {
            if (implementation.Interface is null)
                throw new ArgumentException("An interface revision is required.");
            implementation.Interface.Validate();
        }
        catch (ArgumentException exception)
        {
            Add("interface.revision.invalid", "interfaceImplementation.interface", exception.Message);
            return issues.ToImmutable();
        }

        if (!IsValidId(implementation.ImplementationId))
            Add("interface.implementation.id", "interfaceImplementation.implementationId", "Implementation IDs must be stable ASCII identifiers up to 128 characters.");
        if (implementation.Operations is null)
        {
            Add("interface.implementation.operations", "interfaceImplementation.operations", "Operation bindings are required.");
            return issues.ToImmutable();
        }

        foreach (var operationId in implementation.Interface.Definition.Operations.Keys.Except(implementation.Operations.Keys, StringComparer.Ordinal))
            Add("interface.operation.missing", $"interfaceImplementation.operations.{operationId}", "Every interface operation must have an implementation.");
        foreach (var operationId in implementation.Operations.Keys.Except(implementation.Interface.Definition.Operations.Keys, StringComparer.Ordinal))
            Add("interface.operation.unexpected", $"interfaceImplementation.operations.{operationId}", "An implementation cannot bind an undeclared interface operation.");

        foreach (var (operationId, pin) in implementation.Operations)
        {
            var path = $"interfaceImplementation.operations.{operationId}";
            if (!implementation.Interface.Definition.Operations.TryGetValue(operationId, out var operation)) continue;
            if (pin is null)
            {
                Add("interface.workflow.missing", path, "An exact workflow revision pin is required.");
                continue;
            }
            try { pin.Validate(); }
            catch (ArgumentException exception)
            {
                Add("interface.workflow.pin", path, exception.Message);
                continue;
            }

            var workflowIssues = WorkflowValidator.Validate(pin.Definition);
            if (!workflowIssues.IsEmpty)
            {
                Add("interface.workflow.invalid", path, "The pinned implementation workflow is invalid: " +
                    string.Join("; ", workflowIssues.Select(issue => $"{issue.Path}: {issue.Message}")));
                continue;
            }

            var input = pin.Definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Input);
            var output = pin.Definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
            if (input.Value is null || output.Value is null)
            {
                Add("interface.workflow.boundary", path, "An operation implementation requires one Input and one Output boundary.");
                continue;
            }
            var inputType = input.Value.InputDataType ?? WorkflowDataTypes.String;
            var outputType = output.Value.OutputDataType ?? WorkflowDataTypes.String;
            if (!string.Equals(operation.InputDataType, inputType, StringComparison.Ordinal))
                Add("interface.input.type", path, $"Operation input is '{operation.InputDataType}', but the workflow accepts '{inputType}'.");
            if (!string.Equals(operation.OutputDataType, outputType, StringComparison.Ordinal))
                Add("interface.output.type", path, $"Operation output is '{operation.OutputDataType}', but the workflow returns '{outputType}'.");
        }
        return issues.ToImmutable();
    }

    public static void ThrowIfInvalid(WorkflowInterfaceImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        var issues = ValidateConformance(implementation);
        if (!issues.IsEmpty)
            throw new ArgumentException(string.Join("; ", issues.Select(issue => $"{issue.Code} ({issue.Path}): {issue.Message}")),
                nameof(implementation));
    }

    public static void ThrowIfInvalid(WorkflowInterfaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.SchemaVersion, AssetSchemaVersion, StringComparison.Ordinal) &&
            !string.Equals(definition.SchemaVersion, FieldContractSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported workflow interface schema '{definition.SchemaVersion}'.", nameof(definition));
        if (!IsValidId(definition.InterfaceId))
            throw new ArgumentException("Interface IDs must be stable ASCII identifiers up to 128 characters.", nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ArgumentException("Interface names must contain 1–160 characters.", nameof(definition));
        if (definition.Description is null || definition.Description.Length > MaximumDescriptionLength)
            throw new ArgumentException("Interface descriptions are limited to 2,000 characters.", nameof(definition));
        if (definition.Operations is null || definition.Operations.Count is < 1 or > MaximumOperations)
            throw new ArgumentException("Interfaces require 1–32 named operations.", nameof(definition));
        foreach (var (id, operation) in definition.Operations)
        {
            if (!IsValidId(id) || operation is null)
                throw new ArgumentException("Operation IDs must be stable ASCII identifiers up to 128 characters and operations cannot be null.", nameof(definition));
            if (string.IsNullOrWhiteSpace(operation.DisplayName) || operation.DisplayName.Length > MaximumDisplayNameLength)
                throw new ArgumentException($"Operation '{id}' must have a name of 1–160 characters.", nameof(definition));
            if (operation.InputDataType is not (WorkflowDataTypes.String or WorkflowDataTypes.Json))
                throw new ArgumentException($"Operation '{id}' input must use a workflow-supported string or JSON boundary.", nameof(definition));
            if (!WorkflowDataTypeCatalog.IsSupported(operation.OutputDataType))
                throw new ArgumentException($"Operation '{id}' output uses an unsupported workflow data type.", nameof(definition));

            if (definition.SchemaVersion == FieldContractSchemaVersion)
            {
                if (operation.InputDataType != WorkflowDataTypes.Json || operation.OutputDataType != WorkflowDataTypes.Json)
                    throw new ArgumentException($"Operation '{id}' schema 1.1 boundaries must use JSON objects.", nameof(definition));
                ValidateFields(id, "inputs", operation.Inputs);
                ValidateFields(id, "outputs", operation.Outputs);
            }
            else if (operation.Inputs is not null || operation.Outputs is not null)
            {
                throw new ArgumentException($"Operation '{id}' field contracts require interface schema 1.1.", nameof(definition));
            }
        }
    }

    private static void ValidateFields(string operationId, string direction,
        ImmutableArray<WorkflowInterfaceDataField>? fields)
    {
        if (fields is not { } declared || declared.Length is < 1 or > MaximumFieldsPerDirection)
            throw new ArgumentException($"Operation '{operationId}' requires 1–{MaximumFieldsPerDirection} {direction} fields.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in declared)
        {
            if (field is null || !IsValidId(field.FieldId) || !ids.Add(field.FieldId))
                throw new ArgumentException($"Operation '{operationId}' {direction} field IDs must be unique stable ASCII identifiers.");
            if (string.IsNullOrWhiteSpace(field.DisplayName) || field.DisplayName.Length > MaximumDisplayNameLength)
                throw new ArgumentException($"Operation '{operationId}' {direction} field names must contain 1–{MaximumDisplayNameLength} characters.");
            if (!WorkflowDataTypeCatalog.IsSupported(field.DataType))
                throw new ArgumentException($"Operation '{operationId}' {direction} field '{field.FieldId}' uses an unsupported data type.");
        }
    }

    internal static WorkflowInterfaceDefinition NormalizeDefinition(WorkflowInterfaceDefinition definition) => definition with
    {
        Operations = definition.Operations.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    internal static WorkflowInterfaceImplementation NormalizeImplementation(WorkflowInterfaceImplementation implementation) => implementation with
    {
        Interface = implementation.Interface with { Definition = NormalizeDefinition(implementation.Interface.Definition) },
        Operations = implementation.Operations.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= MaximumIdLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
