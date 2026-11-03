# Inspecting workflow state for debugging

When a workflow fails or behaves unexpectedly, inspect the local storage to understand its configuration and execution history.

## Workflow definitions

Workflow drafts are stored in SQLite databases at `src/VisualAgentWorkflow.Studio/.local/projects/<project-id>/workflows.db`. The main project database is typically at `src/VisualAgentWorkflow.Studio/.local/projects/default/workflows.db`.

Inspect workflow configuration:
```bash
sqlite3 src/VisualAgentWorkflow.Studio/.local/projects/default/workflows.db \
  "SELECT workflow_id, display_name FROM workflows;"
```

Extract and inspect a specific workflow's draft JSON:
```python
import sqlite3, json
conn = sqlite3.connect('src/VisualAgentWorkflow.Studio/.local/projects/default/workflows.db')
cur = conn.cursor()
cur.execute("SELECT draft_json FROM workflows WHERE workflow_id = '<id>'")
draft = json.loads(cur.fetchone()[0])
print(json.dumps(draft, indent=2))
```

Check which tools and providers a workflow requires by examining agent nodes in the draft JSON.

## Run snapshots

Execution runs are stored as JSON snapshot files in `src/VisualAgentWorkflow.Studio/.local/runs/<run-id>.snapshot.json`.

Find recent runs:
```bash
ls -lt src/VisualAgentWorkflow.Studio/.local/runs/*.snapshot.json | head -10
```

Analyze a run's status and events:
```python
import json
with open('src/VisualAgentWorkflow.Studio/.local/runs/<run-id>.snapshot.json') as f:
    snapshot = json.load(f)
print(f"Status: {snapshot['status']}")
print(f"Events: {len(snapshot['events'])}")
# Check for failed events
failed = [e for e in snapshot['events'] if e.get('type') == 'NodeFailed']
```

Runs that fail instantly typically have very few events (often just the start event). Look for admission errors or missing dependency issues.

## Tool bindings and MCP configuration

Tool binding IDs are available via the API endpoint `GET /api/tool-bindings` when Studio is running, or by inspecting local storage files.

MCP server configuration is in `src/VisualAgentWorkflow.Studio/.local/mcp-servers.json`. Verify that servers referenced by workflows are properly configured and reachable.

## Provider configuration

Model provider configurations are in `src/VisualAgentWorkflow.Studio/.local/model-providers.json`. Verify endpoints are reachable before blaming provider issues for workflow failures.