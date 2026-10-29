# Decision 0048: Establish an explicit workflow migration registry

**Status: Registry and catalog provenance slice shipped; package/full compatibility migration remains open.**

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

The target candidate can now be explicitly published through the optional
`IWorkflowMigrationCatalog` boundary. SQLite catalogs persist the provenance as
an additive, canonical JSON envelope plus a separate integrity hash, upgrade
legacy databases to schema 4, and validate the envelope on reopen. Missing
provenance remains the valid legacy state; no normal publish, load, restore, or
run path performs migration implicitly.

The registry is deliberately separate from `WorkflowSchemaPromotionCatalog` and
does not add schema 1.50 to the schema catalog, validator, Studio, package
import, or runtime. It cannot migrate the private WorkflowCall proof seam.

## Compatibility boundary

This is still not the complete compatibility gate for schema 1.50. Package-level
source-byte preservation, older-runtime behavior, package tamper verification,
and the WorkflowCall migration contract remain required before public schema
admission. The catalog provenance hash detects inconsistent stored bytes but is
not a signature or author-authentication mechanism. Downgrades, automatic
load/run migration, topology repair, capability rebinding, and “migrate to
latest” remain unsupported.

## Evidence

Core tests verify explicit inventory, source immutability, parent revision and
hash provenance, layout preservation, contiguous composition, provenance JSON
round-trip, tampered target identity/operation rejection, duplicate/unknown
operation rejection, source-schema rejection, and required request fields.
Hosting tests verify explicit migrated publication, schema-4 upgrade from a
legacy database, reopen preservation, and malformed/tampered provenance
rejection.
