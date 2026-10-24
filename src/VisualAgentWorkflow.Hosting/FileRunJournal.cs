using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Single-process local development journal. Not a multi-worker production store.</summary>
public sealed class FileRunJournal
{
    private readonly string directory;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FileRunJournal(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }

    public RunSnapshot Create(WorkflowRevision revision, string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new RunHeader(Guid.NewGuid(), revision.RevisionId, revision.ParentRevisionId, revision.Author,
            revision.CreatedAt, revision.ContentHash, revision.Definition, input);
        lock (gate)
        {
            // Publish the header only after its initially empty journal is present.
            WriteNew(EventsPath(header.RunId), []);
            WriteNew(HeaderPath(header.RunId), JsonSerializer.SerializeToUtf8Bytes(header, Json));
        }
        return new(header, []);
    }

    public void Append(ExecutionEvent evt)
    {
        lock (gate)
        {
            var snapshot = Get(evt.RunId) ?? throw new KeyNotFoundException("Unknown run.");
            if (snapshot.IsTerminal) throw new InvalidOperationException("Run history is terminal.");
            if (evt.RevisionId != snapshot.Header.RevisionId || evt.Sequence != snapshot.Events.Length + 1)
                throw new InvalidOperationException("Event revision or sequence does not match the run.");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, Json) + "\n");
            using var file = new FileStream(EventsPath(evt.RunId), FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
    }

    public RunSnapshot? Get(Guid runId)
    {
        lock (gate)
        {
            if (!File.Exists(HeaderPath(runId))) return null;
            var header = JsonSerializer.Deserialize<RunHeader>(File.ReadAllText(HeaderPath(runId)), Json)
                ?? throw new InvalidDataException("Invalid run header.");
            if (header.RunId != runId || WorkflowJson.ComputeHash(header.Definition) != header.ContentHash)
                throw new InvalidDataException("Run header identity/content mismatch.");
            var events = ImmutableArray.CreateBuilder<ExecutionEvent>();
            foreach (var line in File.ReadLines(EventsPath(runId)))
            {
                var evt = JsonSerializer.Deserialize<ExecutionEvent>(line, Json)
                    ?? throw new InvalidDataException("Invalid event record.");
                if (evt.RunId != runId || evt.RevisionId != header.RevisionId || evt.Sequence != events.Count + 1)
                    throw new InvalidDataException("Invalid event identity/sequence.");
                events.Add(evt);
            }
            return new(header, events.ToImmutable());
        }
    }

    public IReadOnlyList<RunSnapshot> List()
    {
        lock (gate)
        {
            return Directory.EnumerateFiles(directory, "*.run.json")
                .Select(p => Guid.Parse(Path.GetFileName(p)[..^9]))
                .Select(id => Get(id)!)
                .OrderByDescending(r => r.Header.CreatedAt).ToArray();
        }
    }

    public void MarkUnfinishedAsInterrupted()
    {
        lock (gate)
        {
            foreach (var snapshot in List().Where(r => !r.IsTerminal))
                Append(new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 1,
                    DateTimeOffset.UtcNow, ExecutionEventKinds.Interrupted,
                    Data: "The runtime stopped before recording a terminal outcome. This run was not resumed."));
        }
    }

    private string HeaderPath(Guid runId) => Path.Combine(directory, $"{runId:D}.run.json");
    private string EventsPath(Guid runId) => Path.Combine(directory, $"{runId:D}.events.jsonl");

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
