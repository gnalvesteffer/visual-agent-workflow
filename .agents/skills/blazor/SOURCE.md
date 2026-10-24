# Blazor skill provenance and project guidance

- Upstream repository: https://github.com/Mindrally/skills
- Pinned commit: `7682ca77710e0971eab4e0ae5dddfa281aea0ba5`
- Original skill: https://github.com/Mindrally/skills/blob/7682ca77710e0971eab4e0ae5dddfa281aea0ba5/blazor/SKILL.md
- Original license: https://github.com/Mindrally/skills/blob/7682ca77710e0971eab4e0ae5dddfa281aea0ba5/LICENSE
- License: Apache-2.0; the complete upstream license is retained in [LICENSE](LICENSE).
- Skill SHA-256: `1dc5e67e038e77562ee6173d902579fdaa729c4a8e6a2a1b9155f599a3c10a04`

`SKILL.md` and `LICENSE` are copied without modification. This file is a local addition. No upstream root or Blazor-directory NOTICE file was present at this commit. This is a suitable alternative to the inaccessible MCP Market listing, not a claim that it is the same skill from that listing.

## Apply to this repository

Use this guidance with [project-guide](../project-guide/SKILL.md), [graph-experience-review](../graph-experience-review/SKILL.md), and the documented [embedding contracts](../../../docs/embedding.md). Project architecture and user requirements take precedence over generic upstream recommendations.

- Keep reusable UI in the Razor Class Library and let consuming hosts supply services. A Blazor circuit or component must never own durable workflow execution. Component disposal stops observation/subscriptions, not backend runs.
- Select render modes and state lifetimes deliberately. Prerendering can initialize a component separately from its interactive instance. Keep run controls disabled until interactivity is ready and verify real controls with browser tests. A scoped service in Interactive Server is generally circuit-scoped; it is not automatically durable or appropriate for every user's shared state.
- Use `OnParametersSetAsync` for data that must reload when parameters change. The upstream route example loads in `OnInitializedAsync`, which alone does not handle a new route parameter on a reused component instance. Guard asynchronous loads against stale completion and disposal.
- Dispose timers, subscriptions, cancellation sources, and JS references using `IDisposable` or `IAsyncDisposable` as appropriate. Use `InvokeAsync` for external UI updates; avoid unnecessary manual renders or speculative `ShouldRender` overrides.
- Preserve stable `@key` identities and the workflow selector's identity/generation handling. Optimize handlers and rendering based on measurements; do not mechanically rewrite working loops merely because the general skill discourages inline handlers.
- Verify APIs against the pinned SDK/packages. Use the framework's `ErrorBoundary`/`ErrorBoundaryBase`; do not assume the upstream reference to `IErrorBoundary` is a public interface available in this SDK. Error UI must not expose raw exception messages, provider details, or secrets.
- `AuthorizeView` is presentation, not backend authorization. Hosts must enforce permissions on definition, run, tool, audit, and payload APIs independently. Do not add JWT storage or a new authentication design merely because the generic skill mentions it.
- Prefer the existing HtmlRenderer and Playwright checks where they fit. The upstream bUnit snippet is illustrative and version-dependent; do not add dependencies or describe unrun sample code as verified. Test lifecycle changes, dynamic selection, cancellation, reconnect, and embedding through the affected behavior.
- Treat Server/WebAssembly hosting summaries as tradeoffs rather than rules about internal versus public apps. WebAssembly does not imply offline backend execution or remove the need for authenticated services.

## Updating

Review the skill, license, and referenced resources at an explicit new upstream commit. Copy required files together, update the commit/digest here, recheck the local compatibility notes, and validate YAML frontmatter and links. Do not import unrelated upstream skills or execute sample code automatically.

Dynamic string component parameters require an explicit Razor expression, for example `SelectedNodeId="@selectedNode"`. A bare quoted name is a string literal and can compile while leaving controlled selection stale. The workbench browser check verifies graph selection and the payload/property inspector agree after edits, run selection and view changes.
