# Studio user guide

The Studio creates and runs linear and conditional string-processing workflows. It supports deterministic transforms, host-approved MCP text tools, a deterministic demonstration agent, and opt-in host-configured external model providers. Free placement and wire routing organize the graph; they do not change execution semantics. Branches are authored through explicit conditional nodes.

The default theme uses subtle graphite textures, gradient lighting and layered shadows on editor chrome, with plain node contents and payload surfaces. Embedded hosts can opt out through the [theme properties](design-system.md).

Use disposable text in the local development app. It records inputs, outputs, and comments in local storage without workspace authentication or payload encryption.

## Build workflows for your own service

Studio can act as an IDE for workflows that your own application will run. You can also embed the designer in your application or use the engine without a designer. These are independent integration choices.

Today, you can export reviewed workflow definitions/packages and use the lower-level .NET execution libraries from host code. A standalone service/worker sample and shared local-runtime DI registration are implemented; Studio is not required for execution. Stable NuGet distribution and a production hosting SDK remain planned. See [service integration](service-integration.md) for available entry points and host responsibilities. The controls below describe authoring and testing in the development Studio, not deploying an authenticated production service.

## Start the app

From the repository root, with the SDK pinned by `global.json` installed:

```sh
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet run --project src/VisualAgentWorkflow.Studio --configuration Release --no-build -- --environment Development
```

Open the loopback address printed by the host, normally port 5099. Keep the backend process running while executing workflows. Closing the browser leaves backend runs running; stopping the backend interrupts unfinished runs.

## Configure model providers

Open **Studio settings** in the top navigation. In **Add a provider**, enter a unique provider ID and version, display name, model, endpoint, and timeout. For local LM Studio, use `http://localhost:1234/v1`, check **Allow plain HTTP for a loopback endpoint**, and leave the API key empty unless you enabled authentication. Copy the model identifier shown by LM Studio into **Model**. Select **Save provider**. The key is protected in local Studio storage and is never returned by the settings page. The new version appears immediately under **Agents** in the node library. Add a new version when changing model or endpoint so existing published workflows retain their pinned provider version. The settings page is part of the loopback-only unauthenticated development Studio; do not expose it through a public proxy. See [provider settings and runtime bounds](model-providers.md).

## Create and edit a workflow

1. Select **New workflow**. The draft begins with connected Input and Output nodes.
2. Search the **Node library**, then click an action to insert it after the selected node. The insertion hint names the path; select **Insert on output** for a condition's true or false arm. Click insertion opens space downstream. Drag an action onto the canvas to choose its position, or onto a wire to insert on that path.
3. Right-click the canvas, a node, or a wire for **Graph actions**. You can also focus the canvas and press Space or Shift+F10, search for an action, and use arrow keys to navigate. Drag an output socket onto empty canvas to create a step on that exact path. Escape cancels the menu. Existing-node socket rewiring remains unsupported. Select a node to configure it in the properties panel. Change a processing node's operation using its dropdown. Use **Move up**, **Move down**, or **Remove** to change the connected execution sequence. Use **Undo edit** and **Redo edit** to recover local edits.
4. For an agent, configure its instructions and requested tools. The demonstration provider explicitly does not interpret instructions. External providers appear only when enabled by the host. Their instructions, input and assigned tool results are sent to the configured model endpoint; credentials are never entered in the designer. See [model provider configuration](model-providers.md). Selecting a tool does not grant access; the backend checks policy when it runs.
5. Select **Save draft** to persist definition and layout. Unsaved changes block switching workflows and publication until saved or discarded.

The following diagram shows supported execution, not the spatial arrangement of cards. Uppercase and reverse are example deterministic steps.

```mermaid
flowchart LR
    Input["Input text"] --> Uppercase["Uppercase"] --> Reverse["Reverse"] --> Output["Output text"]
```

## Organize the canvas

Drag a node header to place its card. Arrow keys move it by 8 logical units; Shift increases the step to 40. Drag empty background or use scrollbars to pan. Zoom changes the view, not the saved graph coordinates. **Fit graph** frames the nodes without saving an edit. **Arrange nodes** restores generated positions, retains annotations and sections, and clears reroute points so old routes do not cut across the new arrangement. Undo restores the previous positions and routes.

Select a card to inspect its properties; compact cards show summaries instead of editing forms. Use **Label, comment and position** in that panel for annotations. Open **Labels, comments, sections, and wire routing** below the canvas for grouping and wire controls. Select **Node to organize** to edit a label, comment, or X/Y coordinates. Labels never replace stable node IDs, and comments are text rather than executable agent instructions.

