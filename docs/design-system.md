# Studio editor design system

The Studio is a focused workflow editor for building and inspecting agent graphs. Its visual hierarchy keeps application controls readable and reserves the dark surface for the graph itself.

## Visual direction

The Studio uses a quiet, cool neutral shell: canvas `#eef1f1`, surface `#ffffff`, control `#e6ebeb`, ink `#1d292d`, muted text `#56666a`, and a restrained green-blue action color `#205c55`. Borders separate neighboring work areas. Surfaces are flat, squarely grouped, and lightly rounded; there are no decorative textures, gradient washes, or ambient shadows.

The graph remains a deep green-gray `#172126` with a fine, low-contrast grid. This is the sole large dark region, so nodes and their edge sockets read as the primary work surface. Grid dots span the viewport and track pan and zoom. Initial attachment and **Fit graph** frame and center the current nodes without changing saved coordinates. Semantic colors remain limited to ports, output boundaries, warnings, and recorded run states; every status also has a text label.

```
Studio navigation
Workflow / save / publish controls
Node library | Dark graph canvas | Selected properties
Annotations and reusable workflow controls
```

Typography uses the local system sans-serif stack. Headings and controls use sentence case, left alignment, and a small consistent scale. Controls keep their existing keyboard focus outline and minimum target size. Reduced-motion and forced-colors preferences remain supported.

## Layout and embedding

The graph is bounded by its viewport; the graph world can extend beyond that viewport for pan and zoom. The background grid belongs to the viewport so it continues across the full available width even when the logical graph world is narrower. The grid position follows the world camera. Initial fit uses transient camera offsets and does not rewrite workflow layout coordinates.

The library, graph, and properties panel use one workbench grid. On narrower component containers the panels reflow and graph scrolling stays inside the canvas. The standalone Studio supplies a light host theme; embedded Razor components inherit their host's `--vaw-color-*`, `--vaw-font-family`, and radius tokens. Embedders can customize the graph independently with `--vaw-graph-color-canvas`, `--vaw-graph-color-surface`, `--vaw-graph-color-control`, `--vaw-graph-color-text`, `--vaw-graph-color-muted`, `--vaw-graph-color-border`, `--vaw-graph-color-control-border`, `--vaw-graph-color-focus`, `--vaw-graph-color-accent`, `--vaw-graph-color-success`, `--vaw-graph-color-error`, `--vaw-graph-color-warning`, and `--vaw-graph-color-grid`. These tokens are scoped to the graph viewport; host styles outside the component remain unaffected.

## Components and states

The graph is the visual focus. Node headers are drag handles, sockets sit on node edges, and wires represent definition connections. Labels, comments, and named sections are supporting organization tools. Selection and focus use a clear outline rather than decorative elevation.

Buttons expose default, hover, focus, pressed, and disabled states. Save and error feedback remains visible text. Node types describe workflow definition; only recorded run events drive completed, failed, or delivered states. Playback controls do not imply execution or issue model calls.

The local Playwright checks cover graph centering, full-width grid, keyboard focus, reduced motion, forced colors, contrast samples, narrow embeds, fullscreen, and graph authoring interactions. Screenshots are used for visual critique; they are not a complete accessibility audit.
