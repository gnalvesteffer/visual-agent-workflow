"""Exercise actual graph gestures, annotation controls, persistence, and recorded layouts.

Requires a running development Studio and Playwright/Chromium. Creates disposable
local workflows and runs; never uses external model providers or side effects.
"""
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
        expect(designer.get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
        designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
        expect(designer.locator(".vaw-definition-status")).to_contain_text(workflow_id)
        expect(designer.locator(".vaw-graph-editor")).to_have_attribute("data-ready", "true")
        return designer

    designer = open_editor()
    canvas = designer.locator(".vaw-graph-editor")
    nodes = designer.locator("ol[aria-label='Editable pipeline nodes'] > li")
    step = nodes.filter(has=page.get_by_role("button", name="Move node step-1", exact=True))
    original = design()
    before_x = original["layout"]["nodes"]["step-1"]["position"]["x"]
    before_y = original["layout"]["nodes"]["step-1"]["position"]["y"]
    handle = designer.get_by_role("button", name="Move node step-1", exact=True)
    drag(page, handle, 64, 96)
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    expect(step).to_have_css("left", f"{before_x + 64}px")
    expect(step).to_have_css("top", f"{before_y + 96}px")
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x}px")
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x + 64}px")

    # Arrow keys provide an alternative to dragging and produce the same undoable edit.
    handle.focus()
    handle.press("ArrowRight")
    expect(step).to_have_css("left", f"{before_x + 72}px")
    designer.get_by_text("Labels, comments, sections, and wire routing", exact=True).click()
    designer.get_by_label("Node to organize", exact=True).select_option("step-1")
    expect(designer.get_by_role("spinbutton", name="Node X", exact=True)).to_have_value(str(before_x + 72))
    designer.get_by_label("Node label", exact=True).fill("Normalize input")
    designer.get_by_label("Node comment", exact=True).fill("Convert the incoming text before forwarding it.")
    designer.get_by_label("Node comment", exact=True).press("Tab")
    expect(handle).to_contain_text("Normalize input")
    expect(step.locator(".vaw-node-comment")).to_have_text("Convert the incoming text before forwarding it.")

    # Membership is explicit; moving the section moves only its nodes in one edit.
    designer.get_by_label("New section label", exact=True).fill("Text preparation")
    designer.get_by_label("Include input in section", exact=True).check()
    designer.get_by_label("Include step-1 in section", exact=True).check()
    designer.get_by_role("button", name="Group selected nodes", exact=True).click()
    section_handle = designer.get_by_role("button", name="Move section Text preparation", exact=True)
    expect(section_handle).to_be_visible()
    output = nodes.filter(has=page.get_by_role("button", name="Move node output", exact=True))
    output_position = output.evaluate("e => [e.style.left,e.style.top]")
    drag(page, section_handle, 40, 32)
    expect(step).to_have_css("left", f"{before_x + 112}px")
    assert output.evaluate("e => [e.style.left,e.style.top]") == output_position
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x + 72}px")
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    expect(step).to_have_css("left", f"{before_x + 112}px")

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
    designer.get_by_label("Wire to organize", exact=True).select_option("link-1")
    designer.get_by_label("Custom angle for waypoint 1", exact=True).fill("30")
    designer.get_by_label("Custom angle for waypoint 1", exact=True).press("Tab")
    expect(designer.get_by_label("Pinned angle for waypoint 1", exact=True)).to_have_value("30")
    designer.get_by_label("Pinned angle for waypoint 1", exact=True).select_option("90")
    expect(wire).to_have_attribute("data-points", re.compile('"angle":90'))
    initial_reroute = reroute.evaluate("e=>({x:parseFloat(e.style.left),y:parseFloat(e.style.top)})")
    drag(page, reroute, 32, 48)
    expect(reroute).to_have_css("left", str(initial_reroute["x"] + 32) + "px")
    designer.get_by_label("Wire style", exact=True).select_option("orthogonal")
    designer.get_by_label("Pinned angle for waypoint 1", exact=True).select_option("90")
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
    designer.get_by_role("button", name="Zoom out graph", exact=True).click()
    old = step.evaluate("e=>parseFloat(e.style.left)")
    drag(page, handle, 72, 0)  # 72 screen pixels at 90% = 80 logical units.
    expect(step).to_have_css("left", f"{old + 80}px")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved = design()
    assert saved["draft"]["definition"] == definition, "Presentation editing changed executable JSON"
    assert saved["layout"]["nodes"]["step-1"]["label"] == "Normalize input"
    assert saved["layout"]["wires"]["link-1"]["points"][0]["angle"] == 90
    assert list(saved["layout"]["sections"].values())[0]["label"] == "Text preparation"

    stale = page.request.put(path + "/design", data={"definition":definition,"layout":original["layout"],"expectedGeneration":original["draft"]["generation"]})
    assert stale.status == 409
    assert design() == saved
    designer = open_editor()
    canvas = designer.locator(".vaw-graph-editor")
    expect(designer.get_by_role("button", name="Move node step-1", exact=True)).to_contain_text("Normalize input")
    expect(canvas.get_by_role("button", name="Reroute 1 on link-1", exact=True)).to_be_attached()
    expect(designer.get_by_role("button", name="Move section Text preparation", exact=True)).to_be_attached()
    header = designer.get_by_role("button", name="Move node step-1", exact=True)
    header.scroll_into_view_if_needed()
    current_node = designer.locator("li[data-node-id='step-1']")
    canonical_position = current_node.evaluate("e=>[e.style.left,e.style.top]")
    box = header.bounding_box()
    page.mouse.move(box["x"] + 40, box["y"] + 20)
    page.mouse.down()
    page.mouse.move(box["x"] + 88, box["y"] + 44, steps=4)
    header.dispatch_event("pointercancel", {"pointerId":1})
    page.mouse.up()
    assert current_node.evaluate("e=>[e.style.left,e.style.top]") == canonical_position
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    designer.get_by_text("Published and activated version 1.", exact=True).wait_for()
    revision = page.request.get(path + "/revisions").json()[0]["revision"]
    pinned = page.request.get(BASE + "/api/revisions/" + revision["revisionId"] + "/layout").json()
    assert pinned == saved["layout"]
    designer.get_by_label("Published run input", exact=True).fill("canvas proof")
    designer.get_by_role("button", name="Run selected version", exact=True).click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_role("button", name="output core.output Completed", exact=True).click()
    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("FOORP SAVNAC")
    expect(viewer.get_by_role("button", name="Inspect node step-1", exact=True)).to_contain_text("Normalize input")
    expect(viewer.locator(".vaw-wire-delivered").first).to_be_attached()
    old_layout = design()["layout"]
    drag(page, viewer.get_by_role("button", name="Inspect node step-1", exact=True), 48, 0)
    assert design()["layout"] == old_layout, "Playback changed the saved layout"
    # Published previews and restores must carry the original presentation, not today's draft.
    designer.get_by_text("Labels, comments, sections, and wire routing", exact=True).click()
    designer.get_by_label("Node to organize", exact=True).select_option("step-1")
    designer.get_by_label("Node label", exact=True).fill("Changed draft label")
    designer.get_by_label("Node label", exact=True).press("Tab")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    designer.get_by_text("Published and activated version 2.", exact=True).wait_for()
    designer.get_by_label("Published version", exact=True).select_option(revision["revisionId"])
    designer.get_by_role("button", name="View selected version", exact=True).click()
    designer.get_by_text("Viewing published version 1 (read-only).", exact=True).wait_for()
    expect(designer.get_by_role("button", name="Inspect node step-1", exact=True)).to_contain_text("Normalize input")
    designer.get_by_role("button", name="Return to draft", exact=True).click()
    designer.get_by_role("button", name="Restore selected to draft", exact=True).click()
    designer.get_by_text("Version restored to draft. Publish to create a new version; active runs are unchanged.", exact=True).wait_for()
    assert design()["layout"] == pinned
    if screenshots:
        designer.scroll_into_view_if_needed()
        page.screenshot(path=str(screenshots / "canvas-app.png"), full_page=True)
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
