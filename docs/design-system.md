# Studio editor design system

The Studio is a focused workflow editor for building and inspecting agent graphs. The graph remains the primary workspace, and the shell can match the operator's preferred editor theme.

## Visual direction

The Studio offers **Dark Modern**, **Light Modern**, **VS Code Dark+**, **Solarized Dark**, **Nord**, **Monokai**, **GitHub Dark**, **GitHub Light**, **Dracula**, **Neon Glow**, and **High Contrast** in the global **Theme** selector. Dark Modern is the default: a graphite shell (`#181818`, `#202020`) with quiet neutral text (`#cccccc`) and a focused sky-blue accent (`#4fc1ff`). Light Modern uses a clean near-white shell with ink text and blue controls. GitHub themes use neutral grays with familiar blue controls; Dracula keeps its recognizable purple-gray surfaces and pastel accents. Neon Glow uses deep blue-black surfaces and crisp cyan graph structure, with a faint ambient wash and restrained glow on primary actions, selected nodes, and focus. The glow supports the requested aesthetic without competing with labels or status colors. High Contrast uses black surfaces, white text, cyan structure, and yellow focus/warning cues. The selector applies before the page styles render and remembers the choice in the current browser. The Studio reapplies it after enhanced route navigation and initializes each page's selector from the saved browser preference. Theme palettes are verified against rendered text and control surfaces for WCAG AA contrast. Navigation uses a solid theme surface and a quiet separator. High-contrast and forced-colors modes remove the decorative wash. The graph editor keeps its own readable grid and node hierarchy.

In Dark Modern, the graph canvas uses the same graphite surface as the shell (`#181818`), with node faces using the shell's raised surface (`#202020`) and a fine, low-contrast grid. Each theme carries its colors through to the graph so the canvas feels integrated with the surrounding Studio. The grid spans the viewport and tracks pan and zoom. Initial attachment and **Fit graph** frame and center the current nodes without changing saved coordinates. Semantic colors remain limited to ports, output boundaries, warnings, and recorded run states; every status also has a text label.

```
Studio navigation
Workflow / save / publish controls
Node library | Dark graph canvas | Selected properties
Annotations and reusable workflow controls
```

Typography uses the local system sans-serif stack, with a 15px shell base size, compact line spacing, and slightly tightened heading tracking to improve information density across settings and library pages. Settings pages share a 76rem reading width, one content inset for section headings and controls, and compact vertical gaps; forms keep their field rhythm without nesting a second card inset. Headings and controls use sentence case, left alignment, and a consistent scale. Controls keep their existing keyboard focus outline and minimum target size. Page navigation shows a static accent bar and polite status during slower route changes, then clears it when enhanced navigation finishes. Reduced-motion and forced-colors preferences remain supported.

## Layout and embedding

The graph is bounded by its viewport; the graph world can extend beyond that viewport for pan and zoom. The background grid belongs to the viewport so it continues across the full available width even when the logical graph world is narrower. The grid position follows the world camera. Initial fit uses transient camera offsets and does not rewrite workflow layout coordinates.

The library, graph, and properties panel use one workbench grid. On narrower component containers the panels reflow and graph scrolling stays inside the canvas. The standalone theme selector changes only Studio's root tokens. Embedded Razor components do not read or persist that preference; they inherit the consumer's `--vaw-color-*`, `--vaw-font-family`, and radius tokens. Embedders can customize the graph independently with `--vaw-graph-color-canvas`, `--vaw-graph-color-surface`, `--vaw-graph-color-control`, `--vaw-graph-color-text`, `--vaw-graph-color-muted`, `--vaw-graph-color-border`, `--vaw-graph-color-control-border`, `--vaw-graph-color-focus`, `--vaw-graph-color-accent`, `--vaw-graph-color-success`, `--vaw-graph-color-error`, `--vaw-graph-color-warning`, and `--vaw-graph-color-grid`. These tokens are scoped to the graph viewport; host styles outside the component remain unaffected.

## Components and states

The graph is the visual focus. Node headers are drag handles, sockets sit on node edges, and wires represent definition connections. Labels, comments, and named sections are supporting organization tools. Selection and focus use a clear outline rather than decorative elevation.

Buttons expose default, hover, focus, pressed, and disabled states. Save and error feedback remains visible text. Node types describe workflow definition; only recorded run events drive completed, failed, or delivered states. Playback controls do not imply execution or issue model calls.

The local Playwright checks cover graph centering, full-width grid, keyboard focus, reduced motion, forced colors, contrast samples, narrow embeds, fullscreen, and graph authoring interactions. Screenshots are used for visual critique; they are not a complete accessibility audit.
