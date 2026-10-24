using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Single-owner local development store. Atomic snapshot replacement, not a multi-worker production store.</summary>
public sealed class FileRunJournal : IParameterizedWorkflowRunStore, ILocalRuntimeOwnershipStore
{
    private static readonly ConcurrentDictionary<string, object> DirectoryGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string directory;
    private readonly object gate;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FileRunJournal(string directory)
    {
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        gate = DirectoryGates.GetOrAdd(this.directory, static _ => new object());
        Directory.CreateDirectory(this.directory);
    }

    public IDisposable AcquireRuntimeOwnership() => LocalRuntimeOwnership.Acquire(Path.Combine(directory, "runtime.owner"));

    public RunSnapshot Create(WorkflowRevision revision, string input)
        => Create(revision, input, null);

    public RunSnapshot Create(WorkflowRevision revision, string input, ImmutableSortedDictionary<string, string>? parameters)
    {
        var snapshot = RunStoreRecords.Create(revision, input, parameters);
        lock (gate) Publish(snapshot, overwrite: false);
        return snapshot;
    }

    public void Append(ExecutionEvent evt)
    {
        try { Append(evt.RunId, evt.Sequence - 1, [evt]); }
        catch (WorkflowRunStoreConflictException) { throw new InvalidOperationException("Event sequence does not match the run."); }
    }

    public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
    {
        lock (gate)
        {
            var snapshot = Get(runId) ?? throw new KeyNotFoundException("Unknown run.");
            var next = RunStoreRecords.Apply(snapshot, expectedSequence, events);
            Publish(next, overwrite: true);
            return next;
        }
    }

    public RunSnapshot? Get(Guid runId)
    {
        lock (gate)
        {
            RunSnapshot snapshot;
            if (File.Exists(SnapshotPath(runId)))
            {
                // A concurrent read-only observer may retain the previous complete snapshot during replacement,
                // including on Windows where readers must explicitly allow rename/delete sharing.
                using var file = new FileStream(SnapshotPath(runId), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                snapshot = JsonSerializer.Deserialize<RunSnapshot>(file, Json)
                    ?? throw new InvalidDataException("Invalid run snapshot.");
            }
            else
            {
                if (!File.Exists(HeaderPath(runId))) return null;
                // Legacy headers and JSONL evidence remain readable. First successful append publishes
                // one complete snapshot; the old files stay untouched and no longer take precedence.
                var header = JsonSerializer.Deserialize<RunHeader>(File.ReadAllText(HeaderPath(runId)), Json)
                    ?? throw new InvalidDataException("Invalid run header.");
                var events = File.ReadLines(EventsPath(runId)).Select(line =>
                    JsonSerializer.Deserialize<ExecutionEvent>(line, Json) ?? throw new InvalidDataException("Invalid event record."))
                    .ToImmutableArray();
                snapshot = new(header, events);
            }
            RunStoreRecords.Validate(snapshot, runId);
            return snapshot;
        }
    }

    public IReadOnlyList<RunSnapshot> List()
    {
        lock (gate)
        {
            return Directory.EnumerateFiles(directory, "*.snapshot.json").Select(p => Guid.Parse(Path.GetFileName(p)[..^14]))
                .Concat(Directory.EnumerateFiles(directory, "*.run.json").Select(p => Guid.Parse(Path.GetFileName(p)[..^9])))
                .Distinct().Select(id => Get(id)!).OrderByDescending(r => r.Header.CreatedAt).ToArray();
        }
    }

    public void MarkUnfinishedAsInterrupted()
    {
        lock (gate) WorkflowRunStoreExtensions.MarkUnfinishedAsInterrupted(this);
    }

    private string SnapshotPath(Guid runId) => Path.Combine(directory, $"{runId:D}.snapshot.json");
    private string HeaderPath(Guid runId) => Path.Combine(directory, $"{runId:D}.run.json");
    private string EventsPath(Guid runId) => Path.Combine(directory, $"{runId:D}.events.jsonl");

    private void Publish(RunSnapshot snapshot, bool overwrite)
    {
        var temporary = Path.Combine(directory, $"{snapshot.Header.RunId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, snapshot, Json);
                file.Flush(flushToDisk: true);
            }
            // Same-directory rename is the publication point. Readers ignore incomplete temporary files.
            // On Windows, transient scanners or readers can briefly deny replacement despite delete sharing.
            MoveSnapshotIntoPlace(temporary, SnapshotPath(snapshot.Header.RunId), overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void MoveSnapshotIntoPlace(string temporary, string destination, bool overwrite)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite);
                return;
            }
            catch (Exception error) when (overwrite && attempt < 5 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 << attempt));
            }
        }
    }
}