To create a labeled section, enter **New section label**, check its member nodes, then select **Group selected nodes**. Drag the section header to move all members in one edit. Rename the section or use **Ungroup** in the inspector. Membership is explicit; simply placing a card inside a frame does not join it. A node belongs to one section at a time.

Double-click a wire to add a reroute point, or select a wire in the inspector and use **Add reroute point**. Move points with the mouse, arrow keys, or coordinate fields. **Pinned angle** fixes the tangent; curved wires also accept custom degrees. **Right-angle** style constrains bends to orthogonal segments. **Reset wire route** removes the routing points without deleting the connection. These edits share the same undo/redo history as other draft changes.

**Fullscreen graph** expands the canvas, library, properties and toolbar using the browser fullscreen API. Escape or **Exit fullscreen graph** returns to the page and restores focus. Fullscreen requires browser/iframe permission; a rejection leaves the inline editor available with an explanation. View switching, selection, fit, and fullscreen do not alter a saved workflow or stop execution. Within the graph, Ctrl/Cmd+S saves; Ctrl/Cmd+Z undoes and Ctrl/Cmd+Y or Ctrl/Cmd+Shift+Z redoes when outside a text field. Delete removes an eligible selected processing node. Required terminals and branch boundaries cannot be deleted independently.

Use **Find workflow** to filter saved IDs; the selected workflow remains available even when it does not match.

## Save, publish, and restore

Saving changes a draft; publishing creates a numbered immutable version and activates it for future active-version runs. Selecting and viewing a version does not replace the current draft. **Restore selected to draft** copies the historical definition and layout into a new draft generation. Publish afterward to create a new version. **Activate selected version** changes the active pointer without rewriting publication history.

```mermaid
flowchart LR
    Local["Local edits"] -->|"Save draft"| Draft["Saved draft"]
    Draft -->|"Publish and activate"| Revision["Immutable published version"]
    Revision -->|"View selected version"| Preview["Read-only preview"]
    Revision -->|"Restore selected to draft"| Draft
    Revision -->|"Run selected version"| Run["Backend-owned run"]
```

Choose a **Published version** and select **View selected version** to inspect its saved positions, comments, sections, and wire routes. **Return to draft** resumes local authoring. If another editor saved first, the conflict message preserves your local work. Review/export it before choosing **Discard local changes and reload**. There is no automatic merge or simultaneous-edit synchronization.

## Run and inspect

Select **Run inspector** for run controls and history; **Designer** returns to the authoring canvas with local edits preserved. Starting a published run opens the inspector automatically. Use **Filter recorded runs** to find a workflow, status, or run identity.

For a quick test, choose a pipeline in **Quick template run**, enter **Input**, and select **Start independent run**. For a saved workflow, enter **Published run input** and select **Run selected version**. This uses the selected immutable revision, including when the draft has unsaved edits.

Select a run under **Recorded runs**. Click a node's execution card to inspect its recorded input/output. **Previous event**, **Next event**, the recorded-position slider, and **Follow latest** control playback. Thick dashed teal wires correspond to recorded transfer events; moving cards cannot fabricate execution evidence. Historical viewing makes no live model/tool calls. **Cancel selected run** explicitly requests cancellation; closing the page does not.

## Pause and resume a run

Select a nonterminal run under **Recorded runs**, then choose **Pause selected run**. Wait for `paused`: an in-flight node and its tool/model operations finish first. **Resume selected run** continues the same run; when the status is `pausing`, it withdraws the pending pause. **Cancel selected run** also works while paused.

For fast pipelines, check **Start paused** before **Start independent run**, or **Start published run paused** before **Run selected version**. No node executes until resumed. Closing or reloading the page preserves the backend pause state. Other runs can execute while one is paused, within the 32-context admission limit.

Playback shows **State at recorded position** separately from the latest status. Scrubbing over a recorded pause is observation only. Host restart interrupts unfinished runs; it cannot resume their old continuation. See [run-control guarantees and API](run-control.md).

## Reuse a section or published workflow

1. Create a named section containing consecutive processing nodes. Exclude input/output terminals.
2. Open **Reusable pipelines**, choose **Section to save**, enter a unique **Reusable pipeline ID**, and select **Save section as reusable draft**. This creates a separate draft and leaves the current workflow unchanged.
3. Save any current edits, open that new draft in **Saved workflow**, review it, and publish it. Reusable pipelines use the same explicit version lifecycle as other workflows.
4. In a consumer workflow, open **Reusable pipelines** and choose **Reusable workflow**, an exact **Reusable version**, and **Insert pipeline after**.
5. Review the listed requirements, then select **Insert published pipeline**. Use **Undo edit** if needed, then save and publish the consumer explicitly.

