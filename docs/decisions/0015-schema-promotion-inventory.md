# Schema promotion inventory

Status: accepted (2026-10-28)

## Decision

Core now exposes a deterministic inventory of individually reviewed schema
promotion operations. Each entry records an exact source schema, exact target
schema, stable operation ID, and the existing named `GraphEditing` method that
authors may invoke explicitly. The inventory is documentation metadata only;
it does not contain delegates or execute transformations.

The initial inventory covers version-only promotions already implemented by the
editor: the Boolean/function/agent sequence, the integer and numeric slices,
the direct-MCP typed-pin promotion as it actually behaves (`1.23` to `1.37`),
and the Sequence path promotion (`1.38` to `1.43`). Bootstrap from the legacy
blank graph and topology-mutating insertion helpers are deliberately excluded.

## Compatibility boundary

This is not a migration registry. It provides no source lookup, path finding,
composition, latest-version selection, automatic migration, revision creation,
or provenance transfer. Existing methods remain responsible for their exact
topology and validity preconditions. A caller that promotes a definition must
create a new draft/revision through the owning storage boundary while retaining
the source definition and its hash as the source artifact.

The unusual version jumps are intentional and describe observed method
behavior, not numeric compatibility or a promise that intermediate schemas can
be skipped generally. In particular, `EnableDirectMcpGraph` is recorded as
`1.23` to `1.37` because that is the current method contract; the inventory
does not correct or reinterpret the separate `1.36` direct-MCP schema.

## Evidence

Core tests pin the ordered operation IDs and exact source/target pairs, prove
the metadata is absent from definition JSON and hashes, verify a named
promotion leaves its source unchanged, and confirm bootstrap/topology edits are
not represented as version-only promotions. The migration item in the roadmap
remains unchecked: historical package loading, explicit source-preserving
transforms, revision provenance, and broader migration coverage still require
separate contracts and tests.
