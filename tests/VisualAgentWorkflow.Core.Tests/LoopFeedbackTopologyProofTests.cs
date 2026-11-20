using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class LoopFeedbackTopologyProofTests
{
    [Fact]
    public void Supported_loop_topology_matches_after_public_validation_and_package_round_trips()
    {
        var definition = SupportedExplicitLoop();

        Assert.Empty(WorkflowValidator.Validate(definition));
        var beforePackage = LoopFeedbackTopologyProofAnalyzer.Analyze(definition,
            LoopFeedbackPinShape.ExplicitFeedbackInput);
        Assert.True(beforePackage.IsValid, string.Join(Environment.NewLine, beforePackage.Issues));

        var packageJson = WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition));
        var imported = WorkflowPackageJson.Deserialize(packageJson);
        Assert.Empty(WorkflowValidator.Validate(imported.Definition));
        var afterPackage = LoopFeedbackTopologyProofAnalyzer.Analyze(imported.Definition,
            LoopFeedbackPinShape.ExplicitFeedbackInput);

        var release = WorkflowProjectPackageJson.Read(WorkflowProjectPackageJson.CreateArchive("project",
            "Loop proof", [imported]));
        var releasedDefinition = release.Workflows[definition.WorkflowId].Definition;
        Assert.Empty(WorkflowValidator.Validate(releasedDefinition));
        var afterRelease = LoopFeedbackTopologyProofAnalyzer.Analyze(releasedDefinition,
            LoopFeedbackPinShape.ExplicitFeedbackInput);

        Assert.True(afterPackage.IsValid, string.Join(Environment.NewLine, afterPackage.Issues));
        Assert.Equal(ProjectRegions(beforePackage), ProjectRegions(afterPackage));
        Assert.True(afterRelease.IsValid, string.Join(Environment.NewLine, afterRelease.Issues));
        Assert.Equal(ProjectRegions(beforePackage), ProjectRegions(afterRelease));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(releasedDefinition));
    }

    [Fact]
    public void Nested_multi_exit_topology_is_derived_but_not_publicly_serializable()
    {
        var definition = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(
            ThreeNestedLoopsWithSequenceExitsCandidate());
        var plan = LoopFeedbackTopologyProofAnalyzer.Analyze(definition,
            LoopFeedbackPinShape.ExplicitFeedbackInput);
        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.Issues));
        var outer = Assert.Single(plan.Regions, region => region.LoopNodeId == "outer-loop");
        var middle = Assert.Single(plan.Regions, region => region.LoopNodeId == "inner-loop");
        var inner = Assert.Single(plan.Regions, region => region.LoopNodeId == "deep-loop");
        Assert.Null(outer.ParentLoopNodeId);
        Assert.Equal("outer-loop", middle.ParentLoopNodeId);
        Assert.Equal("inner-loop", inner.ParentLoopNodeId);
        Assert.Equal(["inner-abort", "inner-break"], middle.BreakRoutes
            .Select(route => route.BreakNodeId).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(WorkflowValidator.Validate(definition), issue =>
            issue.Path == "nodes.inner-break" && issue.Code == "node.unsupported");
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(definition));
        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Create(definition,
            WorkflowLayout.Create(definition)));
    }

    [Fact]
    public void Structured_activation_plan_derives_loop_and_break_ownership_from_the_graph()
    {
        var definition = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(
            ThreeNestedLoopsWithSequenceExitsCandidate());

        var created = StructuredActivationGraphPlan.TryCreate(definition, out var plan, out var issues);

        Assert.True(created, string.Join(Environment.NewLine, issues));
        Assert.NotNull(plan);
        Assert.Equal("outer-loop", plan.RegionsByLoopNodeId["inner-loop"].ParentLoopNodeId);
        Assert.Equal("inner-loop", plan.RegionsByLoopNodeId["deep-loop"].ParentLoopNodeId);
        Assert.Equal("outer-loop", plan.OwningLoopByBodyNodeId["inner-loop"]);
        Assert.Equal("inner-loop", plan.OwningLoopByBodyNodeId["deep-loop"]);
        Assert.Equal("inner-loop", plan.TargetLoopByBreakNodeId["inner-break"]);
        Assert.Equal("inner-loop", plan.TargetLoopByBreakNodeId["inner-abort"]);
    }

    [Fact]
    public void Structured_activation_plan_rejects_invalid_topology_and_non_unified_schema()
    {
        var definition = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(
            ThreeNestedLoopsWithSequenceExitsCandidate());
        var missingReturn = definition with
        {
            ExecutionConnections = definition.ExecutionConnections!.Remove("deep-return")
        };

        Assert.False(StructuredActivationGraphPlan.TryCreate(missingReturn, out var invalidPlan,
            out var topologyIssues));
        Assert.Null(invalidPlan);
        Assert.NotEmpty(topologyIssues);

        Assert.False(StructuredActivationGraphPlan.TryCreate(definition with { SchemaVersion = "1.8" },
            out var legacyPlan, out var schemaIssues));
        Assert.Null(legacyPlan);
        Assert.Contains(schemaIssues, issue => issue.Code == "activation.plan.schema");
    }

    [Fact]
    public void Structured_activation_plan_resolves_the_existing_inferred_feedback_shape()
    {
        var created = StructuredActivationGraphPlan.TryCreate(NestedLoopCandidate(), out var plan,
            out var issues);

        Assert.True(created, string.Join(Environment.NewLine, issues));
        Assert.NotNull(plan);
        Assert.Equal("outer-return", plan.RegionsByLoopNodeId["outer-loop"].FeedbackConnectionId);
        Assert.Equal("inner-return", plan.RegionsByLoopNodeId["inner-loop"].FeedbackConnectionId);
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Nested_loop_feedback_candidates_have_the_same_ownership(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? NestedLoopCandidate()
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(NestedLoopCandidate());

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        var outer = Assert.Single(proof.Regions, region => region.LoopNodeId == "outer-loop");
        var inner = Assert.Single(proof.Regions, region => region.LoopNodeId == "inner-loop");
        Assert.Null(outer.ParentLoopNodeId);
        Assert.Equal("outer-loop", inner.ParentLoopNodeId);
        Assert.Equal("outer-body", outer.BodyAnchorNodeId);
        Assert.Equal("inner-body", inner.BodyAnchorNodeId);
        Assert.Equal("outer-return", outer.FeedbackConnectionId);
        Assert.Equal("inner-return", inner.FeedbackConnectionId);
        Assert.Equal("start-outer", outer.InitialConnectionId);
        Assert.Equal("outer-inner", inner.InitialConnectionId);
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Nested_sequence_break_candidates_keep_feedback_ownership_and_nearest_break(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        var outer = Assert.Single(proof.Regions, region => region.LoopNodeId == "outer-loop");
        var inner = Assert.Single(proof.Regions, region => region.LoopNodeId == "inner-loop");
        Assert.Equal("outer-loop", inner.ParentLoopNodeId);
        Assert.Equal("outer-return", outer.FeedbackConnectionId);
        Assert.Equal("inner-return", inner.FeedbackConnectionId);
        Assert.Equal(["inner-break"], inner.BreakNodeIds.ToArray());
        Assert.Empty(outer.BreakNodeIds);
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Three_nested_loops_with_multi_exit_sequence_have_identical_ownership(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = ThreeNestedLoopsWithSequenceExitsCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        var outer = Assert.Single(proof.Regions, region => region.LoopNodeId == "outer-loop");
        var middle = Assert.Single(proof.Regions, region => region.LoopNodeId == "inner-loop");
        var inner = Assert.Single(proof.Regions, region => region.LoopNodeId == "deep-loop");
        Assert.Null(outer.ParentLoopNodeId);
        Assert.Equal("outer-loop", middle.ParentLoopNodeId);
        Assert.Equal("inner-loop", inner.ParentLoopNodeId);
        Assert.Equal("outer-return", outer.FeedbackConnectionId);
        Assert.Equal("inner-return", middle.FeedbackConnectionId);
        Assert.Equal("deep-return", inner.FeedbackConnectionId);
        Assert.Equal(["inner-abort", "inner-break"], middle.BreakNodeIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(inner.BreakNodeIds);
        Assert.Empty(outer.BreakNodeIds);
    }

    [Fact]
    public void Three_nested_loop_migration_preserves_all_authored_identities_and_non_loop_edges()
    {
        var source = ThreeNestedLoopsWithSequenceExitsCandidate();
        var migrated = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(source);
        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(migrated,
            LoopFeedbackPinShape.ExplicitFeedbackInput);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        Assert.Equal(source.Nodes.Keys, migrated.Nodes.Keys);
        Assert.Equal(source.Connections.Keys, migrated.Connections.Keys);
        Assert.Equal(source.ExecutionConnections!.Keys, migrated.ExecutionConnections!.Keys);
        var feedbackConnections = proof.Regions
            .Select(region => region.FeedbackConnectionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (connectionId, sourceEdge) in source.ExecutionConnections)
        {
            var migratedEdge = migrated.ExecutionConnections[connectionId];
            Assert.Equal(sourceEdge.Source, migratedEdge.Source);
            Assert.Equal(feedbackConnections.Contains(connectionId)
                    ? "feedback"
                    : sourceEdge.Target.PortId,
                migratedEdge.Target.PortId);
            Assert.Equal(sourceEdge.Target.NodeId, migratedEdge.Target.NodeId);
        }
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Sequence_break_topology_candidate_is_not_publicly_admitted(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        Assert.Contains(WorkflowValidator.Validate(candidate), issue =>
            issue.Path == "nodes.inner-break" && issue.Code == "node.unsupported");
        Assert.Throws<ArgumentException>(() =>
            WorkflowPackageJson.Create(candidate, WorkflowLayout.Create(candidate)));
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Conditional_sequence_route_can_end_in_nearest_loop_break(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopConditionalSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        var inner = Assert.Single(proof.Regions.Where(region => region.LoopNodeId == "inner-loop"));
        Assert.Equal(["inner-break"], inner.BreakNodeIds.ToArray());
        var breakRoute = Assert.Single(inner.BreakRoutes);
        Assert.Equal("inner-loop", breakRoute.LoopNodeId);
        Assert.Equal("inner-break", breakRoute.BreakNodeId);
        Assert.Equal("inner-sequence", breakRoute.SequenceNodeId);
        Assert.Equal("then.route", breakRoute.SequenceOutputPinId);
        Assert.Empty(proof.Regions.Single(region => region.LoopNodeId == "outer-loop").BreakNodeIds);
        Assert.Contains(WorkflowValidator.Validate(candidate), issue =>
            issue.Code == "node.unsupported" && issue.Path == "nodes.inner-break");
        Assert.Throws<ArgumentException>(() =>
            WorkflowPackageJson.Create(candidate, WorkflowLayout.Create(candidate)));
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Break_without_sequence_output_owner_is_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopConditionalSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var execution = candidate.ExecutionConnections!
            .Remove("inner-sequence-start")
            .Remove("inner-route")
            .Add("condition-start", Edge("inner-body", "then", "break-condition", "execute"));
        candidate = candidate with { ExecutionConnections = execution };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.break-owner" &&
            issue.Path == "nodes.inner-break");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Multiple_sequence_breaks_retain_distinct_output_owners(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var sequence = inferred.Nodes["inner-sequence"];
        var nodes = inferred.Nodes.SetItem("inner-sequence", sequence with
        {
            Sequence = sequence.Sequence! with
            {
                Outputs = sequence.Sequence.Outputs.Add(new("break-again", "Break again"))
            }
        }).Add("inner-break-again", new NodeDefinition("vaw.proof.array-break", "proof-only"));
        var extended = inferred with
        {
            Nodes = nodes,
            ExecutionConnections = inferred.ExecutionConnections!.Add("inner-break-again",
                Edge("inner-sequence", "then.break-again", "inner-break-again", "execute"))
        };
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? extended
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(extended);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        var inner = Assert.Single(proof.Regions.Where(region => region.LoopNodeId == "inner-loop"));
        Assert.Equal(["inner-break", "inner-break-again"], inner.BreakRoutes
            .Select(route => route.BreakNodeId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("then.break", inner.BreakRoutes
            .Single(route => route.BreakNodeId == "inner-break").SequenceOutputPinId);
        Assert.Equal("then.break-again", inner.BreakRoutes
            .Single(route => route.BreakNodeId == "inner-break-again").SequenceOutputPinId);
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Disconnected_sequence_break_route_is_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var basis = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var nodes = basis.Nodes
            .Add("orphan-sequence", new NodeDefinition(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("orphan", "Unreachable Break route")])
            })
            .Add("orphan-break", new("vaw.proof.array-break", "proof-only"));
        var candidate = basis with
        {
            Nodes = nodes,
            ExecutionConnections = basis.ExecutionConnections!.Add("orphan-sequence-start",
                Edge("orphan-sequence", "then.orphan", "orphan-break", "execute"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.unreachable" &&
            issue.Path == "nodes.orphan-break");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Shared_sequence_output_routes_are_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var shared = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.SetItem("inner-break",
                Edge("inner-sequence", "then.break", "inner-step", "execute"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(shared, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code is "loop.feedback.multiple-owner" or
            "loop.feedback.cardinality");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Sequence_break_terminal_cannot_have_a_successor(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var fallthrough = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.Add("break-fallthrough",
                Edge("inner-break", "then", "inner-step", "execute"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(fallthrough, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.break-terminal");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Nested_sequence_break_cross_owner_feedback_is_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var feedbackPin = shape == LoopFeedbackPinShape.InferredFromBodyTopology ? "execute" : "feedback";
        var crossOwner = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.SetItem("inner-return",
                Edge("inner-step", "then", "outer-loop", feedbackPin))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(crossOwner, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.cross-owner");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Nested_sequence_break_unowned_cycle_is_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var cycle = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.SetItem("inner-return",
                Edge("inner-step", "then", "inner-body", "execute"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(cycle, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.cycle");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Nested_sequence_break_incomplete_return_is_rejected(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopSequenceBreakCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var incomplete = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.Remove("inner-return")
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(incomplete, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code is "loop.feedback.body-path" or
            "loop.feedback.missing" or "loop.feedback.cardinality");
        if (shape == LoopFeedbackPinShape.InferredFromBodyTopology)
            Assert.Throws<ArgumentException>(() =>
                LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(incomplete));
    }

    [Fact]
    public void Nested_sequence_break_feedback_migration_preserves_authored_identities()
    {
        var source = NestedLoopSequenceBreakCandidate();
        var migrated = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(source);
        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(migrated,
            LoopFeedbackPinShape.ExplicitFeedbackInput);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        Assert.Equal(source.Nodes.Keys, migrated.Nodes.Keys);
        Assert.Equal(source.ExecutionConnections!.Keys, migrated.ExecutionConnections!.Keys);
        Assert.Equal(source.Connections.Keys, migrated.Connections.Keys);
        Assert.Equal(new PortReference("inner-loop", "feedback"), migrated.ExecutionConnections["inner-return"].Target);
        Assert.Equal(new PortReference("outer-loop", "feedback"), migrated.ExecutionConnections["outer-return"].Target);
        Assert.Equal(source.ExecutionConnections["inner-break"], migrated.ExecutionConnections["inner-break"]);
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Unowned_cycle_is_rejected_for_both_pin_shapes(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? NestedLoopCandidate()
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(NestedLoopCandidate());
        var cycle = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.SetItem("inner-return",
                new(new("inner-step", "then"), new("inner-body", "execute"), "execution"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(cycle, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code is "loop.feedback.cycle" or "loop.feedback.missing");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Cross_owner_feedback_is_rejected_for_both_pin_shapes(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? NestedLoopCandidate()
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(NestedLoopCandidate());
        var wrongOwnerPort = shape == LoopFeedbackPinShape.InferredFromBodyTopology ? "execute" : "feedback";
        var crossOwner = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.SetItem("inner-return",
                new(new("inner-step", "then"), new("outer-loop", wrongOwnerPort), "execution"))
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(crossOwner, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.cross-owner");
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Incomplete_feedback_draft_is_diagnostic_and_cannot_be_migrated(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        var incomplete = candidate with
        {
            ExecutionConnections = candidate.ExecutionConnections!.Remove("inner-return")
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(incomplete, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code is "loop.feedback.body-path" or
            "loop.feedback.missing" or "loop.feedback.cardinality");
        Assert.Throws<ArgumentException>(() =>
            LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(incomplete));
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Candidate_shapes_remain_rejected_by_public_and_package_validation(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);

        Assert.Contains(WorkflowValidator.Validate(candidate), issue => issue.Code == "loop.count");
        Assert.Throws<ArgumentException>(() =>
            WorkflowPackageJson.Create(candidate, WorkflowLayout.Create(candidate)));
    }

    [Theory]
    [InlineData(nameof(LoopFeedbackPinShape.InferredFromBodyTopology))]
    [InlineData(nameof(LoopFeedbackPinShape.ExplicitFeedbackInput))]
    public void Disconnected_loop_region_is_rejected_for_both_pin_shapes(string shapeName)
    {
        var shape = Enum.Parse<LoopFeedbackPinShape>(shapeName);
        var inferred = NestedLoopCandidate();
        var candidate = shape == LoopFeedbackPinShape.InferredFromBodyTopology
            ? inferred
            : LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(inferred);
        candidate = AddDisconnectedLoop(candidate, shape);

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(candidate, shape);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.unreachable");
    }

    [Fact]
    public void Inferred_feedback_migration_preserves_node_and_connection_ids()
    {
        var source = NestedLoopCandidate();
        var sourceNodes = source.Nodes;
        var sourceExecution = source.ExecutionConnections;
        var migrated = LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(source);
        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(migrated,
            LoopFeedbackPinShape.ExplicitFeedbackInput);

        Assert.True(proof.IsValid, string.Join(Environment.NewLine, proof.Issues));
        Assert.Equal(source.Nodes.Keys, migrated.Nodes.Keys);
        Assert.Equal(source.ExecutionConnections!.Keys, migrated.ExecutionConnections!.Keys);
        Assert.Equal(source.Connections.Keys, migrated.Connections.Keys);
        Assert.Equal(new PortReference("inner-loop", "feedback"), migrated.ExecutionConnections["inner-return"].Target);
        Assert.Equal(new PortReference("outer-loop", "feedback"), migrated.ExecutionConnections["outer-return"].Target);
        Assert.Equal(new PortReference("inner-loop", "execute"), migrated.ExecutionConnections["outer-inner"].Target);
        Assert.Same(sourceNodes, source.Nodes);
        Assert.Same(sourceExecution, source.ExecutionConnections);
    }

    [Fact]
    public void Ambiguous_feedback_migration_fails_without_mutating_source()
    {
        var source = NestedLoopCandidate() with
        {
            ExecutionConnections = NestedLoopCandidate().ExecutionConnections!
                .Add("second-inner-return", new(new("outer-body", "then"), new("inner-loop", "execute"), "execution"))
        };
        var sourceNodes = source.Nodes;
        var sourceExecution = source.ExecutionConnections;

        Assert.Throws<ArgumentException>(() =>
            LoopFeedbackTopologyProofAnalyzer.ToExplicitFeedbackCandidate(source));
        Assert.Same(sourceNodes, source.Nodes);
        Assert.Same(sourceExecution, source.ExecutionConnections);
    }

    private static WorkflowDefinition NestedLoopCandidate()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["outer-loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "continue"), 3) },
            ["outer-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["inner-loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "continue"), 2) },
            ["inner-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["inner-step"] = new(NodeTypes.SequenceReverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-value", new(new("input", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("start-outer", Edge("input", "then", "outer-loop", "execute"))
            .Add("outer-body-start", Edge("outer-loop", "body", "outer-body", "execute"))
            .Add("outer-inner", Edge("outer-body", "then", "inner-loop", "execute"))
            .Add("inner-body-start", Edge("inner-loop", "body", "inner-body", "execute"))
            .Add("inner-step-start", Edge("inner-body", "then", "inner-step", "execute"))
            .Add("inner-return", Edge("inner-step", "then", "inner-loop", "execute"))
            .Add("outer-return", Edge("inner-loop", "done", "outer-loop", "execute"))
            .Add("outer-done", Edge("outer-loop", "done", "output", "execute"));

        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "nested-loop-feedback-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition NestedLoopSequenceBreakCandidate()
    {
        var definition = NestedLoopCandidate();
        var nodes = definition.Nodes
            .Remove("inner-step")
            .Add("inner-sequence", new NodeDefinition(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([
                    new("continue", "Continue inner loop"),
                    new("break", "Break inner loop")
                ])
            })
            .Add("inner-step", new(NodeTypes.SequenceReverse, "1.0.0"))
            .Add("inner-break", new("vaw.proof.array-break", "proof-only"));
        var execution = definition.ExecutionConnections!
            .Remove("inner-step-start")
            .Remove("inner-return")
            .Add("inner-sequence-start", Edge("inner-body", "then", "inner-sequence", "execute"))
            .Add("inner-continue", Edge("inner-sequence", "then.continue", "inner-step", "execute"))
            .Add("inner-return", Edge("inner-step", "then", "inner-loop", "execute"))
            .Add("inner-break", Edge("inner-sequence", "then.break", "inner-break", "execute"));
        return definition with { Nodes = nodes, ExecutionConnections = execution };
    }

    private static WorkflowDefinition NestedLoopConditionalSequenceBreakCandidate()
    {
        var definition = NestedLoopCandidate();
        var nodes = definition.Nodes
            .Remove("inner-step")
            .Add("inner-sequence", new NodeDefinition(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("route", "Conditional inner-loop route")])
            })
            .Add("inner-step", new(NodeTypes.SequenceReverse, "1.0.0"))
            .Add("break-condition", new NodeDefinition(NodeTypes.Condition, "1.0.0")
            {
                Condition = new("contains", "stop")
            })
            .Add("inner-break", new("vaw.proof.array-break", "proof-only"));
        var data = definition.Connections.Add("condition-input",
            new(new("input", "out"), new("break-condition", "in"), "data"));
        var execution = definition.ExecutionConnections!
            .Remove("inner-step-start")
            .Remove("inner-return")
            .Add("inner-sequence-start", Edge("inner-body", "then", "inner-sequence", "execute"))
            .Add("inner-route", Edge("inner-sequence", "then.route", "break-condition", "execute"))
            .Add("condition-break", Edge("break-condition", "true", "inner-break", "execute"))
            .Add("condition-continue", Edge("break-condition", "false", "inner-step", "execute"))
            .Add("inner-return", Edge("inner-step", "then", "inner-loop", "execute"));

        return definition with { Nodes = nodes, Connections = data, ExecutionConnections = execution };
    }

    private static WorkflowDefinition ThreeNestedLoopsWithSequenceExitsCandidate()
    {
        var definition = NestedLoopSequenceBreakCandidate();
        var nodes = definition.Nodes
            .Add("deep-loop", new NodeDefinition(NodeTypes.Loop, "1.0.0")
            { Loop = new(new("equals", "continue"), 2) })
            .Add("deep-body", new(NodeTypes.LoopBody, "1.0.0"))
            .Add("deep-step", new(NodeTypes.SequenceReverse, "1.0.0"))
            .Add("inner-abort", new("vaw.proof.array-break", "proof-only"));
        var middleSequence = nodes["inner-sequence"] with
        {
            Sequence = new([
                new("continue", "Continue inner loop"),
                new("break", "Break inner loop"),
                new("abort", "Abort inner loop")
            ])
        };
        nodes = nodes.SetItem("inner-sequence", middleSequence);

        var execution = definition.ExecutionConnections!
            .Remove("inner-sequence-start")
            .Add("deep-entry", Edge("inner-body", "then", "deep-loop", "execute"))
            .Add("deep-body-start", Edge("deep-loop", "body", "deep-body", "execute"))
            .Add("deep-step-start", Edge("deep-body", "then", "deep-step", "execute"))
            .Add("deep-return", Edge("deep-step", "then", "deep-loop", "execute"))
            .Add("deep-done", Edge("deep-loop", "done", "inner-sequence", "execute"))
            .Add("inner-abort", Edge("inner-sequence", "then.abort", "inner-abort", "execute"));

        return definition with { Nodes = nodes, ExecutionConnections = execution };
    }

    private static WorkflowDefinition AddDisconnectedLoop(WorkflowDefinition definition, LoopFeedbackPinShape shape)
    {
        var nodes = definition.Nodes
            .Add("detached-loop", new(NodeTypes.Loop, "1.0.0")
            { Loop = new(new("equals", "continue"), 2) })
            .Add("detached-body", new(NodeTypes.LoopBody, "1.0.0"));
        var feedbackPort = shape == LoopFeedbackPinShape.InferredFromBodyTopology ? "execute" : "feedback";
        var execution = definition.ExecutionConnections!
            .Add("detached-body-start", Edge("detached-loop", "body", "detached-body", "execute"))
            .Add("detached-return", Edge("detached-body", "then", "detached-loop", feedbackPort))
            .Add("detached-done", Edge("detached-loop", "done", "output", "execute"));
        return definition with { Nodes = nodes, ExecutionConnections = execution };
    }

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    private static WorkflowDefinition SupportedExplicitLoop()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["step"] = new(NodeTypes.SequenceReverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("step-input", new(new("input", "out"), new("step", "in"), "data"))
            .Add("result", new(new("step", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("start", Edge("input", "then", "loop", "execute"))
            .Add("body-start", Edge("loop", "body", "body", "execute"))
            .Add("body-step", Edge("body", "then", "step", "execute"))
            .Add("return", Edge("step", "then", "loop", "feedback"))
            .Add("done", Edge("loop", "done", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "supported-loop-topology", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static string[] ProjectRegions(LoopFeedbackTopologyProof proof) =>
        proof.Regions.Select(region => string.Join("|", region.LoopNodeId,
            region.ParentLoopNodeId ?? "", region.BodyAnchorNodeId, region.InitialConnectionId,
            region.FeedbackConnectionId, string.Join(",", region.BodyNodeIds),
            string.Join(",", region.BreakNodeIds), string.Join(",", region.BreakRoutes.Select(route =>
                string.Join(":", route.LoopNodeId, route.BreakNodeId, route.SequenceNodeId,
                    route.SequenceOutputPinId))))).ToArray();
}
