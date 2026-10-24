# Studio editor theme

Audience: people authoring and inspecting agent workflows. The graph is the primary working surface; publication and execution evidence stay close to it. This is the implemented linear editor, not a free-form Blueprint canvas.

## Direction and critique

The previous design treated the title, forms, nodes, and version controls as a long document. The revised design uses an editor toolbar, a run shelf, and a graph workbench. A horizontal pipeline makes ordering visible at a glance on wide containers; narrow embeds retain the complete vertical editing path. No decorative metrics or nonfunctional navigation are added.

Core palette: graphite canvas `#17191f`, panel `#20232b`, control `#2b303b`, light text `#eef0f6`, violet accent `#c4b5fd`, and teal success `#88ddc8`. Amber identifies the output boundary and warnings; failures retain explicit text as well as color. Dark violet primary actions retain light text. Borders distinguish surfaces without wrapping every item in a large rounded card.

Typography uses the locally available humanist stack `"Segoe UI", "Helvetica Neue", Arial, sans-serif`; no third-party font request is required. Sizes follow a restrained rem scale: 0.8125 metadata, 0.875 controls, 1 body, 1.125 section headings, and 1.5 title. Alignment is left. Spacing follows the existing quarter-rem scale. Controls remain at least 2.75rem tall and retain visible keyboard focus.

```
Product / workflow studio                         Local development
Run template + history | Workflow selection / save / publish
                       | Input — transform — transform — output
                       | Published versions / selected-version run
                       | Recorded execution / payload inspector
```

The distinctive treatment belongs to the graph: typed node headers, port markers, connecting lines, and a quiet dot grid. Panels and typography stay restrained. Node types describe definition roles; completed/failed colors in playback reflect recorded events only. Editor arrows indicate definition order, not recorded execution.

## Embedding and states

Public `--vaw-color-*`, `--vaw-font-family`, and `--vaw-color-scheme` overrides continue to inherit through nested components. New public `--vaw-radius-panel`, `--vaw-radius-control`, and `--vaw-font-mono` tune shape and payload text. Library selectors remain scoped. The standalone page background belongs to Studio.

Buttons have default, hover, focus, pressed, and disabled treatments. Primary actions are explicit, not selected by button position. Save/error statuses remain visible text. Motion is limited to short user-triggered control feedback and disabled under reduced motion. Forced colors removes the dot grid. Horizontal graph layout is container-dependent; larger graphs can scroll within the labeled, keyboard-focusable graph region while the page itself reflows.

Validation uses the existing browser authoring/execution smoke check and `ui_skill_review.py` for theme inheritance, sampled contrast, focus, reduced motion, form embedding, and reflow. Screenshots support visual critique; they do not substitute for behavior checks or a full accessibility audit.
