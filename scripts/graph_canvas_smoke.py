"""Exercise actual graph gestures, annotation controls, persistence, and recorded layouts.

Requires a running development Studio and Playwright/Chromium. Creates disposable
local workflows and runs; never uses external model providers or side effects.
"""

from workbench_browser import workbench, inspect_control, show_runs
import argparse
import os
import uuid
import re
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def drag(page, locator, dx, dy):
    locator.scroll_into_view_if_needed()
    box = locator.bounding_box()
    x, y = box["x"] + box["width"] / 2, box["y"] + box["height"] / 2
    page.mouse.move(x, y)
    page.mouse.down()
    page.mouse.move(x + dx, y + dy, steps=8)
    page.mouse.up()


def check(browser, screenshots):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    definition = page.request.get(BASE + "/api/templates/default").json()
    definition["workflowId"] = "canvas-smoke-" + uuid.uuid4().hex
    workflow_id = definition["workflowId"]
    path = BASE + "/api/workflows/" + workflow_id
    response = page.request.put(path + "/draft", data={"definition": definition, "expectedGeneration": 0})
    assert response.ok, response.text()

    def design():
        response = page.request.get(path + "/design")
        assert response.ok, response.text()
        return response.json()

    def select_workflow(workflow_id):
        page.wait_for_function("""id => {
            const select = document.querySelector('[data-vaw-workflow-selector]');
            return select && !select.matches(':disabled') && [...select.options].some(option => option.value === id);
        }""", arg=workflow_id)
        result = page.evaluate("id => window.vawStudioIde.selectWorkflow(id)", workflow_id)
        assert result == 1, f"IDE workflow selector could not select {workflow_id}: {result}"

    def open_editor():
        page.goto(BASE)
        designer = page.get_by_role("region", name="Workflow designer", exact=True)
        expect(workbench(designer).locator("[data-graph-save]")).to_be_enabled()
        select_workflow(workflow_id)
        expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text(workflow_id)
        expect(workbench(designer).locator(".vaw-graph-editor")).to_have_attribute("data-ready", "true")
        return designer

    designer = open_editor()
    canvas = workbench(designer).locator(".vaw-graph-editor")
    library_splitter = canvas.locator("[data-pane-resizer='library']")
    inspector_splitter = canvas.locator("[data-pane-resizer='inspector']")
    bottom_splitter = designer.locator("[data-pane-resizer='bottom']")
    for splitter, key in ((library_splitter, "library"), (inspector_splitter, "inspector"), (bottom_splitter, "bottom")):
        expect(splitter).to_have_attribute("role", "separator")
        before = int(splitter.get_attribute("aria-valuenow"))
        splitter.focus()
        splitter.press("ArrowRight" if key != "bottom" else "ArrowUp")
        expect(splitter).to_have_attribute("aria-valuenow", str(before + 16))
    page.reload()
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    select_workflow(workflow_id)
    expect(workbench(designer).locator("[data-pane-resizer='library']")).to_have_attribute("aria-valuenow", "240")
    page.get_by_text("Studio settings", exact=True).click()
    page.get_by_role("button", name="Reset editor layout", exact=True).click()
    expect(workbench(designer).locator("[data-pane-resizer='library']")).to_have_attribute("aria-valuenow", "224")
    canvas = workbench(designer).locator(".vaw-graph-editor")
    # The canvas list's accessible name may vary between workflows and graph modes;
    # target the stable canvas implementation rather than a copy-specific label.
    nodes = workbench(designer).locator("ol.vaw-free-graph > li")
    step = nodes.filter(has=page.get_by_role("button", name="Move node step-1", exact=True))
    # The whole node card is a selection/drag surface; selection must not require the header.
    step.locator(".vaw-node-body").click(position={"x": 8, "y": 8})
    expect(step.locator("[data-drag-node]")).to_have_attribute("aria-pressed", "true")
    original = design()
    before_x = original["layout"]["nodes"]["step-1"]["position"]["x"]
    before_y = original["layout"]["nodes"]["step-1"]["position"]["y"]
    handle = inspect_control(designer, "Move node step-1").get_by_role("button", name="Move node step-1", exact=True)
    zoom = canvas.locator(".vaw-canvas-world").evaluate("e=>new DOMMatrixReadOnly(getComputedStyle(e).transform).a")
    logical_delta = lambda screen_delta: round((screen_delta / zoom) / 8) * 8
    drag(page, handle, 64, 96)
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    expect(step).to_have_css("left", f"{before_x + logical_delta(64)}px")
    expect(step).to_have_css("top", f"{before_y + logical_delta(96)}px")
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x}px")
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x + logical_delta(64)}px")

    # Arrow keys provide an alternative to dragging and produce the same undoable edit.
    handle.focus()
    handle.press("ArrowRight")
    moved_x = before_x + logical_delta(64) + 8
    expect(step).to_have_css("left", f"{moved_x}px")
    workbench(designer).get_by_text("Labels, comments, sections, and wire routing", exact=True).click()
    inspect_control(designer, "Move node step-1")
    expect(workbench(designer).get_by_role("spinbutton", name="Node X", exact=True)).to_have_value(str(moved_x))
    expect(workbench(designer).get_by_label("Node comment", exact=True)).to_have_attribute("rows", "6")
    expect(workbench(designer).get_by_role("button", name="Expand node comment", exact=True)).to_have_count(0)
    workbench(designer).get_by_label("Node label", exact=True).fill("Normalize input")
    short_comment = "Short note."
    workbench(designer).get_by_label("Node comment", exact=True).fill(short_comment)
    workbench(designer).get_by_label("Node comment", exact=True).press("Tab")
    expect(handle).to_contain_text("Normalize input")
    expect(step.locator(".vaw-node-comment")).to_have_text(short_comment)
    anchor = step.locator(".vaw-tooltip-anchor")
    tooltip = step.locator("[role='tooltip']")
    expect(anchor).to_have_attribute("data-tooltip-truncated", "false")
    expect(anchor).not_to_have_attribute("aria-describedby")
    anchor.hover()
    expect(tooltip).not_to_be_visible()

    long_comment = ("This comment is intentionally long enough to exceed the compact node preview and "
                    "should be available in the full tooltip. " * 3)
    workbench(designer).get_by_label("Node comment", exact=True).fill(long_comment)
    workbench(designer).get_by_label("Node comment", exact=True).press("Tab")
    expect(anchor).to_have_attribute("data-tooltip-truncated", "true")
    expect(anchor).to_have_attribute("aria-describedby")
    expand_comment = step.get_by_role("button", name="Expand node comment", exact=True)
    expect(expand_comment).to_be_visible()
    anchor.hover()
    expect(tooltip).to_be_visible()
    expect(tooltip).to_have_text(long_comment)
    expand_comment.click()
    expect(expand_comment).to_have_attribute("aria-expanded", "true")
    expect(workbench(designer).get_by_label("Node comment", exact=True)).to_have_attribute("rows", "12")

    # Membership is explicit; moving the section moves only its nodes in one edit.
    workbench(designer).get_by_label("New section label", exact=True).fill("Text preparation")
    workbench(designer).get_by_label("Include input in section", exact=True).check()
    workbench(designer).get_by_label("Include step-1 in section", exact=True).check()
    workbench(designer).get_by_role("button", name="Group selected nodes", exact=True).click()
    section_handle = inspect_control(designer, "Move section Text preparation").get_by_role("button", name="Move section Text preparation", exact=True)
    expect(section_handle).to_be_visible()
    output = nodes.filter(has=page.get_by_role("button", name="Move node output", exact=True))
    output_position = output.evaluate("e => [e.style.left,e.style.top]")
    drag(page, section_handle, 40, 32)
    section_move_x = moved_x + logical_delta(40)
    expect(step).to_have_css("left", f"{section_move_x}px")
    assert output.evaluate("e => [e.style.left,e.style.top]") == output_position
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(step).to_have_css("left", f"{moved_x}px")
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(step).to_have_css("left", f"{section_move_x}px")

    # Socket centers sit on node edges, and the wire endpoints match those actual centers.
    measurements = nodes.filter(has=page.get_by_role("button", name="Move node step-1", exact=True)).evaluate("""node => {
        const rect=node.getBoundingClientRect();
        return [...node.querySelectorAll('[data-socket]')].map(pin=>{const p=pin.getBoundingClientRect();return Math.abs(p.left+p.width/2-(pin.dataset.socket==='in'?rect.left:rect.right))});
    }""")
    assert all(offset <= 2 for offset in measurements), measurements

    # Double-click a live SVG segment; this adds a presentation waypoint, not an executable node.
    wire = canvas.locator("path[data-wire='link-1']").first
    canvas.locator(".vaw-canvas-viewport").evaluate("e=>{e.scrollLeft=0;e.scrollTop=0}")
    wire.scroll_into_view_if_needed()
    location = wire.evaluate("""path=>{const p=path.getPointAtLength(path.getTotalLength()/2);const point=new DOMPoint(p.x,p.y).matrixTransform(path.getScreenCTM());return {x:point.x,y:point.y}}""")
    page.mouse.dblclick(location["x"], location["y"])
    reroute = canvas.get_by_role("button", name="Reroute 1 on link-1", exact=True)
    expect(reroute).to_be_visible()
    workbench(designer).get_by_label("Wire to organize", exact=True).select_option("link-1")
    workbench(designer).get_by_label("Custom angle for waypoint 1", exact=True).fill("30")
    workbench(designer).get_by_label("Custom angle for waypoint 1", exact=True).press("Tab")
    expect(workbench(designer).get_by_label("Pinned angle for waypoint 1", exact=True)).to_have_value("30")
    workbench(designer).get_by_label("Pinned angle for waypoint 1", exact=True).select_option("90")
    expect(wire).to_have_attribute("data-points", re.compile('"angle":90'))
    initial_reroute = reroute.evaluate("e=>({x:parseFloat(e.style.left),y:parseFloat(e.style.top)})")
    zoom = canvas.locator(".vaw-canvas-world").evaluate("e=>new DOMMatrixReadOnly(getComputedStyle(e).transform).a")
    drag(page, reroute, 32, 48)
    expect(reroute).to_have_css("left", str(initial_reroute["x"] + round((32 / zoom) / 8) * 8) + "px")
    workbench(designer).get_by_label("Wire style", exact=True).select_option("orthogonal")
    workbench(designer).get_by_label("Pinned angle for waypoint 1", exact=True).select_option("90")
    expect(wire).to_have_attribute("d", re.compile(" L "))
    page.wait_for_function("""() => {
        const paths=[...document.querySelectorAll('.vaw-designer path[data-wire="link-1"]')];
        if(paths.length!==2) return false;
        const points=paths.map(path=>(path.getAttribute('d')?.match(/-?[0-9.]+/g)||[]).map(Number));
        return points.every(coords=>coords.length>=8 && coords.every((value,i)=>i<2 || i%2!==0 || Math.abs(value-coords[i-2])<.001 || Math.abs(coords[i+1]-coords[i-1])<.001)) &&
            Math.abs(points[0].at(-2)-points[0].at(-4))<.001 &&
            Math.abs(points[1][0]-points[1][2])<.001;
    }""")

    # A zoomed drag must convert screen motion back into graph coordinates.
    workbench(designer).get_by_role("button", name="Zoom out graph", exact=True).click()
    old = step.evaluate("e=>parseFloat(e.style.left)")
    zoom = canvas.locator(".vaw-canvas-world").evaluate("e=>new DOMMatrixReadOnly(getComputedStyle(e).transform).a")
    drag(page, handle, 72, 0)
    expect(step).to_have_css("left", f"{old + round((72 / zoom) / 8) * 8}px")

    # Drag-select a group, move it in one layout update, pan into the surrounding canvas, and reset.
    viewport = canvas.locator(".vaw-canvas-viewport")
    minimap = canvas.locator("[data-canvas-minimap]")
    minimap_viewport = minimap.locator("[data-minimap-viewport]")
    assert float(minimap_viewport.get_attribute("width")) > 0 and float(minimap_viewport.get_attribute("height")) > 0, "The minimap should show the current viewport area."
    minimap_box = minimap.bounding_box()
    minimap_scroll_before = viewport.evaluate("e=>[e.scrollLeft,e.scrollTop]")
    page.mouse.move(minimap_box["x"] + minimap_box["width"] * .5, minimap_box["y"] + minimap_box["height"] * .5)
    page.mouse.down()
    page.mouse.move(minimap_box["x"] + minimap_box["width"] * .75, minimap_box["y"] + minimap_box["height"] * .65, steps=4)
    page.mouse.up()
    assert viewport.evaluate("e=>[e.scrollLeft,e.scrollTop]") != minimap_scroll_before, "Dragging the minimap should move the graph camera."
    input_node = canvas.locator("li[data-node-id='input']")
    input_node.scroll_into_view_if_needed()
    step.scroll_into_view_if_needed()
    input_rect, step_rect = input_node.bounding_box(), step.bounding_box()
    left = min(input_rect["x"], step_rect["x"]) - 24
    top = min(input_rect["y"], step_rect["y"]) - 24
    right = max(input_rect["x"] + input_rect["width"], step_rect["x"] + step_rect["width"]) + 24
    bottom = max(input_rect["y"] + input_rect["height"], step_rect["y"] + step_rect["height"]) + 24
    page.mouse.move(left, top)
    page.mouse.down()
    page.mouse.move(right, bottom, steps=8)
    page.mouse.up()
    expect(input_node).to_have_class(re.compile("vaw-node-multi-selected"))
    expect(step).to_have_class(re.compile("vaw-node-multi-selected"))
    position = lambda node: node.evaluate("e=>({x:parseFloat(e.style.left),y:parseFloat(e.style.top)})")
    before_input, before_step = position(input_node), position(step)
    drag(page, handle, 32, 24)
    after_input, after_step = position(input_node), position(step)
    group_dx, group_dy = after_step["x"] - before_step["x"], after_step["y"] - before_step["y"]
    assert group_dx > 0 and group_dy > 0, (group_dx, group_dy)
    assert after_input["x"] - before_input["x"] == group_dx
    assert after_input["y"] - before_input["y"] == group_dy
    viewport_box = viewport.bounding_box()
    scroll_before = viewport.evaluate("e=>[e.scrollLeft,e.scrollTop]")
    page.mouse.move(viewport_box["x"] + 20, viewport_box["y"] + 20)
    page.mouse.down(button="middle")
    page.mouse.move(viewport_box["x"] + 80, viewport_box["y"] + 60, steps=4)
    page.mouse.up(button="middle")
    assert viewport.evaluate("e=>[e.scrollLeft,e.scrollTop]") != scroll_before
    workbench(designer).get_by_role("button", name="Reset view", exact=True).click()
    expect(canvas.locator(".vaw-canvas-toolbar > span").filter(has_text="100%")).to_have_text("100%")
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    saved = design()
    assert saved["draft"]["definition"] == definition, "Presentation editing changed executable JSON"
    assert saved["layout"]["nodes"]["step-1"]["label"] == "Normalize input"
    assert saved["layout"]["nodes"]["step-1"]["commentExpanded"] is True
    assert saved["layout"]["wires"]["link-1"]["points"][0]["angle"] == 90
    assert list(saved["layout"]["sections"].values())[0]["label"] == "Text preparation"

    stale = page.request.put(path + "/design", data={"definition":definition,"layout":original["layout"],"expectedGeneration":original["draft"]["generation"]})
    assert stale.status == 409
    assert design() == saved
    designer = open_editor()
    canvas = workbench(designer).locator(".vaw-graph-editor")
    expect(inspect_control(designer, "Move node step-1").get_by_role("button", name="Move node step-1", exact=True)).to_contain_text("Normalize input")
    expect(inspect_control(designer, "Move node step-1").get_by_label("Node comment", exact=True)).to_have_attribute("rows", "12")
    collapse_comment = canvas.locator("li[data-node-id='step-1']").get_by_role("button", name="Collapse node comment", exact=True)
    collapse_comment.click()
    expect(inspect_control(designer, "Move node step-1").get_by_label("Node comment", exact=True)).to_have_attribute("rows", "6")
    expect(inspect_control(designer, "Move node step-1").get_by_role("button", name="Expand node comment", exact=True)).to_have_attribute("aria-expanded", "false")
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    saved = design()
    assert saved["layout"]["nodes"]["step-1"]["commentExpanded"] is False
    expect(canvas.get_by_role("button", name="Reroute 1 on link-1", exact=True)).to_be_attached()
    expect(inspect_control(designer, "Move section Text preparation").get_by_role("button", name="Move section Text preparation", exact=True)).to_be_attached()
    header = inspect_control(designer, "Move node step-1").get_by_role("button", name="Move node step-1", exact=True)
    header.scroll_into_view_if_needed()
    current_node = workbench(designer).locator("li[data-node-id='step-1']")
    canonical_position = current_node.evaluate("e=>[e.style.left,e.style.top]")
    box = header.bounding_box()
    page.mouse.move(box["x"] + 40, box["y"] + 20)
    page.mouse.down()
    page.mouse.move(box["x"] + 88, box["y"] + 44, steps=4)
    header.dispatch_event("pointercancel", {"pointerId":1})
    page.mouse.up()
    assert current_node.evaluate("e=>[e.style.left,e.style.top]") == canonical_position
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    # IDE runs use the saved source directly and must capture its presentation.
    workbench(designer).get_by_role("button", name="Run workflow", exact=True).click()
    run_dialog = workbench(designer).locator(".vaw-run-dialog")
    run_dialog.get_by_label("Workflow input", exact=True).fill("canvas proof")
    run_dialog.get_by_role("button", name="Run workflow", exact=True).click()
    history = workbench(designer).get_by_label("Workflow run history", exact=True)
    expect(history.locator("tbody tr").first).to_contain_text("completed")
    inspect = history.get_by_role("button", name=re.compile("^Inspect run ")).first
    run_id = inspect.get_attribute("aria-label").removeprefix("Inspect run ")
    recorded = page.request.get(BASE + "/api/runs/" + run_id).json()
    assert recorded["header"]["layout"] == saved["layout"], "Execution lost the saved graph presentation"
    inspect.click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    expect(viewer.locator(".vaw-graph-editor[data-read-only='true']")).to_be_visible()
    assert viewer.locator(".vaw-graph-editor").evaluate("""root => {
        const nodes = [...root.querySelectorAll('li[data-node-id]')].sort((a, b) =>
            parseFloat(a.style.left) - parseFloat(b.style.left) || parseFloat(a.style.top) - parseFloat(b.style.top));
        for (let i = 1; i < nodes.length; i++) {
            const previous = nodes[i - 1], current = nodes[i];
            if (Math.abs(parseFloat(previous.style.left) - parseFloat(current.style.left)) < 1 &&
                previous.offsetTop + previous.offsetHeight + 1 > current.offsetTop)
                return false;
        }
        return true;
    }"""), "Recorded graph nodes overlap vertically"
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("FOORP SAVNAC")
    expect(viewer.get_by_role("button", name="Inspect node step-1", exact=True)).to_contain_text("Normalize input")
    expect(viewer.locator(".vaw-wire-delivered").first).to_be_attached()
    old_layout = design()["layout"]
    drag(page, viewer.get_by_role("button", name="Inspect node step-1", exact=True), 48, 0)
    assert design()["layout"] == old_layout, "Playback changed the saved layout"
    # Later edits cannot rewrite the presentation captured by an existing run.
    inspect_control(designer, "Move node step-1")
    workbench(designer).get_by_label("Node label", exact=True).fill("Changed draft label")
    workbench(designer).get_by_label("Node label", exact=True).press("Tab")
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    assert page.request.get(BASE + "/api/runs/" + run_id).json()["header"]["layout"] == saved["layout"]
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    assert page.request.get(path + "/revisions").json() == [], "An IDE run should not publish a source revision"
    # Existing wires can be disconnected, saved as an invalid draft, and rewired to a matching pin.
    canvas = workbench(designer).locator(".vaw-graph-editor")
    workbench(designer).locator("details.vaw-layout-inspector").evaluate("e=>e.open=true")
    workbench(designer).get_by_label("Wire to organize", exact=True).select_option("link-1")
    workbench(designer).get_by_role("button", name="Disconnect wire", exact=True).click()
    expect(workbench(designer).get_by_role("alert", name="Workflow validation issues")).to_contain_text("unresolved issue")
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    expect(workbench(designer).get_by_role("button", name="Run workflow", exact=True)).to_be_disabled()
    assert "link-1" not in design()["draft"]["definition"]["connections"]
    source_pin = canvas.locator("li[data-node-id='input'] .vaw-output-ports [data-socket='out']")
    target_pin = canvas.locator("li[data-node-id='step-1'] .vaw-input-ports [data-socket='in']")
    workbench(designer).get_by_role("button", name="Fit graph", exact=True).click()
    expect(source_pin).to_be_in_viewport(ratio=0.99)
    expect(target_pin).to_be_in_viewport(ratio=0.99)
    source_box, target_box = source_pin.bounding_box(), target_pin.bounding_box()
    page.mouse.move(source_box["x"] + source_box["width"] / 2, source_box["y"] + source_box["height"] / 2)
    page.mouse.down()
    page.mouse.move(target_box["x"] + target_box["width"] / 2, target_box["y"] + target_box["height"] / 2, steps=12)
    expect(target_pin).to_have_class(re.compile("vaw-socket-drop-target"))
    page.mouse.up()
    expect(workbench(designer).get_by_role("alert", name="Workflow validation issues")).to_have_count(0)
    workbench(designer).locator("[data-graph-save]").click()
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    expect(workbench(designer).get_by_role("button", name="Run workflow", exact=True)).to_be_enabled()
    saved_rewire = design()["draft"]["definition"]["connections"]
    assert any(edge["source"] == {"nodeId": "input", "portId": "out"} and edge["target"] == {"nodeId": "step-1", "portId": "in"} for edge in saved_rewire.values())
    # Agent-only controls stay scoped to an agent node and are persisted independently.
    agent_definition = page.request.get(BASE + "/api/templates/default").json()
    agent_definition["workflowId"] = "agent-settings-smoke-" + uuid.uuid4().hex
    # Old saved drafts persist reasoning efforts as numeric enums; 0 must remain Low.
    agent_definition["schemaVersion"] = "1.9"
    agent_definition["nodes"]["step-1"]["type"] = "core.agent"
    agent_definition["nodes"]["step-1"]["agent"] = {
        "providerId": "demo.scripted",
        "instructions": "Return a deterministic response.",
        "tools": [],
        "reasoningEffort": 0,
    }
    agent_path = BASE + "/api/workflows/" + agent_definition["workflowId"]
    response = page.request.put(agent_path + "/draft", data={"definition": agent_definition, "expectedGeneration": 0})
    assert response.ok, response.text()
    page.goto(BASE)
    agent_designer = page.get_by_role("region", name="Workflow designer", exact=True)
    select_workflow(agent_definition["workflowId"])
    inspect_control(agent_designer, "Move node step-1")
    token_limit = workbench(agent_designer).get_by_label("Maximum output tokens for step-1", exact=True)
    token_limit.fill("4096")
    token_limit.press("Tab")
    workbench(agent_designer).locator("[data-graph-save]").click()
    expect(workbench(agent_designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    configured_agent = page.request.get(agent_path + "/design").json()["draft"]["definition"]
    assert configured_agent["schemaVersion"] == "1.10"
    assert configured_agent["nodes"]["step-1"]["agent"]["maxOutputTokens"] == 4096
    assert configured_agent["nodes"]["step-1"]["agent"]["reasoningEffort"] == 0
    # The authoring control creates the typed input/selector pair and saves its RFC 6901 pointer.
    json_definition = page.request.get(BASE + "/api/templates/default").json()
    json_definition["workflowId"] = "json-canvas-smoke-" + uuid.uuid4().hex
    json_path = BASE + "/api/workflows/" + json_definition["workflowId"]
    response = page.request.put(json_path + "/draft", data={"definition": json_definition, "expectedGeneration": 0})
    assert response.ok, response.text()
    page.goto(BASE)
    json_designer = page.get_by_role("region", name="Workflow designer", exact=True)
    select_workflow(json_definition["workflowId"])
    json_canvas = workbench(json_designer).locator(".vaw-graph-editor")
    json_canvas.get_by_role("button", name="Use JSON input and add a value selector", exact=True).click()
    selector = json_canvas.locator("li[data-node-type='core.json.select-string']")
    expect(selector).to_have_count(1)
    pointer = json_canvas.locator("input[aria-label^='JSON pointer for']")
    pointer.fill("/customer/name")
    pointer.press("Tab")
    workbench(json_designer).locator("[data-graph-save]").click()
    expect(workbench(json_designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
    json_saved = page.request.get(json_path + "/design").json()["draft"]["definition"]
    assert json_saved["schemaVersion"] == "1.9"
    assert json_saved["nodes"]["input"]["inputDataType"] == "json"
    assert next(node for node in json_saved["nodes"].values() if node["type"] == "core.json.select-string")["jsonPropertySelection"]["pointer"] == "/customer/name"
    if screenshots:
        workbench(designer).scroll_into_view_if_needed()
        page.screenshot(path=str(screenshots / "canvas-app.png"), full_page=True)
        show_runs(page)
        expect(viewer).to_be_visible()
        viewer.screenshot(path=str(screenshots / "canvas-playback.png"))
    assert not errors, errors
    print("Edge sockets, wire disconnect/rewire, invalid draft save and execution gating, per-agent output-token editing, cancellation, pointer/keyboard moves, undo/redo, comments, box selection/group moves, free pan/reset, section moves, wire reroutes/angles, zoom, JSON-input authoring, reload, CAS conflicts, saved-source execution, immutable run presentation, and recorded layout playback passed.")
    page.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots", type=Path)
    parser.add_argument("--base-url", default=BASE, help="Studio base URL (defaults to the standard local port)")
    args = parser.parse_args()
    BASE = args.base_url.rstrip("/")
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        options = {"headless": True}
        if args.browser_executable:
            options["executable_path"] = args.browser_executable
        browser = p.chromium.launch(**options)
        try:
            check(browser, args.screenshots)
        finally:
            browser.close()
