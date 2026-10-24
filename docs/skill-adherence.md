# Applied skill review

The [Blazor](../.agents/skills/blazor/SOURCE.md) and [CSS](../.agents/skills/css/SOURCE.md) guidance led to concrete fixes rather than additional state frameworks or authentication packages.

- Components reload changing host parameters safely, ignore stale asynchronous responses, dispose observation promptly, and preserve backend-owned execution. Polling starts after interactivity and skips unchanged snapshots. Six HtmlRenderer lifecycle/error-boundary tests cover these behaviors.
- Designer logic lives in code-behind; validation and export values are cached by definition identity. Repeated lists have stable keys. Buttons declare their type so embedding inside a form does not submit it.
- Library styles use scoped selectors, inherited public theme tokens, logical properties, rem units, container-responsive layout, visible focus, reduced-motion and forced-color support. Host page styles are separate.
- Error handling preserves failed actions across successful polling and supplies a sanitized component fallback. Bounded SignalR messages accommodate large editor fields without disconnecting before domain validation.

`python3 scripts/ui_skill_review.py` checks style isolation, nested theming, sampled text contrast, keyboard focus, narrow embedding, 200% text, motion/forced colors, host-form safety, input transport boundaries, JSON import, and persistent errors. It requires a running development Studio, Playwright from `scripts/requirements-browser.txt`, and Chromium (`python3 -m playwright install chromium`, or pass `--browser-executable`). Optional `--screenshots DIRECTORY` saves review images. The existing `studio_smoke.py --browser` exercises the broader authoring and execution path. CI runs both checks.

These checks are representative Chromium tests, not a complete accessibility audit or cross-browser certification. Production authentication, durable distributed execution, and live model providers remain outside the implemented development host's scope.
