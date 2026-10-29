# Decision 0025: host-admission report and bounded run-start preflight

## Status

Accepted as a bounded Core report with an opt-in Hosting preflight. The report is diagnostic evidence,
and the preflight is availability checking rather than authorization.

## Context

Package requirements already describe the exact provider and MCP binding identities needed by a validated workflow. Hosts still need a stable way to compare those persisted requirements with the capabilities they currently advertise before starting a run. Editor filtering and live broker authorization are separate concerns and must remain separate.

## Decision

Core exposes `WorkflowAdmission.Assess`, which compares a validated `WorkflowPackage` (or a validated definition) with a host-supplied immutable `WorkflowAdmissionHostSnapshot`. Hosting can now receive an `IWorkflowAdmissionSnapshotProvider` and run that comparison before journal creation or queue admission. Studio registers a catalog-backed provider, so its direct, published, parameterized, and paused starts share the same preflight.

The snapshot contains only provider IDs and MCP binding contract/description hashes. The result preserves workflow identity, definition hash, sorted package requirements, and deterministic issues for missing providers/tools or contract/description drift.

The report and preflight do not perform discovery, access credentials, grant permissions, invoke providers/tools, or replace `IToolAuthorizationPolicy` and broker checks. A host must re-evaluate and enforce policy at the actual capability boundary because the snapshot can become stale. A failed preflight produces no run record and exposes a bounded typed report; snapshot-provider failure fails closed without exposing provider error details. Hosts that construct the legacy runtime without a provider retain legacy behavior and must document that they have not enabled preflight.

Embedded package requirements such as node types, skills, profiles, graph functions, and pinned child snapshots are not treated as missing host capabilities in this increment. Provider compatibility is limited to provider-ID availability; model/provider contract versioning and node-scoped grant diagnostics remain follow-up work.

## Compatibility and security boundary

- The report is not serialized and is excluded from definition/package hashes.
- Fabricated package requirements are rejected by recomputing package identity before assessment.
- Live MCP discovery cannot alter the persisted requirement set used by assessment.
- Host facts are caller-owned observations, not authority or proof of authorization.

## Follow-up gates

Before broadening this beyond provider-ID and MCP binding availability/contract checks, add scoped node/call-path findings, provider/model contract identity, explicit policy-result classifications, and an integration proof that broker revalidation still fails closed after the snapshot changes. The provider ID is the complete compatibility identity for this bounded increment; no stronger provider-contract claim is made.
