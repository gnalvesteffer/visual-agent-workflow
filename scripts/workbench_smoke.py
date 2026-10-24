"""Exercise compact nodes, selected properties, view switching and native graph fullscreen.

Uses deterministic text workflows and disposable Studio data. Requires an already
running development Studio on port 5099; no model/MCP credentials or calls.
"""
import argparse
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
from workbench_browser import workbench, inspect_control, show_runs

BASE = "http://127.0.0.1:5099"


def check(browser, screenshots):
    page = browser.new_page(viewport={"width":1920,"height":1080})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE)
    designer = page.get_by_role("region",name="Workflow designer",exact=True)
    expect(designer.get_by_role("button",name="New workflow",exact=True)).to_be_enabled()
    designer.get_by_role("button",name="New workflow",exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.",exact=True).wait_for()
    expect(designer.locator("li[data-node-id]")).to_have_count(2)
    designer.get_by_role("button",name="Add Uppercase",exact=True).click()
    expect(designer.locator("li[data-node-id]")).to_have_count(3)
    step = designer.locator("li[data-node-type='core.text.uppercase']").get_attribute("data-node-id")
    workflow_id = designer.get_by_label("Saved workflow",exact=True).input_value()
    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas).to_have_attribute("data-ready","true")
    assert canvas.locator("li[data-node-id] input, li[data-node-id] textarea, li[data-node-id] select").count() == 0
    assert canvas.locator("li[data-node-id]").evaluate_all("nodes => Math.max(...nodes.map(n=>n.getBoundingClientRect().height))") < 260
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_be_visible()
    # Node actions are contextual and retain a single undo entry for definition + placement.
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click(button="right")
    menu=canvas.get_by_role("dialog",name="Graph actions",exact=True)
    expect(menu).to_be_visible()
    expect(menu.get_by_label("Search node actions",exact=True)).to_be_focused()
    menu.get_by_role("button",name="Duplicate node",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    viewport=canvas.locator(".vaw-canvas-viewport")
    viewport.focus(); page.keyboard.press("Control+z")
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    # Space opens searchable creation without a mouse; Escape cancels without an edit.
    viewport.focus(); page.keyboard.press("Space")
    expect(menu).to_be_visible()
    menu.get_by_label("Search node actions",exact=True).fill("reverse")
    assert menu.locator("[data-node-key]").count() >= 1
    page.keyboard.press("Escape")
    expect(menu).to_have_count(0)
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    # Dragging a library entry onto empty space places it there on the selected path.
    viewport.scroll_into_view_if_needed()
    bounds=viewport.bounding_box()
    destination={"x":bounds["x"]+bounds["width"]*.5,"y":bounds["y"]+350}
    library=canvas.get_by_role("complementary",name="Workflow node library",exact=True)
    library.get_by_role("button",name="Add Reverse",exact=True).drag_to(viewport,target_position={"x":bounds["width"]*.5,"y":350})
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    dropped=canvas.locator("li[data-node-type='core.text.reverse']")
    assert abs(dropped.bounding_box()["x"]-destination["x"])<12
    assert abs(dropped.bounding_box()["y"]-destination["y"])<12
    viewport.focus();page.keyboard.press("Control+z")
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    # An output-pin gesture opens creation for that exact existing path.
    pin=canvas.locator(f"li[data-node-id='{step}'] .vaw-output-ports [data-socket]")
    pin.scroll_into_view_if_needed(); box=pin.bounding_box();bounds=viewport.bounding_box()
    page.mouse.move(box["x"]+box["width"]/2,box["y"]+box["height"]/2)
    page.mouse.down();page.mouse.move(bounds["x"]+bounds["width"]*.5,bounds["y"]+350,steps=8);page.mouse.up()
    expect(menu).to_be_visible()
    if screenshots:
        screenshots.mkdir(parents=True,exist_ok=True)
        page.screenshot(path=str(screenshots / "contextual-creation.png"))
    menu.get_by_role("button",name="Add Reverse",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    canvas.locator("li[data-node-type='core.text.reverse'] [data-drag-node]").focus()
    page.keyboard.press("Delete")
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    canvas.get_by_label("Node label",exact=True).fill("Normalize message")
    canvas.get_by_label("Node comment",exact=True).fill("Deterministic text processing. Inspect the recorded result after execution.")
    canvas.get_by_label("Node comment",exact=True).press("Tab")
    canvas.get_by_label(f"Operation for {step}",exact=True).select_option("core.text.reverse")
    expect(canvas.get_by_role("button",name=f"Move node {step}",exact=True)).to_contain_text("Normalize message")
    canvas.get_by_text("Labels, comments, sections, and wire routing",exact=True).click()
    canvas.get_by_role("button",name="Add reroute point",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(1)
    canvas.get_by_role("button",name="Arrange nodes",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(0)
    designer.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(1)
    designer.get_by_role("button",name="Redo edit",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(0)
    canvas.get_by_text("Labels, comments, sections, and wire routing",exact=True).click()
    designer.get_by_role("button",name="Save draft",exact=True).click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")
    before = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()

    # A narrow consuming host must not constrain native fullscreen to its old container query width.
    page.locator(".vaw-studio").evaluate("element => element.style.inlineSize = '780px'")
    canvas.get_by_role("button",name="Fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement !== null")
    assert page.evaluate("document.fullscreenElement.classList.contains('vaw-graph-editor')")
    assert canvas.bounding_box()["width"] >= 1900
    canvas.get_by_role("button",name="Fit graph",exact=True).click()
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_have_value("core.text.reverse")
    assert canvas.get_by_role("complementary",name="Selected node properties",exact=True).bounding_box()["x"] > 1000
    if screenshots:
        screenshots.mkdir(parents=True,exist_ok=True)
        page.screenshot(path=str(screenshots / "fullscreen-designer.png"))
    viewport.focus(); page.keyboard.press("Space")
    expect(menu).to_be_visible()
    page.keyboard.press("Escape")
    expect(menu).to_have_count(0)
    assert page.evaluate("document.fullscreenElement !== null"), "Closing actions must preserve fullscreen"
    # Tab wraps within fullscreen and does not escape into hidden application controls.
    page.evaluate("""() => {
        const root=document.fullscreenElement;
        const controls=[...root.querySelectorAll('button,input,select,textarea,summary,a[href],[tabindex]')]
            .filter(e=>e.tabIndex>=0&&!e.matches(':disabled')&&e.getClientRects().length);
        controls.at(-1).focus();
    }""")
    page.keyboard.press("Tab")
    assert page.evaluate("document.fullscreenElement.contains(document.activeElement)")
    page.keyboard.press("Escape")
    page.wait_for_function("document.fullscreenElement === null")
    expect(canvas.get_by_role("button",name="Fullscreen graph",exact=True)).to_be_focused()
    page.locator(".vaw-studio").evaluate("element => element.style.removeProperty('inline-size')")
    assert page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json() == before
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")

    designer.get_by_role("button",name="Publish & activate",exact=True).click()
    designer.get_by_text("Published and activated version 1.",exact=True).wait_for()
    designer.get_by_label("Published run input",exact=True).fill("Agent workflow")
    designer.get_by_role("button",name="Run selected version",exact=True).click()
    viewer=page.get_by_role("region",name="Workflow run viewer",exact=True)
    viewer.get_by_role("button",name="output core.output Completed",exact=True).click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("wolfkrow tnegA")
    assert not designer.is_visible()
    viewer.get_by_role("button",name="Fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement !== null")
    viewer.get_by_role("button",name="Fit graph",exact=True).click()
    assert viewer.locator(".vaw-wire-delivered").first.evaluate("e => Number.parseFloat(getComputedStyle(e).strokeWidth)") >= 5
    assert viewer.locator(".vaw-wire-delivered").first.evaluate("e => getComputedStyle(e).strokeDasharray") != "none"
    if screenshots:
        page.screenshot(path=str(screenshots / "fullscreen-execution.png"))
    slider=viewer.get_by_label("Recorded position",exact=True)
    slider.evaluate("e=>{e.value=0;e.dispatchEvent(new Event('input',{bubbles:true}));}")
    expect(viewer.locator(".vaw-wire-delivered")).to_have_count(0)
    viewer.get_by_role("button",name="Exit fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement === null")
    workbench(designer)
    inspect_control(designer,f"Operation for {step}")
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_have_value("core.text.reverse")
    designer.get_by_label("Find workflow",exact=True).fill("does-not-match-any-name")
    expect(designer.get_by_label("Saved workflow",exact=True)).to_have_value(workflow_id)
    show_runs(page)
    page.get_by_label("Filter recorded runs",exact=True).fill(workflow_id)
    expect(page.locator(".vaw-history button")).to_have_count(1)
    page.get_by_label("Filter recorded runs",exact=True).fill("no-matching-run")
    expect(page.locator(".vaw-history button")).to_have_count(0)
    page.get_by_label("Filter recorded runs",exact=True).fill("")
    workbench(designer)
    designer.get_by_label("Find workflow",exact=True).fill("")
    if screenshots:
        canvas.get_by_role("button",name="Fit graph",exact=True).click()
        page.wait_for_function("""() => {
            const v=document.querySelector('.vaw-designer .vaw-canvas-viewport').getBoundingClientRect();
            return [...document.querySelectorAll('.vaw-designer li[data-node-id]')].every(n=>{
                const b=n.getBoundingClientRect();return b.left>=v.left&&b.right<=v.right&&b.top>=v.top&&b.bottom<=v.bottom;});
        }""")
        page.evaluate("window.scrollTo(0,0)")
        page.screenshot(path=str(screenshots / "studio-designer.png"))
    # The false socket must create on the false arm, rather than the first available port.
    library.get_by_role("button",name="Add Condition",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(7)
    branch=canvas.locator("li[data-node-type='core.condition']").get_attribute("data-node-id")
    false_pin=canvas.locator(f"li[data-node-id='{branch}'] [data-socket='false']")
    false_pin.scroll_into_view_if_needed();box=false_pin.bounding_box();bounds=viewport.bounding_box()
    page.mouse.move(box["x"]+box["width"]/2,box["y"]+box["height"]/2)
    page.mouse.down();page.mouse.move(bounds["x"]+80,bounds["y"]+bounds["height"]-30,steps=8);page.mouse.up()
    expect(menu).to_be_visible()
    expect(menu.locator(".vaw-insertion-hint")).to_contain_text("false")
    menu.get_by_role("button",name="Add Reverse",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(8)
    false_target=canvas.locator(f"path[data-source='{branch}'][data-source-port='false']").first.get_attribute("data-target")
    expect(canvas.locator(f"li[data-node-id='{false_target}']")).to_have_attribute("data-node-type","core.text.reverse")
    canvas.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(7)
    canvas.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")
    # Permission rejection leaves an explicit error and a usable inline editor.
    canvas.evaluate("root => root.requestFullscreen = () => Promise.reject(new Error('fixture denial'))")
    canvas.get_by_role("button",name="Fullscreen graph",exact=True).click()
    expect(canvas.locator("[data-fullscreen-error]")).to_be_visible()
    assert page.evaluate("document.fullscreenElement === null")
    canvas.evaluate("root => delete root.requestFullscreen")
    page.set_viewport_size({"width":390,"height":844})
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "Narrow viewport overflow"
    assert not errors,errors
    page.close()
    print("Blank creation, node library/drop, context search/cancel/duplicate, output-pin creation, guarded keyboard deletion/undo, compact properties, route arrangement, native fullscreen/focus/rejection, narrow embeds, fit, recorded paths/playback and workspace filters passed.")


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots",type=Path)
    args=parser.parse_args()
    with sync_playwright() as p:
        options={"headless":True}
        if args.browser_executable:options["executable_path"]=args.browser_executable
        browser=p.chromium.launch(**options)
        try:check(browser,args.screenshots)
        finally:browser.close()
