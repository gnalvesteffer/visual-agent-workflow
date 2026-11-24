using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Host-owned Sequence drain option (Decision 0070 review, 2026-11-24): validation, compatibility, defaults.</summary>
public sealed class SequenceDrainOptionsTests
{
    [Fact]
    public void Default_options_leave_the_grace_unbounded()
    {
        Assert.Null(new SequenceDrainOptions().AbandonGrace);
        Assert.Null(new SequenceDrainOptions(null).AbandonGrace);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(86_400_000)]
    public void A_positive_finite_grace_up_to_the_maximum_is_accepted(int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds),
            new SequenceDrainOptions(TimeSpan.FromMilliseconds(milliseconds)).AbandonGrace);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5_000)]
    [InlineData(86_400_001)]
    public void A_zero_negative_or_oversized_grace_is_rejected_at_construction(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SequenceDrainOptions(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void An_infinite_grace_is_rejected_because_null_already_means_unbounded() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SequenceDrainOptions(Timeout.InfiniteTimeSpan));

    [Fact]
    public void Existing_public_constructors_keep_their_exact_signatures()
    {
        var runtime = typeof(LocalWorkflowRuntime).GetConstructors();
        var original = new[]
        {
            typeof(INodeToolBroker), typeof(IAgentNodeExecutor), typeof(ParallelCollaborationRuntimeOptions),
            typeof(IWorkflowAdmissionSnapshotProvider)
        };
        foreach (var journal in new[] { typeof(IWorkflowRunStore), typeof(FileRunJournal) })
            Assert.Contains(runtime, ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([journal, typeof(ILogger<LocalWorkflowRuntime>), .. original]));

        var runner = Assert.Single(typeof(WorkflowRunner).GetConstructors());
        Assert.Equal([typeof(INodeToolBroker), typeof(IAgentNodeExecutor), typeof(ParallelCollaborationRuntimeOptions)],
            runner.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(runner.GetParameters(), parameter => Assert.True(parameter.IsOptional));
    }

    [Fact]
    public void The_new_runtime_constructor_adds_only_a_required_options_parameter()
    {
        var added = Assert.Single(typeof(LocalWorkflowRuntime).GetConstructors(), ctor =>
            ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(SequenceDrainOptions)));

        var last = added.GetParameters()[^1];
        Assert.Equal(typeof(SequenceDrainOptions), last.ParameterType);
        Assert.False(last.IsOptional);
    }

    [Fact]
    public void The_public_runner_surface_does_not_expose_the_grace()
    {
        Assert.DoesNotContain(typeof(WorkflowRunner).GetProperties(), property => property.Name == "SequenceAbandonGrace");
        Assert.DoesNotContain(typeof(WorkflowRunner).GetConstructors(), ctor =>
            ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(SequenceDrainOptions)));
    }

    [Fact]
    public void The_local_runtime_registration_defaults_to_an_unbounded_grace_and_honors_a_host_value()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-drain-options-{Guid.NewGuid():N}");
        try
        {
            using var defaults = new ServiceCollection().AddLocalWorkflowRuntime(directory).BuildServiceProvider();
            Assert.Null(defaults.GetRequiredService<SequenceDrainOptions>().AbandonGrace);

            var services = new ServiceCollection();
            services.AddSingleton(new SequenceDrainOptions(TimeSpan.FromSeconds(5)));
            using var configured = services.AddLocalWorkflowRuntime(directory).BuildServiceProvider();
            Assert.Equal(TimeSpan.FromSeconds(5), configured.GetRequiredService<SequenceDrainOptions>().AbandonGrace);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
