---
name: Visual Agent Workflow Studio
description: A focused IDE for authoring, testing, and inspecting agent workflows.
colors:
  primary: "#0e639c"
  accent: "#4fc1ff"
  canvas: "#181818"
  surface: "#202020"
  control: "#2a2d2e"
  text: "#cccccc"
  muted: "#9da5ac"
  border: "#383838"
  success: "#89d185"
  error: "#f48771"
  warning: "#cca700"
typography:
  title:
    fontFamily: '"Segoe UI", "Helvetica Neue", Arial, sans-serif'
    fontSize: "1.125rem"
    fontWeight: 600
    lineHeight: 1.22
    letterSpacing: "-0.015em"
  body:
    fontFamily: '"Segoe UI", "Helvetica Neue", Arial, sans-serif'
    fontSize: "0.9375rem"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: '"Segoe UI", "Helvetica Neue", Arial, sans-serif'
    fontSize: "0.75rem"
    fontWeight: 600
    lineHeight: 1.4
  code:
    fontFamily: 'ui-monospace, "Cascadia Code", Consolas, monospace'
    fontSize: "0.75rem"
    fontWeight: 400
    lineHeight: 1.45
rounded:
  sm: "4px"
  md: "6px"
  lg: "8px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "24px"
  2xl: "32px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "#ffffff"
    rounded: "{rounded.md}"
    padding: "0.5rem 0.75rem"
    height: "2.75rem"
  button-secondary:
    backgroundColor: "{colors.control}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "0.5rem 0.75rem"
    height: "2.75rem"
  input:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "0.5rem"
    height: "2.75rem"
  panel:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.lg}"
    padding: "1rem"
  explorer-item:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.sm}"
    padding: "0.2rem 0.35rem 0.2rem 1.15rem"
    height: "1.75rem"
---

# Design System: Visual Agent Workflow Studio

## Overview

**Creative North Star: "The Workflow Workbench"**

Visual Agent Workflow Studio is a developer tool for composing agent systems as inspectable programs. Its interface uses the familiar frame of a desktop IDE: project and asset navigation stay at hand, the active document occupies the main workspace, and contextual properties remain beside the graph. The visual workflow editor borrows Blueprint conventions so data and execution paths can be read directly from the graph.

The interface favors compact, steady surfaces and clear state over decorative atmosphere. A restrained blue-cyan accent indicates focus and active navigation; graph node and run states use semantic green, amber, and coral. The user-selected theme is persisted across navigation, with named alternatives available for different developer preferences.

**Key Characteristics:**
- IDE shell with project picker, open-view tabs, activity rail, and project Explorer.
- Graph canvas is the central work surface, with node properties at its edge.
- Document tabs and a compact action header replace workflow identity forms on the canvas.
- Runs, Output, and Problems share one collapsible bottom panel; project source control belongs to the user.
- Cool neutral dark default with concise status colors and an optional theme palette.

## Colors

The default Dark Modern theme uses near-black graphite surfaces and a quiet cyan-blue accent; other selectable themes replace these semantic roles.

### Primary
- **Workbench Blue**: The primary action fill, used for prominent commands such as publishing or running.
- **Signal Cyan**: Focus, active document, Explorer file markers, and graph pin emphasis.

### Neutral
- **Graphite Canvas**: The application and graph work area.
- **Panel Graphite**: Explorer, title bar, and grouped surfaces.
- **Control Charcoal**: Input and secondary control surfaces.
- **Soft Ink**: Main text on dark surfaces.
- **Muted Steel**: Supporting text, labels, and secondary navigation.
- **Divider Gray**: Structural boundaries between editor regions.

### Semantic
- **Completion Green**: Completed work and successful node/run state.
- **Issue Coral**: Errors and failed node/run state.
- **Caution Amber**: Warnings and review-needed state.

**The State-First Color Rule.** Use semantic colors to explain workflow or execution state, not as decoration. Keep cyan available for interaction and focus.

## Typography

**Display Font:** Segoe UI (with Helvetica Neue, Arial, sans-serif fallbacks)
**Body Font:** Segoe UI (with Helvetica Neue, Arial, sans-serif fallbacks)
**Label/Mono Font:** UI monospace (with Cascadia Code and Consolas fallbacks)

**Character:** Familiar system typography keeps the IDE legible and native-feeling. Monospaced text is reserved for identifiers, code, and technical values.

