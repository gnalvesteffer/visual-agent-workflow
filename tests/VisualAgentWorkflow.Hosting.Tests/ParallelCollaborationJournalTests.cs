using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ParallelCollaborationJournalTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-collaboration-journal-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(directory, "runs.sqlite3");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidVersionedEvidence_RoundTripsThroughBothStores(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var events = CompleteEvidence(run);

        var appended = store.Append(run.Header.RunId, 0, events);
        var reopened = CreateStore(sqlite).Get(run.Header.RunId)!;

        Assert.Equal("completed", appended.Status);
        AssertEventsEquivalent(events, reopened.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRunAfterRelease_IsValidWhenItRecordsReleasedAbort(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var events = CompleteEvidence(run, terminalKind: ExecutionEventKinds.Failed, abort: true);

        var appended = store.Append(run.Header.RunId, 0, events);

        Assert.Equal("failed", appended.Status);
        Assert.Equal(1, appended.Events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedPrefixBeforeRelease_IsValidIncompleteEvidence(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var prefix = CompleteEvidence(run).Take(5).Append(new ExecutionEvent(run.Header.RunId,
            run.Header.RevisionId, 6, DateTimeOffset.UtcNow, ExecutionEventKinds.Interrupted));

        var appended = store.Append(run.Header.RunId, 0, prefix.ToArray());

        Assert.Equal("interrupted", appended.Status);
        Assert.DoesNotContain(appended.Events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
    }

    [Theory]
    [InlineData(false, "send-content")]
    [InlineData(false, "send-grant")]
    [InlineData(false, "arrival-count")]
    [InlineData(false, "snapshot-content")]
    [InlineData(false, "snapshot-hash")]
    [InlineData(false, "read-view")]
    [InlineData(false, "abort-state")]
    [InlineData(false, "event-after-abort")]
    [InlineData(true, "send-content")]
    [InlineData(true, "send-grant")]
    [InlineData(true, "arrival-count")]
    [InlineData(true, "snapshot-content")]
    [InlineData(true, "snapshot-hash")]
    [InlineData(true, "read-view")]
    [InlineData(true, "abort-state")]
    [InlineData(true, "event-after-abort")]
    public void TamperedEvidence_IsRejectedAtomicallyOnAppend(bool sqlite, string mutation)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var tampered = Mutate(CompleteEvidence(run, terminalKind: ExecutionEventKinds.Failed, abort: true), mutation);

        Assert.Throws<InvalidDataException>(() => store.Append(run.Header.RunId, 0, tampered));
        Assert.Empty(CreateStore(sqlite).Get(run.Header.RunId)!.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TamperedEvidence_IsRejectedWhenReadingPersistedHistory(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var events = CompleteEvidence(run);
        store.Append(run.Header.RunId, 0, events);
        const long readSequence = 9;
        var read = events.Single(evt => evt.Sequence == readSequence);
        var tampered = Mutate([read], "read-view")[0];

        if (sqlite)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE run_events SET event_json=$event WHERE run_id=$runId AND sequence=$sequence;";
            command.Parameters.AddWithValue("$event", JsonSerializer.Serialize(tampered, Json));
            command.Parameters.AddWithValue("$runId", run.Header.RunId.ToString());
            command.Parameters.AddWithValue("$sequence", readSequence);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        else
        {
            var snapshotPath = Path.Combine(directory, $"{run.Header.RunId:D}.snapshot.json");
            var snapshot = JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(snapshotPath), Json)!;
            var rewritten = snapshot with { Events = [.. snapshot.Events.Select(evt => evt.Sequence == readSequence ? tampered : evt)] };
            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(rewritten, Json));
        }

        Assert.Throws<InvalidDataException>(() => CreateStore(sqlite).Get(run.Header.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCollaborationPayloadVersion_RemainsStoredAsUnavailableEvidence(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(Revision(), "question");
        var events = CompleteEvidence(run);
        var futureStart = Mutate([events[1]], "future-version")[0];
        var forwardCompatible = events.SetItem(1, futureStart);

        store.Append(run.Header.RunId, 0, forwardCompatible);

        AssertEventsEquivalent(forwardCompatible, CreateStore(sqlite).Get(run.Header.RunId)!.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhasedEvidence_RoundTripsAndRejectsWrongPhaseRelease(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var run = store.Create(PhasedRevision(), "question");
        var events = CompletePhasedEvidence(run);
        store.Append(run.Header.RunId, 0, events);
        AssertEventsEquivalent(events, CreateStore(sqlite).Get(run.Header.RunId)!.Events);

        var badStore = CreateStore(sqlite);
        var badRun = badStore.Create(PhasedRevision(), "question");
        var badEvents = CompletePhasedEvidence(badRun);
        var release = badEvents.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased && evt.Data!.Contains("phase1", StringComparison.Ordinal));
        var payload = JsonNode.Parse(release.Data!)!.AsObject();
        payload["phase"] = "phase2";
        var tampered = badEvents.Replace(release, release with { Data = payload.ToJsonString(Json) });
        Assert.Throws<InvalidDataException>(() => badStore.Append(badRun.Header.RunId, 0, tampered));
        Assert.Empty(CreateStore(sqlite).Get(badRun.Header.RunId)!.Events);

        var duplicateStore = CreateStore(sqlite);
        var duplicateRun = duplicateStore.Create(PhasedRevision(), "question");
        var duplicateEvents = CompletePhasedEvidence(duplicateRun);
        var firstReleaseIndex = Array.FindIndex(duplicateEvents.ToArray(), evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        var duplicateRelease = duplicateEvents.Insert(firstReleaseIndex + 1, duplicateEvents[firstReleaseIndex])
            .Select((evt, index) => evt with { Sequence = index + 1 }).ToImmutableArray();
        Assert.Throws<InvalidDataException>(() => duplicateStore.Append(duplicateRun.Header.RunId, 0, duplicateRelease));
    }

    private IWorkflowRunStore CreateStore(bool sqlite) => sqlite
        ? new SqliteWorkflowRunStore(DatabasePath)
        : new FileRunJournal(directory);

    private static void AssertEventsEquivalent(ImmutableArray<ExecutionEvent> expected, ImmutableArray<ExecutionEvent> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var left = expected[index];
            var right = actual[index];
            Assert.Equal(left with { Data = null }, right with { Data = null });
            if (left.Data is null) Assert.Null(right.Data);
            else
            {
                Assert.NotNull(right.Data);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(left.Data), JsonNode.Parse(right.Data)), $"Event {left.Sequence} data differs.");
            }
        }
    }

    private static WorkflowRevision Revision() => WorkflowRevision.Create(CreateGraph(), "test");
    private static WorkflowRevision PhasedRevision() => WorkflowRevision.Create(CreatePhasedGraph(), "test");

    private static WorkflowDefinition CreateGraph()
    {
        string[] branchIds = ["branch_a", "branch_b"];
        var branches = branchIds.Select((id, index) => new ParallelBranchDescriptor(id, $"Branch {index + 1}", $"anchor-{id}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branchIds.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var branchId in branchIds)
        {
            var anchor = $"anchor-{branchId}";
            var publisher = $"publish-{branchId}";
            var reader = $"read-{branchId}";
            nodes = nodes.Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", branchId) })
                .Add(publisher, AgentNode(branchId))
                .Add(reader, AgentNode(branchId));
            execution = execution
                .Add($"fork-{branchId}", Edge("fork", ParallelBranchLimits.PinId(branchId), anchor, "execute"))
                .Add($"publish-{branchId}", Edge(anchor, "then", publisher, "execute"))
                .Add($"rendezvous-in-{branchId}", Edge(publisher, "out", "rendezvous", ParallelBranchLimits.PinId(branchId)))
                .Add($"rendezvous-out-{branchId}", Edge("rendezvous", ParallelBranchLimits.PinId(branchId), reader, "execute"))
                .Add($"join-{branchId}", Edge(reader, "out", "join", ParallelBranchLimits.PinId(branchId)));
            regionBranches.Add(new(branchId,
            [
                new(publisher, ParallelCollaborationPhases.Publish, ["work"]),
                new(reader, ParallelCollaborationPhases.Read, ["work"])
            ]));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.CollaborativeParallelGraph, "collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous",
                new("fork", "rendezvous", "join", ["work"], 30, regionBranches.ToImmutable()))
        };
    }

    private static WorkflowDefinition CreatePhasedGraph()
    {
        string[] branchIds = ["branch_a", "branch_b"];
        var descriptors = branchIds.Select((id, index) => new ParallelBranchDescriptor(id, $"Branch {index + 1}", $"anchor-{id}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(descriptors) })
            .Add("r1", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("r2", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branchIds.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("input-fork", Edge("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var branchId in branchIds)
        {
            var anchor = $"anchor-{branchId}";
            var p1 = $"p1-{branchId}";
            var r1 = $"read1-{branchId}";
            var p2 = $"p2-{branchId}";
            var r2 = $"read2-{branchId}";
            nodes = nodes.Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", branchId) })
                .Add(p1, AgentNode(branchId)).Add(r1, AgentNode(branchId)).Add(p2, AgentNode(branchId)).Add(r2, AgentNode(branchId));
            execution = execution.Add($"fork-{branchId}", Edge("fork", ParallelBranchLimits.PinId(branchId), anchor, "execute"))
                .Add($"a-p1-{branchId}", Edge(anchor, "then", p1, "execute"))
                .Add($"p1-r1-{branchId}", Edge(p1, "out", "r1", ParallelBranchLimits.PinId(branchId)))
                .Add($"r1-read-{branchId}", Edge("r1", ParallelBranchLimits.PinId(branchId), r1, "execute"))
                .Add($"read-p2-{branchId}", Edge(r1, "out", p2, "execute"))
                .Add($"p2-r2-{branchId}", Edge(p2, "out", "r2", ParallelBranchLimits.PinId(branchId)))
                .Add($"r2-read-{branchId}", Edge("r2", ParallelBranchLimits.PinId(branchId), r2, "execute"))
                .Add($"read-join-{branchId}", Edge(r2, "out", "join", ParallelBranchLimits.PinId(branchId)));
            regionBranches.Add(new(branchId,
            [
                new(p1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(r1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Read },
                new(p2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(r2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Read }
            ]));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph, "phased-collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("r1",
                new("fork", "r1", "join", ["work"], 30, regionBranches.ToImmutable()) { SecondRendezvousNodeId = "r2" })
        };
    }

    private static ImmutableArray<ExecutionEvent> CompleteEvidence(RunSnapshot run,
        string terminalKind = ExecutionEventKinds.Completed, bool abort = false)
    {
        var regionInvocationId = Guid.NewGuid();
        var publishInvocationA = Guid.NewGuid();
        var publishInvocationB = Guid.NewGuid();
        var rendezvousInvocationA = Guid.NewGuid();
        var rendezvousInvocationB = Guid.NewGuid();
        var readInvocationA = Guid.NewGuid();
        var readInvocationB = Guid.NewGuid();
        var messages = new[]
        {
            new CollaborationMessage(1, "branch_a", "work", "finding A"),
            new CollaborationMessage(2, "branch_b", "work", "finding B")
        };
        var snapshotJson = JsonSerializer.Serialize(new SortedDictionary<string, CollaborationMessage[]>(StringComparer.Ordinal)
        {
            ["work"] = messages
        }, Json);
        var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson)));
        var viewJson = JsonSerializer.Serialize(messages, Json);
        var events = new List<ExecutionEvent>();
        void Add(string kind, string? nodeId = null, Guid? invocationId = null, string? connectionId = null, object? data = null)
        {
            events.Add(new(run.Header.RunId, run.Header.RevisionId, events.Count + 1, DateTimeOffset.UtcNow,
                kind, nodeId, invocationId, connectionId, data is null ? null : JsonSerializer.Serialize(data, Json)));
        }

        Add(ExecutionEventKinds.Started);
        Add(ExecutionEventKinds.ParallelCollaborationRegionStarted, "rendezvous", regionInvocationId, "collaboration:region", new
        {
            version = 1,
            revisionId = run.Header.RevisionId,
            regionNodeId = "rendezvous",
            forkNodeId = "fork",
            joinNodeId = "join",
            regionInvocationId,
            timeoutSeconds = 30.0,
            deadline = DateTimeOffset.UtcNow.AddSeconds(30)
        });
        Add(ExecutionEventKinds.ParallelCollaborationMessageSent, "publish-branch_a", publishInvocationA, "work", new
        {
            version = 1,
            regionInvocationId,
            sequence = 1,
            branchId = "branch_a",
            channel = "work",
            content = "finding A"
        });
        Add(ExecutionEventKinds.ParallelCollaborationMessageSent, "publish-branch_b", publishInvocationB, "work", new
        {
            version = 1,
            regionInvocationId,
            sequence = 2,
            branchId = "branch_b",
            channel = "work",
            content = "finding B"
        });
        Add(ExecutionEventKinds.ParallelCollaborationArrival, "rendezvous", rendezvousInvocationA, "collaboration:arrival:branch_a", new
        {
            version = 1,
            regionInvocationId,
            branchId = "branch_a",
            arrivedCount = 1,
            participantCount = 2,
            released = false
        });
        Add(ExecutionEventKinds.ParallelCollaborationArrival, "rendezvous", rendezvousInvocationB, "collaboration:arrival:branch_b", new
        {
            version = 1,
            regionInvocationId,
            branchId = "branch_b",
            arrivedCount = 2,
            participantCount = 2,
            released = true
        });
        Add(ExecutionEventKinds.ParallelCollaborationReleased, "rendezvous", rendezvousInvocationB, "collaboration:snapshot", new
        {
            version = 1,
            regionNodeId = "rendezvous",
            forkNodeId = "fork",
            joinNodeId = "join",
            regionInvocationId,
            sha256 = snapshotHash,
            utf8Bytes = Encoding.UTF8.GetByteCount(snapshotJson),
            canonicalJson = snapshotJson
        });
        Add(ExecutionEventKinds.ParallelCollaborationMessagesRead, "read-branch_a", readInvocationA, "work", new
        {
            version = 1,
            regionInvocationId,
            branchId = "branch_a",
            channel = "work",
            snapshotSha256 = snapshotHash,
            viewJson
        });
        Add(ExecutionEventKinds.ParallelCollaborationMessagesRead, "read-branch_b", readInvocationB, "work", new
        {
            version = 1,
            regionInvocationId,
            branchId = "branch_b",
            channel = "work",
            snapshotSha256 = snapshotHash,
            viewJson
        });
        if (abort)
            Add(ExecutionEventKinds.ParallelCollaborationAborted, "rendezvous", regionInvocationId,
                "collaboration:abort", new
                {
                    version = 1,
                    regionNodeId = "rendezvous",
                    forkNodeId = "fork",
                    joinNodeId = "join",
                    regionInvocationId,
                    reason = "A later workflow step failed.",
                    released = true,
                    snapshotSha256 = snapshotHash
                });
        Add(terminalKind);
        return [.. events];
    }

    private static ImmutableArray<ExecutionEvent> CompletePhasedEvidence(RunSnapshot run)
    {
        var regionInvocation = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        void Add(string kind, string? nodeId = null, Guid? invocationId = null, string? connectionId = null, object? data = null) =>
            events.Add(new(run.Header.RunId, run.Header.RevisionId, events.Count + 1, DateTimeOffset.UtcNow,
                kind, nodeId, invocationId, connectionId, data is null ? null : JsonSerializer.Serialize(data, Json)));
        Add(ExecutionEventKinds.Started);
        Add(ExecutionEventKinds.ParallelCollaborationRegionStarted, "r1", regionInvocation, "collaboration:region", new
        {
            version = 2, revisionId = run.Header.RevisionId, regionNodeId = "r1", secondRendezvousNodeId = "r2",
            forkNodeId = "fork", joinNodeId = "join", regionInvocationId = regionInvocation,
            timeoutSeconds = 30.0, deadline = DateTimeOffset.UtcNow.AddSeconds(30)
        });

        var phaseOne = new[]
        {
            new CollaborationMessage(1, "branch_a", "work", "phase1 A"),
            new CollaborationMessage(2, "branch_b", "work", "phase1 B")
        };
        AddPhasedMessages(ParallelCollaborationPhases.Phase1, phaseOne, "r1");
        var phaseOneHash = AddPhasedRelease(ParallelCollaborationPhases.Phase1, "r1", phaseOne, "r1");
        AddPhasedReads(ParallelCollaborationPhases.Phase1, phaseOne, phaseOneHash, "read1-");

        var phaseTwo = new[]
        {
            new CollaborationMessage(3, "branch_a", "work", "phase2 A"),
            new CollaborationMessage(4, "branch_b", "work", "phase2 B")
        };
        AddPhasedMessages(ParallelCollaborationPhases.Phase2, phaseTwo, "r2");
        var phaseTwoHash = AddPhasedRelease(ParallelCollaborationPhases.Phase2, "r2", phaseTwo, "r2");
        AddPhasedReads(ParallelCollaborationPhases.Phase2, phaseTwo, phaseTwoHash, "read2-");
        Add(ExecutionEventKinds.Completed);
        return [.. events];

        void AddPhasedMessages(string phase, CollaborationMessage[] messages, string barrier)
        {
            foreach (var message in messages)
                Add(ExecutionEventKinds.ParallelCollaborationMessageSent, $"{(phase == ParallelCollaborationPhases.Phase1 ? "p1" : "p2")}-{message.BranchId}",
                    Guid.NewGuid(), "work", new { version = 2, phase, regionInvocationId = regionInvocation, sequence = message.Sequence,
                        branchId = message.BranchId, channel = message.Channel, content = message.Content });
        }

        string AddPhasedRelease(string phase, string nodeId, CollaborationMessage[] messages, string barrier)
        {
            var snapshotJson = JsonSerializer.Serialize(new SortedDictionary<string, CollaborationMessage[]>(StringComparer.Ordinal)
                { ["work"] = messages }, Json);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson)));
            var arrivalInvocationA = Guid.NewGuid();
            var arrivalInvocationB = Guid.NewGuid();
            Add(ExecutionEventKinds.ParallelCollaborationArrival, nodeId, arrivalInvocationA, "collaboration:arrival:branch_a",
                new { version = 2, phase, regionInvocationId = regionInvocation, branchId = "branch_a", arrivedCount = 1, participantCount = 2, released = false });
            Add(ExecutionEventKinds.ParallelCollaborationArrival, nodeId, arrivalInvocationB, "collaboration:arrival:branch_b",
                new { version = 2, phase, regionInvocationId = regionInvocation, branchId = "branch_b", arrivedCount = 2, participantCount = 2, released = true });
            Add(ExecutionEventKinds.ParallelCollaborationReleased, nodeId, arrivalInvocationB, "collaboration:snapshot",
                new { version = 2, phase, regionNodeId = nodeId, forkNodeId = "fork", joinNodeId = "join", regionInvocationId = regionInvocation,
                    sha256 = hash, utf8Bytes = Encoding.UTF8.GetByteCount(snapshotJson), canonicalJson = snapshotJson });
            return hash;
        }

        void AddPhasedReads(string phase, CollaborationMessage[] messages, string hash, string nodePrefix)
        {
            var view = JsonSerializer.Serialize(messages, Json);
            foreach (var branch in new[] { "branch_a", "branch_b" })
                Add(ExecutionEventKinds.ParallelCollaborationMessagesRead, $"{nodePrefix}{branch}", Guid.NewGuid(), "work",
                    new { version = 2, phase, regionInvocationId = regionInvocation, branchId = branch, channel = "work", snapshotSha256 = hash, viewJson = view });
        }
    }

    private static ImmutableArray<ExecutionEvent> Mutate(ImmutableArray<ExecutionEvent> events, string mutation)
    {
        var targetKind = mutation switch
        {
            "send-content" or "send-grant" => ExecutionEventKinds.ParallelCollaborationMessageSent,
            "arrival-count" => ExecutionEventKinds.ParallelCollaborationArrival,
            "snapshot-content" or "snapshot-hash" => ExecutionEventKinds.ParallelCollaborationReleased,
            "read-view" => ExecutionEventKinds.ParallelCollaborationMessagesRead,
            "future-version" => ExecutionEventKinds.ParallelCollaborationRegionStarted,
            "abort-state" => ExecutionEventKinds.ParallelCollaborationAborted,
            "event-after-abort" => ExecutionEventKinds.ParallelCollaborationAborted,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var target = events.First(evt => evt.Kind == targetKind);
        if (mutation == "send-grant") return events.Replace(target, target with { NodeId = "read-branch_a" });
        if (mutation == "event-after-abort")
        {
            var sent = events.First(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent);
            var appended = sent with { Sequence = events.Length, Data = sent.Data!.Replace("\"sequence\":1", "\"sequence\":3", StringComparison.Ordinal) };
            return events.SetItem(events.Length - 1, events[^1] with { Sequence = events.Length + 1 })
                .Insert(events.Length - 1, appended);
        }
        var payload = JsonNode.Parse(target.Data!)!.AsObject();
        switch (mutation)
        {
            case "send-content": payload["content"] = "tampered"; break;
            case "arrival-count": payload["arrivedCount"] = 2; break;
            case "snapshot-content": payload["canonicalJson"] = "{}"; break;
            case "snapshot-hash": payload["sha256"] = new string('0', 64); break;
            case "read-view": payload["viewJson"] = "[]"; break;
            case "future-version": payload["version"] = 2; break;
            case "abort-state": payload["released"] = false; break;
        }
        var changed = target with { Data = payload.ToJsonString(Json) };
        return events.Replace(target, changed);
    }

    private static AgentConfiguration Agent(string branchId) => new("fixture", null, branchId, []);
    private static NodeDefinition AgentNode(string branchId) => new(NodeTypes.Agent, "1.0.0", Agent: Agent(branchId));
    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed record CollaborationMessage(long Sequence, string BranchId, string Channel, string Content);
}
