# Decision 0048: Establish an explicit workflow migration registry

**Status: Foundation shipped; full compatibility migration remains open.**

## Context

The schema-promotion catalog records editor operations, but it intentionally does
not execute them, compose them, preserve revision provenance, or choose a target
version. Treating that inventory as a migration engine would make numeric schema
ordering look like compatibility and could accidentally admit the proof-only
Sequence-to-WorkflowCall design.

## Decision

Core now exposes a separate, explicit migration registry for three already-public
deterministic Sequence promotions: 1.47 → 1.48, 1.47 → 1.49, and 1.48 → 1.49.
Callers name the operation IDs in order. The registry validates the source hash,
requires contiguous exact source schemas, applies only its private compile-time
operation mapping, validates every target, and returns a new revision candidate
with the source revision as its parent. It never mutates or persists the source,
publishes a revision, executes a workflow, or selects a “latest” target.

The result carries source/target hashes, revision IDs, operation IDs, and
implementation versions as migration provenance. Layout and source provenance
are carried onto the target candidate, while executable content hashing remains
based only on the target definition. Failures return structured diagnostics and
no target candidate.

The registry is deliberately separate from `WorkflowSchemaPromotionCatalog` and
does not add schema 1.50 to the schema catalog, validator, Studio, package
import, or runtime. It cannot migrate the private WorkflowCall proof seam.

## Compatibility boundary

This is a migration foundation, not the complete compatibility gate for schema
1.50. Persisting migration provenance, package-level source-byte preservation,
older-runtime behavior, tamper verification, and the WorkflowCall migration
contract remain required before public schema admission. Downgrades, automatic
load/run migration, topology repair, capability rebinding, and “migrate to
latest” remain unsupported.

## Evidence

Core tests verify explicit inventory, source immutability, parent revision and
hash provenance, layout preservation, contiguous composition, duplicate/unknown
operation rejection, source-schema rejection, and required request fields.
