using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowInterfaceTests
{
    [Fact]
    public void InterfaceDefinition_UsesIndependentSchemaAndStableCanonicalHash()
    {
        var definition = Interface();
        var serialized = WorkflowInterfaceJson.Serialize(definition);
        var restored = WorkflowInterfaceJson.Deserialize(serialized);

        Assert.Contains("\"schemaVersion\": \"1.0\"", serialized);
        Assert.Equal(serialized, WorkflowInterfaceJson.Serialize(restored));
        Assert.Equal(definition.Operations.Count, restored.Operations.Count);
        Assert.Equal(definition.Operations["lookup"], restored.Operations["lookup"]);
        Assert.Equal(WorkflowInterfaceJson.ComputeHash(definition), WorkflowInterfaceJson.ComputeHash(restored));
        Assert.Throws<ArgumentException>(() => WorkflowInterfaceRevision.Create(definition, revisionId: Guid.Empty));
    }

    [Fact]
    public void ImplementationRevision_RoundTripsExactWorkflowPinsAndValidatesItsHash()
    {
        var lookupPin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(3,
            WorkflowRevision.Create(Workflow("lookup", WorkflowDataTypes.String, WorkflowDataTypes.String), "test")));
        var searchPin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(4,
            WorkflowRevision.Create(Workflow("search", WorkflowDataTypes.Json, WorkflowDataTypes.Json), "test")));
        var implementation = new WorkflowInterfaceImplementation("sample.impl", WorkflowInterfaceRevision.Create(Interface()),
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty
                .Add("lookup", lookupPin).Add("search", searchPin));
        var revision = WorkflowInterfaceImplementationRevision.Create(implementation, version: 2,
            revisionId: Guid.Parse("411b1958-00fd-4f45-871f-11ba3dd7a452"));

        var restored = WorkflowInterfaceJson.DeserializeImplementationRevision(WorkflowInterfaceJson.Serialize(revision));

        Assert.Equal(WorkflowInterfaceJson.Serialize(revision), WorkflowInterfaceJson.Serialize(restored));
        Assert.Equal(revision.RevisionId, restored.RevisionId);
        Assert.Equal(revision.Version, restored.Version);
        Assert.Equal(revision.ContentHash, restored.ContentHash);
        Assert.Equal(lookupPin.RevisionId, restored.Definition.Operations["lookup"].RevisionId);
        Assert.Empty(WorkflowInterfaceJson.ValidateConformance(restored.Definition));
        Assert.Throws<ArgumentException>(() => (revision with { ContentHash = new string('0', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => WorkflowInterfaceImplementationRevision.Create(implementation, revisionId: Guid.Empty));

        var lookup = WorkflowInterfaceResolver.ResolveOperation(restored, "lookup");
        var search = WorkflowInterfaceResolver.ResolveOperation(restored, "search");
        Assert.Equal("lookup", lookup.OperationId);
        Assert.Equal("search", search.OperationId);
        Assert.Equal(lookupPin.RevisionId, lookup.Workflow.RevisionId);
        Assert.Equal(searchPin.RevisionId, search.Workflow.RevisionId);
        Assert.Equal(WorkflowDataTypes.String, lookup.Signature.InputDataType);
        Assert.Equal(WorkflowDataTypes.Json, search.Signature.InputDataType);
        Assert.Throws<ArgumentException>(() => WorkflowInterfaceResolver.ResolveOperation(restored, "delete"));
    }

    [Fact]
    public void Conformance_ReportsMissingUnexpectedAndIncompatibleOperationBindings()
    {
        var lookupPin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(1,
            WorkflowRevision.Create(Workflow("lookup", WorkflowDataTypes.String, WorkflowDataTypes.String), "test")));
        var searchPin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(2,
            WorkflowRevision.Create(Workflow("search", WorkflowDataTypes.Json, WorkflowDataTypes.Json), "test")));
        var interfaceRevision = WorkflowInterfaceRevision.Create(Interface());
        var valid = new WorkflowInterfaceImplementation("sample.impl", interfaceRevision,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty
                .Add("lookup", lookupPin).Add("search", searchPin));

        var missing = valid with { Operations = valid.Operations.Remove("search") };
        var extra = valid with { Operations = valid.Operations.Add("other", lookupPin) };
        var mismatchRevision = WorkflowInterfaceRevision.Create(Interface() with
        {
            Operations = Interface().Operations.SetItem("lookup", new("Lookup", WorkflowDataTypes.Json, WorkflowDataTypes.String))
        });
        var mismatch = valid with { Interface = mismatchRevision };
        var alteredPin = valid with
        {
            Operations = valid.Operations.SetItem("lookup", lookupPin with { ContentHash = new string('0', 64) })
        };
        var alteredInterface = valid with
        {
            Interface = interfaceRevision with { ContentHash = new string('0', 64) }
        };

        Assert.Contains(WorkflowInterfaceJson.ValidateConformance(missing), issue => issue.Code == "interface.operation.missing");
        Assert.Contains(WorkflowInterfaceJson.ValidateConformance(extra), issue => issue.Code == "interface.operation.unexpected");
        Assert.Contains(WorkflowInterfaceJson.ValidateConformance(mismatch), issue => issue.Code == "interface.input.type");
        Assert.Contains(WorkflowInterfaceJson.ValidateConformance(alteredPin), issue => issue.Code == "interface.workflow.pin");
        Assert.Contains(WorkflowInterfaceJson.ValidateConformance(alteredInterface), issue => issue.Code == "interface.revision.invalid");
    }

    [Fact]
    public void InterfaceImport_RejectsUnknownSchemaAndDuplicateProperties()
    {
        var unsupported = Interface() with { SchemaVersion = "2.0" };
        Assert.Throws<ArgumentException>(() => WorkflowInterfaceJson.Serialize(unsupported));
        var duplicate = """
            {"interfaceId":"a","interfaceId":"b","displayName":"Sample","description":"","operations":{"lookup":{"displayName":"Lookup","inputDataType":"string","outputDataType":"string"}},"schemaVersion":"1.0"}
            """;
        Assert.Throws<JsonException>(() => WorkflowInterfaceJson.Deserialize(duplicate));
    }

    private static WorkflowInterfaceDefinition Interface() => new("sample.api", "Sample API", "Static workflow contract.",
        ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty
            .Add("lookup", new("Lookup", WorkflowDataTypes.String, WorkflowDataTypes.String))
            .Add("search", new("Search", WorkflowDataTypes.Json, WorkflowDataTypes.Json)));

    private static WorkflowDefinition Workflow(string workflowId, string inputType, string outputType)
    {
        return new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, workflowId, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new NodeDefinition(NodeTypes.Input, "1.0.0")
                { InputDataType = inputType == WorkflowDataTypes.String ? null : inputType },
                ["output"] = new NodeDefinition(NodeTypes.Output, "1.0.0")
                { OutputDataType = outputType == WorkflowDataTypes.String ? null : outputType }
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("result",
                new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("complete",
                new(new("input", "then"), new("output", "execute"), "execution"))
        };
    }
}
