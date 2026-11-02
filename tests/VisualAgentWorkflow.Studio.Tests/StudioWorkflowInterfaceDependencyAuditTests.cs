using System.Collections.Immutable;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioWorkflowInterfaceDependencyAuditTests
{
    [Fact]
    public void Audit_accepts_exact_assets_and_does_not_flag_a_newer_revision_alone()
    {
        var call = CreateCall();
        var newerInterface = WorkflowInterfaceRevision.Create(call.Implementation.Definition.Interface.Definition, 2);
        var newerImplementation = WorkflowInterfaceImplementationRevision.Create(call.Implementation.Definition, 2);

        var issues = Audit(call, [newerInterface], [newerImplementation]);

        Assert.Empty(issues);
        Assert.Empty(Audit(call, [call.Implementation.Definition.Interface], [call.Implementation]));
    }

    [Fact]
    public void Audit_reports_missing_and_same_version_drift_separately_for_interface_and_implementation()
    {
        var call = CreateCall();
        var embeddedInterface = call.Implementation.Definition.Interface;
        var changedInterface = WorkflowInterfaceRevision.Create(embeddedInterface.Definition with { Description = "Changed" }, embeddedInterface.Version);
        var changedImplementation = WorkflowInterfaceImplementationRevision.Create(
            call.Implementation.Definition with { Interface = changedInterface }, call.Implementation.Version);

        var missing = Audit(call, [], []);
        Assert.Contains(missing, issue => issue.Kind == StudioWorkflowInterfaceDependencyIssueKind.Missing && issue.AssetKind == "workflow-interface");
        Assert.Contains(missing, issue => issue.Kind == StudioWorkflowInterfaceDependencyIssueKind.Missing && issue.AssetKind == "workflow-interface-implementation");

        var drift = Audit(call, [changedInterface], [changedImplementation]);
        Assert.Contains(drift, issue => issue.Kind == StudioWorkflowInterfaceDependencyIssueKind.Drift && issue.AssetKind == "workflow-interface");
        Assert.Contains(drift, issue => issue.Kind == StudioWorkflowInterfaceDependencyIssueKind.Drift && issue.AssetKind == "workflow-interface-implementation");
        Assert.All(drift, issue => Assert.Equal("caller/call", issue.NestedPath));
    }

    private static ImmutableArray<StudioWorkflowInterfaceDependencyIssue> Audit(
        WorkflowInterfaceCallConfiguration call, IEnumerable<WorkflowInterfaceRevision> interfaces,
        IEnumerable<WorkflowInterfaceImplementationRevision> implementations)
    {
        var definition = WorkflowTemplates.Create("uppercase-reverse") with
        {
            WorkflowId = "caller",
            Nodes = WorkflowTemplates.Create("uppercase-reverse").Nodes.Add("call",
                new NodeDefinition(NodeTypes.WorkflowInterfaceCall, "1.0.0") { WorkflowInterfaceCall = call })
        };
        return StudioWorkflowInterfaceDependencyAudit.Assess([("caller", "Caller", definition)], interfaces, implementations);
    }

    private static WorkflowInterfaceCallConfiguration CreateCall()
    {
        var contract = new WorkflowInterfaceDefinition("sample.api", "Sample API", "Contract",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty
                .Add("lookup", new("Lookup", WorkflowDataTypes.String, WorkflowDataTypes.String)));
        var contractRevision = WorkflowInterfaceRevision.Create(contract, 1);
        var pin = WorkflowInterfaceWorkflowPin.From(WorkflowTemplates.Create("uppercase-reverse") with { WorkflowId = "child" });
        var implementation = WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation(
            "sample.impl", contractRevision,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("lookup", pin)), 1);
        return new(implementation, "lookup");
    }
}
