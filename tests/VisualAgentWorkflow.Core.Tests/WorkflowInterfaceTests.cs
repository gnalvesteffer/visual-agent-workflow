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
        var pin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(3,
            WorkflowRevision.Create(Workflow(), "test")));
        var implementation = new WorkflowInterfaceImplementation("sample.impl", WorkflowInterfaceRevision.Create(Interface()),
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty
                .Add("lookup", pin).Add("search", pin));
        var revision = WorkflowInterfaceImplementationRevision.Create(implementation, version: 2,
            revisionId: Guid.Parse("411b1958-00fd-4f45-871f-11ba3dd7a452"));

        var restored = WorkflowInterfaceJson.DeserializeImplementationRevision(WorkflowInterfaceJson.Serialize(revision));

        Assert.Equal(WorkflowInterfaceJson.Serialize(revision), WorkflowInterfaceJson.Serialize(restored));
        Assert.Equal(revision.RevisionId, restored.RevisionId);
        Assert.Equal(revision.Version, restored.Version);
        Assert.Equal(revision.ContentHash, restored.ContentHash);
        Assert.Equal(pin.RevisionId, restored.Definition.Operations["lookup"].RevisionId);
        Assert.Empty(WorkflowInterfaceJson.ValidateConformance(restored.Definition));
        Assert.Throws<ArgumentException>(() => (revision with { ContentHash = new string('0', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => WorkflowInterfaceImplementationRevision.Create(implementation, revisionId: Guid.Empty));
    }

    [Fact]
    public void Conformance_ReportsMissingUnexpectedAndIncompatibleOperationBindings()
    {
        var pin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(1,
            WorkflowRevision.Create(Workflow(), "test")));
        var interfaceRevision = WorkflowInterfaceRevision.Create(Interface());
        var valid = new WorkflowInterfaceImplementation("sample.impl", interfaceRevision,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty
                .Add("lookup", pin).Add("search", pin));

        var missing = valid with { Operations = valid.Operations.Remove("search") };
        var extra = valid with { Operations = valid.Operations.Add("other", pin) };
        var mismatchRevision = WorkflowInterfaceRevision.Create(Interface() with
        {
            Operations = Interface().Operations.SetItem("lookup", new("Lookup", WorkflowDataTypes.Json, WorkflowDataTypes.String))
        });
        var mismatch = valid with { Interface = mismatchRevision };
        var alteredPin = valid with
        {
            Operations = valid.Operations.SetItem("lookup", pin with { ContentHash = new string('0', 64) })
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
            .Add("search", new("Search", WorkflowDataTypes.String, WorkflowDataTypes.String)));

    private static WorkflowDefinition Workflow()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "implementation", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("result",
                new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("complete",
                new(new("input", "then"), new("output", "execute"), "execution"))
        };
        return definition;
    }
}
