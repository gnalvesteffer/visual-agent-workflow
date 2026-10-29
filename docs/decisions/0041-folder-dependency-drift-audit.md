# Decision 0041: audit folder dependency drift before execution

## Status

Accepted and implemented as a bounded read-only increment of the Git-native
mutable workflow-assets phase.

## Context

Folder projects make JSON source files easy for people and coding agents to
edit. A workflow, however, embeds immutable snapshots of reusable skills,
agent profiles, and graph functions. Refreshing a source file's manifest hash
must not silently change the workflow that a saved run or package means. Before
the editor can support explicit rebinding, it needs to make divergence visible
and prevent an execution from being admitted with an ambiguous dependency.

## Decision

`StudioFolderDependencyAudit` compares each embedded reusable dependency with
the indexed folder source by kind, stable identity, version, and semantic
content hash. It reports four outcomes in the folder editor's Problems pane:

- missing source or missing indexed bytes;
- stale source whose semantic hash differs from the embedded snapshot;
- invalid source content or an embedded dependency without a content hash; and
- duplicate source identities in the manifest.

The editor includes these issues in its Problems count and prevents Run while
any remain. The audit is read-only: it does not rewrite the workflow, resolve a
different version, or mutate the embedded snapshot. Source manifest hash
refresh remains separate from semantic dependency reconciliation.

## Consequences

This gives users and agents a deterministic, actionable signal when Git or an
external editor changes a reusable source. It preserves reproducibility and
avoids an implicit authority switch. Explicit dependency rebinding, dependency
closure capture in run evidence, and richer Git conflict/reconciliation UX
remain later roadmap work.

The contract is covered by focused Studio tests for matching, stale, and
missing skill sources. Profile and graph-function paths share the same audit
contract and remain covered by the source archive's typed serialization tests.
