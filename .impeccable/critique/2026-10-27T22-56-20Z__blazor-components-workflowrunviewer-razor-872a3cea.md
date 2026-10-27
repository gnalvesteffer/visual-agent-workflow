---
target: run node status and event chip
total_score: 25
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
target_identity: "file:/var/home/xorberax/dev/visual-agent-workflow/src/VisualAgentWorkflow.Blazor/Components/WorkflowRunViewer.razor"
target_fingerprint: "sha256:ec7534623ff599a68269ab0f1ebdac67a9167c89f44d57b2886ed6a523a41c95"
target_path: /var/home/xorberax/dev/visual-agent-workflow/src/VisualAgentWorkflow.Blazor/Components/WorkflowRunViewer.razor
timestamp: 2026-10-27T22-56-20Z
slug: blazor-components-workflowrunviewer-razor-872a3cea
closed: true
---
# Design Health Score

| # | Heuristic | Score | Key issue |
|---|---|---:|---|
| 1 | Visibility of system status | 3/4 | State and telemetry are visible, but the terminal state competes with metrics. |
| 2 | Match system / real world | 3/4 | Appropriate for technical operators, though provider jargon is dense. |
| 3 | User control and freedom | 3/4 | Selection, playback, and event inspection are available; recovery is not. |
| 4 | Consistency and standards | 3/4 | Fits the IDE/Blueprint system, though the pill differs from the rectangular node language. |
| 5 | Error prevention | 2/4 | Cancellation has little explanation of cause or scope. |
| 6 | Recognition rather than recall | 2/4 | The event icon is ambiguous and failure/cancellation scan similarly. |
| 7 | Flexibility and efficiency | 3/4 | Event access is close, but no compact telemetry mode or shortcut is apparent. |
| 8 | Aesthetic and minimalist design | 2/4 | The metrics stack is visually noisy for a graph node. |
| 9 | Error recovery | 2/4 | The card supports inspection but does not suggest the next recovery action. |
| 10 | Help and documentation | 2/4 | Technical terms and unavailable usage data lack local explanation. |
| **Total** |  | **25/40** | **Adequate foundation; operational clarity needs refinement.** |

# Design specificity verdict

The card is clearly authored for Visual Agent Workflow Studio: Blueprint-like graph geometry, typed pins, dark IDE surfaces, cyan interaction accents, and semantic execution colors reinforce the product identity. The generic element is the lower AI-observability telemetry block, whose repeated small rows compete with the graph’s primary state. The split status/events control is product-specific, but the arrow glyph reads more like external navigation than recorded events.

# Overall impression

The card is technically transparent and compact, but it answers too many secondary questions before answering the operator’s primary question: “Why did this node stop, and what should I do next?”

# What’s working

- Execution state is visible directly on the node.
- Event inspection is colocated with the state.
- Provider limitations are honestly represented rather than fabricated as zeroes.
- The dark Blueprint-inspired visual language is coherent and product-specific.

# Priority issues

## [P1] Cancellation is visually clear but operationally unexplained

“Cancelled” does not say whether the host, parent scope, user, or provider caused the stop. Add a concise cause summary such as “Cancelled by host” or “Cancelled while awaiting model response,” while preserving the raw reason in the event modal.

## [P1] Telemetry overwhelms execution state

Model turns, unavailable tokens, context window, request status, compactions, and model ID all have similar visual weight. Make the terminal state primary, then collapse secondary metrics under Usage details or group them into Execution and Model usage.

## [P1] The event affordance is ambiguous

The arrow glyph is small and conventionally suggests external navigation. Use a recognizable history/events icon, or show a short Events label when the node has enough room. Keep the accessible label.

## [P2] Failed and cancelled states are too closely coupled

The implementation maps both states to the failed visual class, forcing users to rely on text. Give cancellation a distinct neutral/amber treatment and reserve coral for actual failures.

## [P2] There is no visible recovery path

The card ends at inspection. Provide a contextual recovery action where supported, or explicitly state “Run ended; start a new run to retry.”

# Persona red flags

- Alex, power user: must parse several tiny telemetry rows to identify the relevant state; the split chip is efficient only after it is learned; failed and cancelled nodes scan alike.
- Jordan, first-timer: “Host context compactions” and “provider did not report usage” are unexplained; the arrow does not reveal that it opens event history.
- Riley, incident investigator: event access is convenient, but there is no summary of cause, initiating scope, timestamp, or affected downstream nodes.

# Minor observations

- The pill shape is distinctive but slightly conflicts with the product’s restrained rectangular language.
- A semantic leading marker or icon could improve state scanning.
- The 0.7rem telemetry text may be difficult to read at common zoom levels.
- “Usage not reported by provider” would distinguish missing instrumentation from execution failure better than “Tokens · unavailable.”
- The event target size is reasonable, but the visible glyph is small.

# Questions to consider

- What if the card answered “why did this stop?” before showing model accounting?
- Should cancellation look like failure at all, or communicate controlled interruption?
- Would a visible Events label be clearer than the arrow?
- Could telemetry collapse to one actionable summary, such as “1 of 30 turns · usage unavailable”?
- When a run is cancelled, what is the single next action the operator should take?
