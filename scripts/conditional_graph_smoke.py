"""Verify conditional authoring, real framework paths, pinning and recorded playback.

Creates disposable local definitions/runs against the development Studio.
"""

from workbench_browser import workbench, inspect_control, new_workflow_fixture, show_runs
import argparse
import copy
import json
from pathlib import Path
import uuid
from playwright.sync_api import sync_playwright, expect

BASE = "http://127.0.0.1:5099"


def check(browser, screenshots):
    page = browser.new_page(viewport={"width":1600,"height":1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    def get(path):
        response = page.request.get(BASE + path)
        assert response.ok, response.text()
        return response.json()
    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="New workflow", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    workflow_id = workbench(designer).locator(".vaw-definition-status strong").inner_text()
    nodes = workbench(designer).locator(".vaw-node[data-node-id]")
    expect(nodes).to_have_count(3)
    inspect_control(designer, "Add condition after input").get_by_role("button", name="Add condition after input", exact=True).click()
    expect(nodes).to_have_count(5)
    assert workbench(designer).locator(".vaw-node[data-node-type='core.condition']").count() == 1
    assert workbench(designer).locator(".vaw-node[data-node-type='core.merge']").count() == 1
    assert workbench(designer).locator(".vaw-node[data-node-type='core.uppercase'], .vaw-node[data-node-type='core.reverse']").count() == 0
    branch = workbench(designer).locator(".vaw-node[data-node-type='core.condition']").get_attribute("data-node-id")
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(nodes).to_have_count(3)
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(nodes).to_have_count(5)
    branch_card = workbench(designer).locator(f".vaw-node[data-node-id='{branch}']")
    expect(branch_card.locator('[data-socket="true"]')).to_have_count(1)
    expect(branch_card.locator('[data-socket="false"]')).to_have_count(1)
    inspect_control(designer, "Condition operator for " + branch).get_by_label("Condition operator for " + branch, exact=True).select_option("contains")
    operand = inspect_control(designer, "Condition operand for " + branch).get_by_label("Condition operand for " + branch, exact=True)
    operand.fill("allow"); operand.press("Tab")
    expect(operand).to_have_value("allow")
    # Branches are pass-through by default; add processing steps only when requested.
    insert_after_true = inspect_control(designer, f"New node operation for {branch}")
    insert_after_true.get_by_label("New node operation", exact=True).select_option("core.text.uppercase")
    insert_after_true.get_by_role("button", name=f"Add node after {branch}.true", exact=True).click()
    insert_after_false = inspect_control(designer, f"New node operation for {branch}")
    insert_after_false.get_by_label("New node operation", exact=True).select_option("core.text.reverse")
    insert_after_false.get_by_role("button", name=f"Add node after {branch}.false", exact=True).click()
    expect(nodes).to_have_count(7)
    workbench(designer).get_by_role("button", name="Arrange nodes", exact=True).click()
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    design = get(f"/api/workflows/{workflow_id}/design")
    definition = design["draft"]["definition"]
    assert definition["schemaVersion"] == "1.4"
    assert definition["nodes"][branch]["condition"] == {"operator":"contains","operand":"allow"}
    branch_edges = {edge["source"]["portId"]:(edge_id,edge["target"]["nodeId"])
                    for edge_id,edge in definition["connections"].items() if edge["source"]["nodeId"] == branch}
    true_id, false_id = branch_edges["true"][1], branch_edges["false"][1]
    # The actual rendered wire endpoints must honor the selected source port, not a shared 'out'.
    page.wait_for_function("""id => [...document.querySelectorAll('.vaw-designer path[data-wire]')]
        .filter(p=>p.dataset.source===id).every(p=>p.getAttribute('d')?.startsWith('M'))""", arg=branch)
    coordinates = branch_card.evaluate("""node => {
        const body=node.getBoundingClientRect();
        return [...node.querySelectorAll('[data-socket]')].map(pin=>{const r=pin.getBoundingClientRect();
            return {name:pin.dataset.socket,x:r.left+r.width/2-body.left,y:r.top+r.height/2,width:body.width};});
    }""")
    sockets = {pin["name"]:pin for pin in coordinates}
    assert abs(sockets["true"]["x"]-sockets["true"]["width"]) < 6
    assert abs(sockets["false"]["x"]-sockets["false"]["width"]) < 6
    assert sockets["true"]["y"] != sockets["false"]["y"]
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    revision = get(f"/api/workflows/{workflow_id}/revisions")[0]["revision"]
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    for text, port, output in (("allow abc", "true", "ALLOW ABC"), ("abc", "false", "CBA")):
        workbench(designer).get_by_label("Published run input", exact=True).fill(text)
        previous_header = viewer.locator(".vaw-run-header small").inner_text() if viewer.locator(".vaw-run-header small").count() else None
        workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
        if previous_header:
            expect(viewer.locator(".vaw-run-header small")).not_to_have_text(previous_header)
        viewer.get_by_role("button", name="output core.output Completed", exact=True).click()
        expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text(output)
        expect(viewer.get_by_text("Selected output: " + port, exact=True)).to_be_visible()
        selected = page.locator(".vaw-history button[aria-pressed='true'] small").inner_text()
        summaries = get("/api/runs")
        run_id = next(r["header"]["runId"] for r in summaries if r["header"]["runId"].startswith(selected))
        recorded = get("/api/runs/" + run_id)
        assert recorded["header"]["revisionId"] == revision["revisionId"]
        chosen, skipped = branch_edges[port], branch_edges["false" if port == "true" else "true"]
        assert any(e["kind"] == "connection.received" and e.get("connectionId") == chosen[0] for e in recorded["events"])
        assert not any(e.get("nodeId") == skipped[1] for e in recorded["events"])
        assert viewer.locator(f"path[data-wire='{chosen[0]}'].vaw-wire-delivered").count() == 1
        assert viewer.locator(f"path[data-wire='{skipped[0]}'].vaw-wire-delivered").count() == 0
        slider = viewer.get_by_label("Recorded position", exact=True)
        decision = next(e["sequence"] for e in recorded["events"] if e["kind"] == "branch.selected")
        slider.evaluate("""(input,position)=>{input.value=position;input.dispatchEvent(new Event('input',{bubbles:true}));}""", decision-1)
        expect(viewer.get_by_text("Selected output: " + port, exact=True)).to_have_count(0)
        assert get("/api/runs/" + run_id) == recorded
        viewer.get_by_role("button", name="Follow latest", exact=True).click()
        expect(viewer.get_by_text("Selected output: " + port, exact=True)).to_be_visible()
    if screenshots:
        screenshots.mkdir(parents=True, exist_ok=True)
        viewer.screenshot(path=str(screenshots / "conditional-false-path.png"))
    package = get(f"/api/revisions/{revision['revisionId']}/package")
    assert package["definition"] == definition
    assert any(r["id"] == "core.condition" for r in package["requirements"])
    # A changed draft must not alter execution of v1.
    inspect_control(designer, "Condition operand for " + branch)
    operand.fill("never-match"); operand.press("Tab")
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    response = page.request.post(BASE + f"/api/revisions/{revision['revisionId']}/runs", data={"input":"allow abc"})
    assert response.ok, response.text()
    pinned_id = response.json()["header"]["runId"]
    page.wait_for_function("""async id => (await (await fetch('/api/runs/'+id)).json()).status==='completed'""", arg=pinned_id)
    pinned = get("/api/runs/" + pinned_id)
    assert pinned["events"][-1]["data"] == "ALLOW ABC"
    assert pinned["header"]["contentHash"] == revision["contentHash"]
    # Unsupported wiring is rejected through the public API before execution.
    invalid = copy.deepcopy(definition)
    invalid["connections"][branch_edges["false"][0]]["source"]["portId"] = "true"
    rejected = page.request.post(BASE + "/api/runs", data={"definition":invalid,"input":"abc"})
    assert rejected.status == 400
    page.reload()
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    workbench(designer).get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(inspect_control(designer, "Condition operand for " + branch).get_by_label("Condition operand for " + branch, exact=True)).to_have_value("never-match")
    # Removing a block is explicit, cancelable, and one definition/layout undo unit.
    nodes = workbench(designer).locator(".vaw-node[data-node-id]")
    layout_before_removal = get(f"/api/workflows/{workflow_id}/design")["layout"]
    inspector = inspect_control(designer, "Condition operand for " + branch)
    branch_handle = branch_card.locator("[data-drag-node]")
    branch_handle.click()
    expect(branch_handle).to_have_attribute("aria-pressed", "true")
    branch_handle.press("Delete")
    confirmation = workbench(designer).get_by_role("region", name="Confirm conditional block removal", exact=True)
    expect(confirmation).to_contain_text("Remove these 4 nodes")
    expect(confirmation.locator("li")).to_have_count(4)
    if screenshots:
        confirmation.screenshot(path=str(screenshots / "conditional-removal-confirmation.png"))
    confirmation.get_by_role("button", name="Cancel block removal", exact=True).click()
    expect(confirmation).to_have_count(0)
    expect(nodes).to_have_count(7)
    inspector.get_by_role("button", name="Remove conditional block…", exact=True).click()
    confirmation.get_by_role("button", name="Remove block and reconnect", exact=True).click()
    expect(nodes).to_have_count(3)
    expect(workbench(designer).locator(".vaw-node[data-node-type='core.condition']")).to_have_count(0)
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(nodes).to_have_count(7)
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    assert get(f"/api/workflows/{workflow_id}/design")["layout"] == layout_before_removal
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(nodes).to_have_count(3)
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    removed = get(f"/api/workflows/{workflow_id}/design")["draft"]["definition"]
    assert set(removed["nodes"]) == {"input", "step-1", "output"}
    assert get(f"/api/revisions/{revision['revisionId']}/package")["definition"] == definition
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 2.", exact=True).wait_for()
    new_revision = get(f"/api/workflows/{workflow_id}/revisions")[0]["revision"]
    response = page.request.post(BASE + f"/api/revisions/{new_revision['revisionId']}/runs", data={"input":"abc"})
    assert response.ok, response.text()
    removed_run_id = response.json()["header"]["runId"]
    page.wait_for_function("""async id => (await (await fetch('/api/runs/'+id)).json()).status==='completed'""", arg=removed_run_id)
    removed_run = get("/api/runs/" + removed_run_id)
    assert removed_run["events"][-1]["data"] == "ABC"
    assert not any(event["kind"] == "branch.selected" for event in removed_run["events"])
    assert not errors, errors
    page.close()
    print("Conditional insertion/undo/redo, predicate editing, edge sockets, true/false framework paths, merge, recorded playback, package/reload, revision pinning, invalid wiring rejection, and confirmed block removal/undo/redo/bypass execution passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots", type=Path)
    args = parser.parse_args()
    with sync_playwright() as playwright:
        options = {"headless":True}
        if args.browser_executable:
            options["executable_path"] = args.browser_executable
        browser = playwright.chromium.launch(**options)
        try:
            check(browser, args.screenshots)
        finally:
            browser.close()
