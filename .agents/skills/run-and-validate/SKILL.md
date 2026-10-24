---
name: run-and-validate
description: Use when adding executable projects, installing dependencies, starting services, running tests, or diagnosing development and CI failures.
---

# Run and validate

Read [project-guide](../project-guide/SKILL.md). Discover actual solution/project files, SDK pins, lockfiles, scripts, and CI before choosing commands. From the root, use `dotnet restore --locked-mode`, `dotnet build --no-restore --configuration Release`, and `dotnet test --no-build --configuration Release`. Core tests require no external services. See [roadmap and current scope](../../../docs/roadmap.md) for the current executable scope.

## Establish the workflow

1. Inspect Git status and preserve existing work. Identify required SDKs, services, model/MCP bindings, and configuration from implemented code and documentation.
2. Check credential names and presence only; never print values or dump environments. Reuse existing authentication. Do not assume missing token variables mean Git access is unavailable.
3. Use pinned tools and locked restore where supported by the repository. Do not disable TLS, signatures, or checksum verification to make installation pass.
4. Run the relevant build and tests from their documented directories. Preserve exit status and verify tests actually executed; distinguish skipped tests from passes.
5. Start required backend/UI services and perform a representative functional operation. An open port, screenshot, or successful compile alone does not establish workflow readiness.

## Runtime evidence

The local studio starts with `dotnet run --project src/VisualAgentWorkflow.Studio --configuration Release --no-build -- --environment Development`. With it running, `python3 scripts/studio_smoke.py` exercises actual API execution, catalog publication/conflicts/restore, and access/validation checks. Add `--browser` for Playwright controls; use `--browser-executable` for an existing Chromium. The smoke check writes workflows, revisions, audit entries, and runs into ignored local storage; use non-sensitive disposable data. Development-only local hosting is not production authentication.

Use deterministic test providers and local controlled MCP fixtures for repeatable checks. Real-provider checks require available authorized credentials and should be separately labeled. Use disposable data and avoid live side effects unless authorized.

For runtime changes, verify UI disconnection does not end a run and reconnect recovers missed events. Apply [workflow-runtime-review](../workflow-runtime-review/SKILL.md) for recovery changes. Stop only processes started for the task when cleanup is needed.

Diagnose the failing prerequisite before retrying. Distinguish environment problems from application defects. Report exact commands, results, and untested prerequisites. Add real startup/build/test commands here once implemented and verified; do not invent placeholder commands that appear executable.

Select meaningful regression checks with [quality assurance](../quality-assurance/SKILL.md); use [webapp testing](../webapp-testing/SKILL.md) and [local notes](../webapp-testing/SOURCE.md) for Playwright checks.
