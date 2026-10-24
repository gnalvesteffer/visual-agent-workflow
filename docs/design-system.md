# Studio editor theme

Audience: people authoring and inspecting agent workflows. The graph is the primary working surface; publication and execution evidence stay close to it. The canvas supports free node placement and rerouted wires; execution supports validated linear and exclusive conditional string graphs.

## Direction and critique

The previous design treated the title, forms, nodes, and version controls as a long document. The revised design uses an editor toolbar, a run shelf, and a graph workbench. Edge sockets and curved or orthogonal wires show definition connections on a scrollable workbench. Narrow embeds keep graph scrolling inside the component and provide reflowing coordinate/annotation controls. No decorative metrics or nonfunctional navigation are added.

Core palette: graphite canvas `#17191f`, panel `#20232b`, control `#2b303b`, light text `#eef0f6`, violet accent `#c4b5fd`, and teal success `#88ddc8`. Amber identifies the output boundary and warnings; failures retain explicit text as well as color. Dark violet primary actions retain light text. Borders distinguish surfaces without wrapping every item in a large rounded card.

Typography uses the locally available humanist stack `"Segoe UI", "Helvetica Neue", Arial, sans-serif`; no third-party font request is required. Sizes follow a restrained rem scale: 0.8125 metadata, 0.875 controls, 1 body, 1.125 section headings, and 1.5 title. Alignment is left. Spacing follows the existing quarter-rem scale. Controls remain at least 2.75rem tall and retain visible keyboard focus.

```
Product / workflow studio                         Local development
Designer / Run inspector workspace switcher
Workflow selection / save / publish / fullscreen
Node library | Input — transform — output | Selected properties
Published versions / packages / selected-version run
```

The distinctive treatment belongs to the graph: typed node headers, port markers, connecting lines, and a quiet dot grid. Panels and typography stay restrained. Node types describe definition roles; completed/failed colors in playback reflect recorded events only. Editor arrows indicate definition order, not recorded execution.

## Embedding and states

Public `--vaw-color-*`, `--vaw-font-family`, and `--vaw-color-scheme` overrides continue to inherit through nested components. New public `--vaw-radius-panel`, `--vaw-radius-control`, and `--vaw-font-mono` tune shape and payload text. Library selectors remain scoped. The standalone page background belongs to Studio.

Buttons have default, hover, focus, pressed, and disabled treatments. Primary actions are explicit, not selected by button position. Save/error statuses remain visible text. Motion is limited to short user-triggered control feedback and disabled under reduced motion. Forced colors removes the dot grid. Graph coordinates are independent of viewport width. The labeled, keyboard-focusable graph viewport scrolls and zooms while the page and inspector reflow. Named section frames organize explicit members, and comments remain readable text.

Validation uses the existing browser authoring/execution smoke check and `graph_canvas_smoke.py`, plus `ui_skill_review.py` for theme inheritance, sampled contrast, focus, reduced motion, form embedding, and reflow. Screenshots support visual critique; they do not substitute for behavior checks or a full accessibility audit.

## Canvas-first workbench

Use compact 224px cards with edge sockets, friendly names and short summaries. Keep configuration in the adjacent selected-node panel, with full stable identity available there. The library occupies the left panel; contextual search appears beside the gesture. Show the insertion source/port before an action, and preserve valid connected graphs through validated insertion. Fullscreen retains all graph controls and supports keyboard exit; narrow containers reflow panels without widening the host page. Run inspection uses the same canvas and a payload panel. Dashed teal delivered paths and textual statuses convey recorded evidence independently of authoring colors.

## Generated material textures

The chrome uses two original generated graphite materials, created with the image-generation tool for this project on 2026-10-24. `graphite-brushed.webp` is fine brushed matte graphite for the workspace and node headers; `graphite-matte.webp` is a fine dimpled graphite surface for the toolbar/library/properties. Both prompts requested seamless square orthographic materials, uniform dark illumination, low contrast, and no text, symbols, logos, UI, borders or perspective. They are decorative material assets, not third-party artwork or execution indicators.

The checked-in [brushed texture](../src/VisualAgentWorkflow.Blazor/wwwroot/textures/graphite-brushed.webp) and [matte texture](../src/VisualAgentWorkflow.Blazor/wwwroot/textures/graphite-matte.webp) are WebP encodings of the generated PNGs, with metadata stripped and quality 82 (about 425 KiB combined). They retain the generated dimensions; no image-processing runtime or remote image/font service is required. Solid host-colored overlays keep texture contribution at 8% on the workspace and 6% on panels/headers. Forms, payloads, node bodies and the dot grid remain plain. Missing assets leave a usable solid-color surface.

Set inherited `--vaw-workspace-texture: none` and `--vaw-panel-texture: none` to disable the images or supply host-local `url(...)` values. Forced-colors mode removes both textures and decorative shadows. Browser review verifies local asset responses, override inheritance, sampled text contrast, forced colors, focus, 200% text and narrow embeds. Generated textures can vary spatially; sampled computed-color checks do not constitute a complete pixel-by-pixel accessibility audit.

## Subtle gradient lighting

Workspace chrome, panels, toolbars, library actions and node headers use a restrained vertical highlight-to-shade gradient (about 3% white to 6% black). Primary buttons receive the same lighting. Gradients layer above the existing material overlays; node bodies, text inputs, payloads and the graph grid stay plain. They convey surface depth, never execution state. Hosts can set inherited `--vaw-chrome-gradient: none` for flat lighting independently of the texture opt-outs. Forced-colors mode removes decorative gradients as well as textures. This is a CSS-only presentation change with no architecture, execution or serialization changes.

## Surface elevation

Soft layered shadows lift nodes above the recessed graph, separate editor panels, and give contextual menus stronger elevation. Toolbars, library actions and primary buttons use a small contact shadow and inset highlight. Controls do not move on hover, and focus outlines remain explicit. Inherited `--vaw-shadow-node`, `--vaw-shadow-panel`, `--vaw-shadow-floating`, `--vaw-shadow-inset` and `--vaw-shadow-control` accept host overrides or `none`. Forced-colors mode suppresses every decorative shadow, including hover states. This presentation-only change affects no runtime or architecture contracts.