### Hierarchy
- **Title** (600, 1.125rem, 1.22): Section and inspector headings.
- **Body** (400, 0.9375rem, 1.45): Workflow properties and explanatory text.
- **Label** (600, 0.75rem, 1.4): Compact field labels, navigation labels, and tool controls.
- **Code** (400, 0.75rem, 1.45): IDs and technical values that benefit from fixed-width alignment.

**The Scannable Density Rule.** Use compact labels and supporting text to keep editor tools visible, while preserving comfortable line height for body content.

## Layout

The application fills the viewport as a desktop-style workspace. A shallow title bar spans the top. Below it, a narrow activity rail sits beside the Explorer and the active route workspace. The workflow workspace keeps its graph toolbar above a flexible canvas and a contextual node-properties inspector; a lower tray exposes run, versions, and issues without displacing the graph when collapsed.

The default graph canvas starts at 100% zoom and occupies the available space between the side panels. The Explorer is a compact tree with project asset folders. At narrower widths, the Explorer yields its width and is hidden before the graph and inspector become unusable; the shell prevents page-level horizontal overflow. The graph itself remains independently pannable and scrollable.

Spacing uses a compact 4px-based rhythm (4, 8, 12, 16, 24, and 32px). Shell and panel layout favors aligned edges and thin dividers over large page margins.

## Elevation & Depth

The interface is primarily flat. Tonal surface changes and thin borders distinguish the title bar, navigation, Explorer, graph canvas, controls, and inspector. Shadows are reserved for a selected theme's explicit emphasis, such as Neon Glow, rather than being the default depth cue.

**The Structural Surface Rule.** Separate IDE regions with surface tone and borders first; apply glow only when the selected theme calls for it.

## Shapes

Controls and cards use restrained, slightly rounded corners: about 4px for compact tree items, 6px for controls, and 8px for panels. The graph is defined by rectangular nodes, circular ports, and curved wires. Thin outlines keep dense regions distinct without making the interface look ornamental.

## Components

### Buttons
- **Shape:** Small, lightly rounded corners (6px for common controls).
- **Primary:** Workbench blue fill, white text, and a clear label for the main action.
- **Hover / Focus:** Subtle surface shift on hover; theme-colored focus outline remains visible.
- **Secondary / Ghost / Tertiary:** Graphite control fill or transparent navigation, with muted text that strengthens on hover or selection.

### Cards / Containers
- **Corner Style:** Gently rounded panels (8px), compact controls (6px).
- **Background:** Surface graphite over the darker application canvas.
- **Shadow Strategy:** Flat by default; region boundaries use a thin divider.
- **Internal Padding:** Small increments from 8px to 16px, depending on density.

### Inputs / Fields
- **Style:** Canvas-colored fill, visible control border, and gently rounded corners (6px).
- **Focus:** The selected theme's focus color marks keyboard focus and active interaction.
- **Error / Disabled:** Semantic error color for validation; disabled controls retain legible muted text and state.

### Navigation
- **Style:** Persistent title bar and activity rail use the panel surface. Open views appear as document tabs.
- **States:** Active routes use the focus accent and selected surface; inactive links use muted text.
- **Mobile:** Keep the title bar compact and collapse the Explorer to prioritize the editor.

### Workflow Graph
- **Nodes:** Compact rectangular cards with distinct input, agent/tool, and output identity; execution state appears on the node.
- **Connections:** Typed pins and visible wires communicate data and control flow. Wires can be selected, disconnected, and rerouted.
- **Inspector:** Node-specific properties appear in a dedicated panel adjacent to the canvas.
- **Details Tray:** Runs, Output, and Problems share a collapsible lower panel. Test input opens from the document header.
- **Document Settings:** Naming, parameters, and import/export stay outside the default graph view.

### Project Explorer
- **Tree:** Asset folders group workflows, agents, skills, and functions. File-type marks use the accent color.
- **Selection:** A restrained selected surface and leading indicator show the active asset.

## Do's and Don'ts

### Do:
- **Do** keep the graph central and the selected node's properties close to it.
- **Do** use the persistent Explorer and tabs as the primary navigation model.
- **Do** use semantic color for success, failure, and warnings.
- **Do** keep theme tokens semantic so alternate themes preserve the same hierarchy.
- **Do** preserve visible keyboard focus and clear disabled states.

### Don't:
- **Don't** use large marketing-style headings or broad whitespace in the authoring workspace.
- **Don't** add decorative gradients or glow to the default theme.
- **Don't** encode node state with color alone; keep labels and execution details visible.
- **Don't** put the graph's main controls below the fold or behind unrelated library content.
