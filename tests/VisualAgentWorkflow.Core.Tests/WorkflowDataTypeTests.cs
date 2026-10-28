using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowDataTypeTests
{
    [Fact]
    public void Catalog_contains_only_the_supported_portable_graph_types()
    {
        Assert.Equal(
            new[] { WorkflowDataTypes.String, WorkflowDataTypes.Boolean, WorkflowDataTypes.Integer, WorkflowDataTypes.Json },
            WorkflowDataTypeCatalog.All.Select(contract => contract.Id));
        Assert.All(WorkflowDataTypeCatalog.All, contract =>
        {
            Assert.Equal("1.0.0", contract.TypeVersion);
            Assert.False(contract.SupportsNull);
            Assert.False(contract.SupportsImplicitConversion);
        });
        Assert.True(WorkflowDataTypeCatalog.Get(WorkflowDataTypes.Json).IsContainer);
    }

    [Fact]
    public void Compatibility_requires_exact_known_type_identity()
    {
        Assert.True(WorkflowDataTypeCatalog.AreCompatible(WorkflowDataTypes.String, WorkflowDataTypes.String));
        Assert.True(WorkflowDataTypeCatalog.AreCompatible(WorkflowDataTypes.Json, WorkflowDataTypes.Json));
        Assert.False(WorkflowDataTypeCatalog.AreCompatible(WorkflowDataTypes.String, WorkflowDataTypes.Json));
        Assert.False(WorkflowDataTypeCatalog.AreCompatible("number", WorkflowDataTypes.String));
        Assert.False(WorkflowDataTypeCatalog.AreCompatible(null, WorkflowDataTypes.String));
        Assert.Throws<KeyNotFoundException>(() => WorkflowDataTypeCatalog.Get("number"));
    }
}
