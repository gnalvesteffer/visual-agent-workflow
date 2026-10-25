"""Exercise actual graph gestures, annotation controls, persistence, and recorded layouts.

Requires a running development Studio and Playwright/Chromium. Creates disposable
local workflows and runs; never uses external model providers or side effects.
"""

from workbench_browser import workbench, inspect_control, show_runs
import argparse
import uuid
import re
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

BASE = "http://127.0.0.1:5099"


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

    def open_editor():
        page.goto(BASE)
        designer = page.get_by_role("region", name="Workflow designer", exact=True)
        expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
        workbench(designer).get_by_label("Saved workflow", exact=True).select_option(workflow_id)
        expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text(workflow_id)
        expect(workbench(designer).locator(".vaw-graph-editor")).to_have_attribute("data-ready", "true")
        return designer

    designer = open_editor()
    canvas = workbench(designer).locator(".vaw-graph-editor")
    nodes = workbench(designer).locator("ol[aria-label='Editable pipeline nodes'] > li")
    step = nodes.filter(has=page.get_by_role("button", name="Move node step-1", exact=True))
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
    workbench(designer).get_by_label("Node to organize", exact=True).select_option("step-1")
    expect(workbench(designer).get_by_role("spinbutton", name="Node X", exact=True)).to_have_value(str(moved_x))
    workbench(designer).get_by_label("Node label", exact=True).fill("Normalize input")
    workbench(designer).get_by_label("Node comment", exact=True).fill("Convert the incoming text before forwarding it.")
    workbench(designer).get_by_label("Node comment", exact=True).press("Tab")
    expect(handle).to_contain_text("Normalize input")
    expect(step.locator(".vaw-node-comment")).to_have_text("Convert the incoming text before forwarding it.")

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
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    saved = design()
    assert saved["draft"]["definition"] == definition, "Presentation editing changed executable JSON"
    assert saved["layout"]["nodes"]["step-1"]["label"] == "Normalize input"
    assert saved["layout"]["wires"]["link-1"]["points"][0]["angle"] == 90
    assert list(saved["layout"]["sections"].values())[0]["label"] == "Text preparation"

    stale = page.request.put(path + "/design", data={"definition":definition,"layout":original["layout"],"expectedGeneration":original["draft"]["generation"]})
    assert stale.status == 409
    assert design() == saved
    designer = open_editor()
    canvas = workbench(designer).locator(".vaw-graph-editor")
    expect(inspect_control(designer, "Move node step-1").get_by_role("button", name="Move node step-1", exact=True)).to_contain_text("Normalize input")
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
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    revision = page.request.get(path + "/revisions").json()[0]["revision"]
    pinned = page.request.get(BASE + "/api/revisions/" + revision["revisionId"] + "/layout").json()
    assert pinned == saved["layout"]
    workbench(designer).get_by_label("Published run input", exact=True).fill("canvas proof")
    workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_role("button", name="output core.output Completed", exact=True).click()
    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("FOORP SAVNAC")
    expect(viewer.get_by_role("button", name="Inspect node step-1", exact=True)).to_contain_text("Normalize input")
    expect(viewer.locator(".vaw-wire-delivered").first).to_be_attached()
    old_layout = design()["layout"]
    drag(page, viewer.get_by_role("button", name="Inspect node step-1", exact=True), 48, 0)
    assert design()["layout"] == old_layout, "Playback changed the saved layout"
    # Published previews and restores must carry the original presentation, not today's draft.
    workbench(designer).get_by_text("Labels, comments, sections, and wire routing", exact=True).click()
    workbench(designer).get_by_label("Node to organize", exact=True).select_option("step-1")
    workbench(designer).get_by_label("Node label", exact=True).fill("Changed draft label")
    workbench(designer).get_by_label("Node label", exact=True).press("Tab")
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 2.", exact=True).wait_for()
    workbench(designer).get_by_label("Published version", exact=True).select_option(revision["revisionId"])
    workbench(designer).get_by_role("button", name="View selected version", exact=True).click()
    workbench(designer).get_by_text("Viewing published version 1 (read-only).", exact=True).wait_for()
    expect(workbench(designer).get_by_role("button", name="Inspect node step-1", exact=True)).to_contain_text("Normalize input")
    workbench(designer).get_by_role("button", name="Return to draft", exact=True).click()
    workbench(designer).get_by_role("button", name="Restore selected to draft", exact=True).click()
    workbench(designer).get_by_text("Version restored to draft. Publish to create a new version; active runs are unchanged.", exact=True).wait_for()
    assert design()["layout"] == pinned
    if screenshots:
        workbench(designer).scroll_into_view_if_needed()
        page.screenshot(path=str(screenshots / "canvas-app.png"), full_page=True)
        show_runs(page)
        expect(viewer).to_be_visible()
        viewer.screenshot(path=str(screenshots / "canvas-playback.png"))
    assert not errors, errors
    print("Edge sockets, cancellation, pointer/keyboard moves, undo/redo, comments, section moves, wire reroutes/angles, zoom, reload, CAS conflicts, publication, version preview/restore, and recorded layout playback passed.")
    page.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots", type=Path)
    args = parser.parse_args()
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
