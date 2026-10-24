# Source and local guidance

Vendored unchanged from [Anthropic webapp-testing](https://github.com/anthropics/skills/tree/dbd4588f9e1033efb41dad4bef2f7947c8993d44/skills/webapp-testing), commit `dbd4588f9e1033efb41dad4bef2f7947c8993d44` (checked against upstream HEAD on 2026-10-24). License: [Apache 2.0](LICENSE.txt). SKILL.md SHA-256: `51b7349e77ec63b7744a6f63647e7566a0b4d2e301121cc10e8c2113af6556a2`.

Read [quality assurance](../quality-assurance/SKILL.md) and [run and validate](../run-and-validate/SKILL.md). Use existing Python Playwright scripts and pinned requirements; no JavaScript test framework is required. An existing Studio process and system Chromium can be reused with `--browser-executable /usr/bin/chromium`.

For Blazor, await observable UI state and Playwright assertions. Network idle alone does not prove a circuit or workflow is ready. Prefer role/name locators, bounded condition waits, and explicit synchronization over arbitrary timeouts. Check browser errors and verify backend evidence as well as rendered state. Screenshots establish appearance, not accessibility conformance or execution correctness.

Use disposable local data and deterministic providers/MCP fixtures. Do not contact live services or install additional tools solely because an upstream example does so. The optional server helper is not necessary when the existing server is running; call its `--help` before use. Keep upstream files unchanged and repository adaptations here.
