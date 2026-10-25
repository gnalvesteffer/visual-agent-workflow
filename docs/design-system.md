# Studio editor design system

The Studio is a focused workflow editor for building and inspecting agent graphs. The graph remains the primary workspace, and the shell can match the operator's preferred editor theme.

## Visual direction

The Studio offers **Dark Modern**, **Light Modern**, and **High Contrast** in the global **Theme** selector. Dark Modern is the default: a graphite shell (`#181818`, `#202020`) with quiet neutral text (`#cccccc`) and a focused sky-blue accent (`#4fc1ff`). Light Modern uses a clean near-white shell with ink text and blue controls. High Contrast uses black surfaces, white text, cyan structure, and yellow focus/warning cues. The selector applies before the page styles render and remembers the choice in the current browser. Theme palettes are verified against rendered text and control surfaces for WCAG AA contrast. Surface borders provide hierarchy without decorative textures, gradient washes, or ambient shadows.

In Dark Modern, the graph uses blue graphite (`#111a20`) with a fine, low-contrast grid. Light Modern switches it to a pale neutral surface; High Contrast switches to black with a clearly visible grid. The grid spans the viewport and tracks pan and zoom. Initial attachment and **Fit graph** frame and center the current nodes without changing saved coordinates. Semantic colors remain limited to ports, output boundaries, warnings, and recorded run states; every status also has a text label.

```
Studio navigation
Workflow / save / publish controls
Node library | Dark graph canvas | Selected properties
Annotations and reusable workflow controls
```

Typography uses the local system sans-serif stack. Headings and controls use sentence case, left alignment, and a small consistent scale. Controls keep their existing keyboard focus outline and minimum target size. Reduced-motion and forced-colors preferences remain supported.

## Layout and embedding

The graph is bounded by its viewport; the graph world can extend beyond that viewport for pan and zoom. The background grid belongs to the viewport so it continues across the full available width even when the logical graph world is narrower. The grid position follows the world camera. Initial fit uses transient camera offsets and does not rewrite workflow layout coordinates.

The library, graph, and properties panel use one workbench grid. On narrower component containers the panels reflow and graph scrolling stays inside the canvas. The standalone theme selector changes only Studio's root tokens. Embedded Razor components do not read or persist that preference; they inherit the consumer's `--vaw-color-*`, `--vaw-font-family`, and radius tokens. Embedders can customize the graph independently with `--vaw-graph-color-canvas`, `--vaw-graph-color-surface`, `--vaw-graph-color-control`, `--vaw-graph-color-text`, `--vaw-graph-color-muted`, `--vaw-graph-color-border`, `--vaw-graph-color-control-border`, `--vaw-graph-color-focus`, `--vaw-graph-color-accent`, `--vaw-graph-color-success`, `--vaw-graph-color-error`, `--vaw-graph-color-warning`, and `--vaw-graph-color-grid`. These tokens are scoped to the graph viewport; host styles outside the component remain unaffected.

## Components and states

The graph is the visual focus. Node headers are drag handles, sockets sit on node edges, and wires represent definition connections. Labels, comments, and named sections are supporting organization tools. Selection and focus use a clear outline rather than decorative elevation.

Buttons expose default, hover, focus, pressed, and disabled states. Save and error feedback remains visible text. Node types describe workflow definition; only recorded run events drive completed, failed, or delivered states. Playback controls do not imply execution or issue model calls.

The local Playwright checks cover graph centering, full-width grid, keyboard focus, reduced motion, forced colors, contrast samples, narrow embeds, fullscreen, and graph authoring interactions. Screenshots are used for visual critique; they are not a complete accessibility audit.
