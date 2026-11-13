using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Candidate-one architecture evidence for activation identity and state scoping.
/// This is a contract proof fixture, not a production scheduler or public iterator.
/// </summary>
public sealed class ArrayIterationStructuredActivationProofTests
{
    [Fact]
    public void NestedActivations_IsolateNodeValuesAndVariables_AndBreakTargetsNearestLoop()
    {
        var root = ActivationFrame.Root();
        var values = new ActivationValueStore();
        var variables = new ActivationVariableStore();
        var outerFrames = new[]
        {
            root.EnterIteration("outer-loop", 0, ParseItem("\"first\"")),
            root.EnterIteration("outer-loop", 1, ParseItem("\"second\""))
        };

        foreach (var (outer, expected) in outerFrames.Zip(["first", "second"]))
        {
            Assert.Equal(Array.IndexOf(outerFrames, outer), outer.Index);
            Assert.Equal(expected, outer.Item!.Value.GetString());
            values.Set(outer, "uppercase", "out", expected.ToUpperInvariant());
            variables.Set(outer, "item", expected);

            var inner = outer.EnterIteration("inner-loop", 0, ParseItem("0"));
            values.Set(inner, "uppercase", "out", "INNER");
            variables.Set(inner, "item", "inner");

            Assert.Equal("inner-loop", inner.NearestLoop().LoopNodeId);
            Assert.Equal("outer-loop", outer.NearestLoop().LoopNodeId);
            Assert.Equal("INNER", values.Get<string>(inner, "uppercase", "out"));
            Assert.Equal(expected.ToUpperInvariant(), values.Get<string>(outer, "uppercase", "out"));
            Assert.Equal("inner", variables.Get<string>(inner, "item"));
            Assert.Equal(expected, variables.Get<string>(outer, "item"));
        }

        Assert.Equal("FIRST", values.Get<string>(outerFrames[0], "uppercase", "out"));
        Assert.Equal("SECOND", values.Get<string>(outerFrames[1], "uppercase", "out"));
    }

    [Fact]
    public async Task ParallelBranchJoin_IsolatedByForkActivation_AndCancellationStopsNextIteration()
    {
        var root = ActivationFrame.Root();
        var join = new ActivationJoinLedger(["left", "right"]);
        foreach (var index in Enumerable.Range(0, 2))
        {
            var frame = root.EnterIteration("join-items", index, ParseItem(index.ToString()));
            await AssertOneShotJoin(join, frame, index);
        }

        using var cancellation = new CancellationTokenSource();
        var visited = new ConcurrentQueue<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            foreach (var index in Enumerable.Range(0, 3))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var frame = root.EnterIteration("items", index, ParseItem(index.ToString()));
                visited.Enqueue(index);

                if (index == 0) cancellation.Cancel();
                await Task.Yield();
            }
        });

        Assert.Equal([0], visited.ToArray());
    }

    private static async Task AssertOneShotJoin(ActivationJoinLedger join, ActivationFrame frame, int index)
    {
        var forkActivation = Guid.NewGuid();
        var arrivals = await Task.WhenAll(
            Task.Run(() => join.Arrive(frame, forkActivation, "left", $"left-{index}")),
            Task.Run(() => join.Arrive(frame, forkActivation, "right", $"right-{index}")));
        Assert.Equal(1, arrivals.Count(complete => complete));
        Assert.Equal($"left-{index}", join.Result(frame, forkActivation)["left"]);
        Assert.Equal($"right-{index}", join.Result(frame, forkActivation)["right"]);
    }

    private static JsonElement ParseItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record ActivationFrame(Guid Id, ActivationFrame? Parent, string? LoopNodeId,
        int? Index, JsonElement? Item)
    {
        public static ActivationFrame Root() => new(Guid.NewGuid(), null, null, null, null);

        public ActivationFrame EnterIteration(string loopNodeId, int index, JsonElement item)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(loopNodeId);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            return new(Guid.NewGuid(), this, loopNodeId, index, item);
        }

        public ActivationFrame NearestLoop()
        {
            for (ActivationFrame? current = this; current is not null; current = current.Parent)
                if (current.LoopNodeId is not null) return current;
            throw new InvalidOperationException("The activation is not inside an iterator.");
        }
    }

    private sealed class ActivationValueStore
    {
        private readonly ConcurrentDictionary<(Guid ActivationId, string NodeId, string PortId), object> values = new();

        public void Set(ActivationFrame activation, string nodeId, string portId, object value) =>
            values[(activation.Id, nodeId, portId)] = value;

        public T Get<T>(ActivationFrame activation, string nodeId, string portId) =>
            (T)values[(activation.Id, nodeId, portId)];
    }

    private sealed class ActivationVariableStore
    {
        private readonly ConcurrentDictionary<(Guid ActivationId, string VariableId), object> values = new();

        public void Set(ActivationFrame activation, string variableId, object value) =>
            values[(activation.Id, variableId)] = value;

        public T Get<T>(ActivationFrame activation, string variableId) =>
            (T)values[(activation.Id, variableId)];
    }

    private sealed class ActivationJoinLedger(IReadOnlyCollection<string> branchIds)
    {
        private readonly ConcurrentDictionary<(Guid ActivationId, Guid ForkActivationId), JoinState> arrivals = new();

        public bool Arrive(ActivationFrame activation, Guid forkActivationId, string branchId, string result)
        {
            if (!branchIds.Contains(branchId, StringComparer.Ordinal))
                throw new InvalidOperationException("The branch is not declared for this join.");
            var state = arrivals.GetOrAdd((activation.Id, forkActivationId), static _ => new JoinState());
            lock (state)
            {
                if (state.Released) throw new InvalidOperationException("The join already released for this activation.");
                if (!state.Results.TryAdd(branchId, result))
                    throw new InvalidOperationException("A branch arrived more than once for this activation.");
                if (state.Results.Count != branchIds.Count) return false;
                state.Released = true;
                return true;
            }
        }

        public IReadOnlyDictionary<string, string> Result(ActivationFrame activation, Guid forkActivationId)
        {
            var state = arrivals[(activation.Id, forkActivationId)];
            lock (state)
            {
                if (!state.Released) throw new InvalidOperationException("The join is still waiting for branch results.");
                return new Dictionary<string, string>(state.Results, StringComparer.Ordinal);
            }
        }

        private sealed class JoinState
        {
            public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);
            public bool Released { get; set; }
        }
    }
}