Inserted steps have fresh identities and retain their copied annotations and exact tool/provider requirements. They show which source/version they came from. Later source publications do not rewrite existing copies, and edits to a copy do not change the source. This is template expansion, not a nested runtime call or automatic upgrade. Host authorization still applies to each new node.

## Share and recover local work

Open **Import / export definition**. Use **Copy workflow package into JSON editor** or **Download workflow package** to share the full definition/layout and declared requirements. To import, create or select a draft, paste the package into **Workflow JSON**, and choose **Import package into draft**. Checksum/manifest validation detects mismatched contents; it does not authenticate the sender. Review user-authored text and requirements before sharing or executing.

Package import keeps the current workflow ID, stages one undoable local edit, and requires explicit save/publication. It grants no tools, installs no code, and starts no run. Missing registrations are listed; even a matching registration still needs host policy permission. Existing executable JSON and separate layout sidecar downloads/imports remain available. Layouts validate node/connection references against the current definition.

The catalog and run history survive normal host restarts. Hosts can choose file or SQLite run storage; changing that setting selects a separate history and does not migrate runs. See [storage configuration and upgrade notes](run-storage.md). Unsaved browser edits do not. Interrupted Studio runs are recorded as interrupted rather than automatically retried. A separate [consumer checkpoint API](checkpoint-recovery.md) supports explicit recovery of deterministic saved suspensions; Studio has no checkpoint recovery controls. A stale tool contract, unavailable provider, denied grant, or oversized input can fail a run; inspect its evidence and correct the draft/configuration rather than assuming it was rolled back or retried.

Arbitrary socket rewiring, nested prefab instances, collaborative editing, credentialed live-provider qualification, durable approvals, arbitrary-crash/external-call recovery, and production authentication remain future work. Follow the [roadmap](roadmap.md) for current scope and [graph controls](graph-editor.md) for detailed limits.

## Execute from your own service

Export a reviewed workflow package and deploy it with an operator-owned revision/content pin. The [standalone service sample](../samples/WorkflowService/README.md) shows direct and queued invocation without opening Studio, plus node-scoped host grants and local run evidence. Its commands are documented in the sample; no catalog or browser is required. It remains a single-process development runtime without crash recovery.

Service developers can use `AddLocalWorkflowRuntime` to register the same backend runtime in a .NET Generic Host, then resolve invocation/control services. See the [local hosting API](hosting-sdk.md) for lifecycle, dependency registration and current source-consumption limits. Studio controls are unchanged by this helper.

## Observe a consumer service

When a consumer has enabled its authorized observation endpoint and a Studio operator has configured a named source, choose **Observe a consumer service**, select **Consumer source**, then **Connect to consumer**. Choose a run to inspect live state, node payloads and recorded playback. The connection is read-only; **Disconnect observation** and closing the browser do not change consumer execution. The **Recovery assessment** panel explains unsettled work and checkpoint disposition at the latest evidence cursor, independently of recorded playback. It offers no retry/recovery controls, and eligibility still requires fresh validation by an exclusive host. A connection failure explicitly marks the retained last received evidence and suppresses the old assessment. See [assessment meanings](run-reconciliation.md). See [configuration, embedding and limits](observation.md).

## Add a conditional path

On a node's connected output, choose **+ Conditional after out** (or true/false). Set the inserted condition's operator and operand, then configure the two processing arms. Only one arm runs; the exclusive merge passes its value forward. **Arrange nodes** provides a starting layout, and **Undo edit** reverses the insertion as one edit. Save and publish before running both matching and nonmatching inputs. The viewer records **Selected output: true/false** and highlights actual transfers; playback makes no calls. See [conditions, bounds and current editing limits](conditional-graphs.md).

## Require review before an MCP call

On an MCP tool card, choose **Require approval before tool**, edit the inserted approval prompt, then save and publish. A selected path reaching it shows `waiting-approval`. Review **Recorded action approval** and **Exact tool input**, then choose **Approve recorded action** or **Reject recorded action**. Reloading the browser preserves the same request. Pausing and approving are separate: approval cannot resume a paused run, and resume cannot approve a call. Remote read-only observation and historical playback show evidence without decision controls. Host restart interrupts the pending continuation. See [exact-action approval semantics](approvals.md).
