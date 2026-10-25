using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using VisualAgentWorkflow.Mcp;

namespace ExtensionMathPlugin;

public sealed class MathWorkflowExtension : IWorkflowExtension
{
    public WorkflowExtensionDescriptor Descriptor { get; } = new("sample.math", "1.0.0");

    public IReadOnlyList<McpServerTool> CreateTools(IWorkflowExtensionContext context)
    {
        var tools = new MathTools(context);
        return
        [
            CreateTool(nameof(MathTools.Add), tools),
            CreateTool(nameof(MathTools.AddViaHostCapability), tools)
        ];
    }

    private static McpServerTool CreateTool(string methodName, object target) =>
        McpServerTool.Create(typeof(MathTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!, target);

    private sealed class MathTools(IWorkflowExtensionContext context)
    {
        [McpServerTool(Name = "sample.math.add", ReadOnly = true, Destructive = false),
         Description("Adds two integers deterministically in the isolated extension worker.")]
        public int Add(int left, int right) => checked(left + right);

        [McpServerTool(Name = "sample.math.add_via_host", ReadOnly = true, Destructive = false),
         Description("Adds two integers by invoking the separately assigned sandbox.add host capability.")]
        public async Task<string> AddViaHostCapability(int left, int right, CancellationToken cancellationToken)
        {
            var capabilities = await context.ListToolsAsync(cancellationToken);
            if (capabilities.Count != 1 || capabilities[0].BindingId != "sandbox.add")
                throw new InvalidOperationException("The host did not assign the sandbox.add capability.");

            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { left, right }));
            return await context.InvokeMcpToolAsync("sandbox.add", arguments.RootElement, cancellationToken);
        }
    }
}
