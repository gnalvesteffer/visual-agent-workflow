# Decision 0044: Bounded deterministic transform paths

**Status:** Implemented as schema 1.47

## Context

The Blueprint parity phase already supports a narrow persisted Sequence path: each authored output dispatches through a bounded, disjoint chain of direct MCP nodes, with a separate schema 1.46 slice for one terminal plain-text Agent. The next topology choice was consequential: admitting a pinned child workflow would expose arbitrary nested capability and settlement semantics through a path that currently presents as linear, while turning all deterministic transforms into execution nodes would invent a generic expression contract.

An adversarial proponent, opponent, and adjudicator review selected the smaller deterministic increment. The adjudicated boundary is one fixed string transform, not a general transform family.

## Decision

Schema 1.47 adds `core.sequence.uppercase` (`Sequence uppercase`) with:

- required `in`/`out` string data pins;
- required `execute`/`then` execution pins;
- deterministic `ToUpperInvariant` behavior;
- zero or more direct MCP predecessors;
- direct connection from the transform to the workflow Output;
- ordinary node lifecycle and Sequence lineage evidence.

The Studio exposes the node only in the schema-1.47 explicit picker and provides a guarded promotion from a valid schema-1.43 path graph. Existing schema 1.43 and 1.46 definitions are unchanged. Invalid drafts remain saveable but cannot publish or run.

## Deferred

This decision does not admit Reverse, templates, numeric transforms, child WorkflowCall nodes, Agent-after-transform paths, branches, joins, loops, or arbitrary expressions. Each requires its own typed activation, failure, data-availability, authority, evidence, playback, and migration review.

## Verification

Core validates the topology, package/schema metadata remains versioned, the production runner executes the terminal transform and preserves Sequence output evidence, and the full solution test matrix plus documentation link checks pass.
