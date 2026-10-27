# Source and local guidance

Vendored unchanged from [Anthropic webapp-testing](https://github.com/anthropics/skills/tree/dbd4588f9e1033efb41dad4bef2f7947c8993d44/skills/webapp-testing), commit `dbd4588f9e1033efb41dad4bef2f7947c8993d44` (checked against upstream HEAD on 2026-10-24). License: [Apache 2.0](LICENSE.txt). SKILL.md SHA-256: `51b7349e77ec63b7744a6f63647e7566a0b4d2e301121cc10e8c2113af6556a2`.

Read [quality assurance](../quality-assurance/SKILL.md) and [run and validate](../run-and-validate/SKILL.md). Use existing Python Playwright scripts and pinned requirements; no JavaScript test framework is required. An existing Studio process and system Chromium can be reused with `--browser-executable /usr/bin/chromium`.

For Blazor, await observable UI state and Playwright assertions. Network idle alone does not prove a circuit or workflow is ready. Prefer role/name locators, bounded condition waits, and explicit synchronization over arbitrary timeouts. Check browser errors and verify backend evidence as well as rendered state. Screenshots establish appearance, not accessibility conformance or execution correctness.

Use disposable local data and deterministic providers/MCP fixtures. Do not contact live services or install additional tools solely because an upstream example does so. The optional server helper is not necessary when the existing server is running; call its `--help` before use. Keep upstream files unchanged and repository adaptations here.

When switching IDE detail tabs, await the destination tab’s `aria-selected="true"` and scope actions to its visible named `tabpanel`. Runs and Output both contain an Inspect execution button; an unscoped locator can bind the previously visible button before the Blazor render and then wait indefinitely after that panel hides.

When opening execution inspection, scope canvas actions to the named Workflow run viewer region after it becomes visible. The authoring canvas remains mounted while hidden and has identically named Fit graph controls. For source selection after a reload, navigate to `/projects/{projectId}?workflow={workflowId}` rather than selecting the IDE’s intentionally hidden synchronization select.

Keep failure screenshots best-effort: a crashed page cannot capture an image, and that secondary exception must not replace the original browser error. If Chromium reports a page crash in a shared workspace, inspect available memory and cgroup `memory.events` before retrying or attributing it to the application. Record resource-blocked verification separately from passed assertions; never terminate unrelated Studio or browser processes to make a test pass.

Use the repository POSIX launcher `scripts/with_owned_server.py` for new disposable Studio checks. The unchanged upstream helper terminates only its shell and can leave .NET child processes orphaned; see the verified command in [run and validate](../run-and-validate/SKILL.md). The repository helper refuses occupied ports, captures bounded diagnostic logs, and terminates its owned process group on all exit paths.

For responsive navigation, assert that the project picker and each host destination remain visible and exercise their actual links; absence of horizontal overflow alone can pass when CSS hides essential controls. A label’s text node does not count as an element child: `label > :first-child` can accidentally hide its select. Review the rendered narrow screenshot as well as the overflow assertions.
