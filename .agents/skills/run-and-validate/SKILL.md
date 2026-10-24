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

`python3 scripts/pause_resume_smoke.py --browser-executable /usr/bin/chromium` exercises paused starts, API/UI resume/cancel, browser reload, independent work while paused, version pinning and recorded pause playback against a running development Studio. It uses disposable deterministic workflows; it does not validate crash-resumable checkpoints.

After the Release build, `python3 scripts/service_smoke.py` launches the standalone consumer directly, without Studio/HTTP/browser. It checks pure and real MCP direct/queued runs, exact deployment identities, rejection before admission, event ordering and SIGTERM cancellation of its own paused child. Use `--dotnet /path/to/dotnet` for a non-PATH SDK. Each invocation uses an exclusive temporary journal directory.

`python3 scripts/observation_smoke.py` builds on the Release outputs and starts its own isolated consumer/Studio processes with ephemeral server-side credentials. It verifies opt-in/authenticated read-only attachment, graph/payload playback, source replacement and restart/reconnect. Pass `--dotnet` and `--browser-executable` for explicit local tools. The script owns all child cleanup and uses a separate Studio data root; do not point concurrent hosts at the same journal.

`python3 scripts/conditional_graph_smoke.py --browser-executable /usr/bin/chromium` checks conditional insertion/undo/redo, predicate controls, distinct edge sockets, both chosen paths and merge evidence, immutable publication/package/reload and playback against Studio. Uses only deterministic disposable local workflows.

`python3 scripts/approval_smoke.py --browser-executable /usr/bin/chromium` checks the scripted Agent → Condition → Approval → read-only MCP path, exact request binding, reload, independent work, approve/reject/cancel/retry, actor/origin rejection and non-executing playback. Demo agents without tools prefix input with `Demo agent: `; approval must capture the actual value at the gate, not assume the original run input.

Run `python3 scripts/provider_smoke.py --dotnet /path/to/dotnet --browser-executable /path/to/chromium` for host-configured SDK provider changes. It owns isolated Studio/service processes and a loopback protocol fixture, tests browser and direct/queued paths, and makes no live model calls. Report credentialed deployment qualification separately.

CI now runs locked restore, Release build and .NET tests on Windows/Linux; Linux alone runs Python/browser integration. Local Linux validation cannot establish a Windows runner result. `service_smoke.py` checks both file snapshots and SQLite; set `WorkflowService__RunStore=sqlite` for the observation smoke and `WorkflowStudio__RunStore=sqlite` with a disposable data directory for Studio suites. Never run two runtime owners against one store.

`python3 scripts/checkpoint_smoke.py --dotnet /path/to/dotnet` exercises real native deterministic checkpoint rehydration in separate owned processes, live-owner exclusion and forced owner loss at a committed boundary. It uses temporary SQLite storage and no models/MCP calls. Linux CI runs this process script; .NET ownership/recovery tests remain in the Windows/Linux matrix. Keep its recovery claims narrower than arbitrary active-process crash recovery.
