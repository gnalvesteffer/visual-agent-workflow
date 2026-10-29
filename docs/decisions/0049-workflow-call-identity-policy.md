# Decision 0049: Pin WorkflowCall identity to an embedded immutable snapshot

**Status: Prerequisite slice shipped; public schema 1.50 remains deferred.**

## Decision

A `WorkflowCall` refers to one exact child release through the tuple:

```
(workflow ID, revision ID, version, content hash)
```

The embedded child definition is an immutable snapshot of that identity. A caller
must not resolve a call by workflow ID alone, substitute the latest revision, or
rebind the snapshot because a catalog has changed. The revision ID is the stable
lineage identity, the version is the workflow-facing release number, and the
content hash binds the snapshot bytes. The validator already rejects a call whose
workflow ID or content hash does not match its embedded definition, or whose
revision ID is empty.

`WorkflowCallConfiguration.Identity` exposes this tuple as one Core value so
package, admission, runtime, and playback code can compare the same identity
without reconstructing it field by field. JSON continues to serialize the
existing configuration fields; `Identity` is a derived, non-persisted view.

## Package boundary

Packages preserve the complete identity tuple in the serialized WorkflowCall
definition. Their `workflow-revision` requirement remains a dependency summary
of workflow ID, release version, and content hash; it is not an authorization
token and is not a replacement for the embedded revision ID. Package import
must round-trip and revalidate the embedded tuple. A package consumer must
perform its own catalog and host-grant checks before execution.

This policy is intentionally additive and does not promote schema 1.50. It
provides the identity vocabulary needed by the future WorkflowCall migration
contract while keeping proof-only call-bearing Sequence graphs rejected at
publish and run boundaries.

## Acceptance evidence

Core regression coverage verifies that package serialization/deserialization
preserves the child workflow ID, revision ID, release version, content hash, and
embedded snapshot, while dependency requirements remain stable. The existing
validator and nested playback tests cover hash/identity tampering. Migration,
recursive admission, host-grant preflight, and public schema promotion remain
separate gates tracked in Decision 0047. Admission now evaluates provider
grants against nested child workflow IDs and content hashes rather than only
top-level Agent nodes.
