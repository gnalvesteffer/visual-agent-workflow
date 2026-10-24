# Applied skill review

The [Blazor](../.agents/skills/blazor/SOURCE.md) and [CSS](../.agents/skills/css/SOURCE.md) guidance led to concrete fixes rather than additional state frameworks or authentication packages.

- Components reload changing host parameters safely, ignore stale asynchronous responses, dispose observation promptly, and preserve backend-owned execution. Polling starts after interactivity and skips unchanged snapshots. Six HtmlRenderer lifecycle/error-boundary tests cover these behaviors.
- Designer logic lives in code-behind; validation and export values are cached by definition identity. Repeated lists have stable keys. Buttons declare their type so embedding inside a form does not submit it.
- Library styles use scoped selectors, inherited public theme tokens, logical properties, rem units, container-responsive layout, visible focus, reduced-motion and forced-color support. Host page styles are separate.
- Error handling preserves failed actions across successful polling and supplies a sanitized component fallback. Bounded SignalR messages accommodate large editor fields without disconnecting before domain validation.

`python3 scripts/ui_skill_review.py` checks style isolation, nested theming, sampled text contrast, keyboard focus, narrow embedding, 200% text, motion/forced colors, host-form safety, input transport boundaries, JSON import, and persistent errors. It requires a running development Studio, Playwright from `scripts/requirements-browser.txt`, and Chromium (`python3 -m playwright install chromium`, or pass `--browser-executable`). Optional `--screenshots DIRECTORY` saves review images. The existing `studio_smoke.py --browser` exercises the broader authoring and execution path. CI runs both checks.

These checks are representative Chromium tests, not a complete accessibility audit or cross-browser certification. Production authentication, durable distributed execution, and live model providers remain outside the implemented development host's scope.

## C#, architecture, and API design

Four licensed, pinned upstream skills are installed with provenance and local compatibility notes: modern C# coding standards, API design, .NET project structure, and clean code. Their catalog routes contributors to the relevant review rather than requiring every skill on every change.

Direct package versions are centralized in `Directory.Packages.props` without upgrades. Removing Hosting’s MCP reference lets Hosting resolve `Microsoft.Extensions.AI.Abstractions` 10.10.1 instead of MCP’s 10.11.0; its test project also resolves the adapter’s 10.0.12 extension abstractions instead of 10.0.13. Studio still resolves MCP’s requirements. Regenerated lockfiles capture these narrower dependency graphs. Core remains dependency-free; Blazor and adapters depend inward on Core. Hosting depends on Core and the Agent Framework adapter, with no mandatory MCP transport dependency. Studio explicitly composes the concrete adapters. Architecture tests guard the permitted project-reference graph and prevent runtime/transport/storage assembly references from entering Core or Blazor.

Existing records, public signatures, schema versions, and canonical hash formats are retained. Component async work captures the host context, honors observation cancellation, and ignores stale results. Framework inheritance remains where required; no blanket conversion of identifiers to structs, speculative result framework, blocking async bridge, or unsafe optimization was introduced.

The architecture still includes synchronous SQLite catalog operations and a single-process file journal. Central package management and dependency checks do not make storage distributed or durable across active-process failures; see [implementation scope](implementation.md).
