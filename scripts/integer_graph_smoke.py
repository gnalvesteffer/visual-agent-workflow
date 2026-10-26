"""Exercise typed integer graph authoring, run evidence, and inspector UI in Studio."""

import os
import time
import uuid
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def definition(workflow_id):
    return {
        "schemaVersion": "1.31",
        "workflowId": workflow_id,
        "displayName": "Integer graph browser smoke",
        "entryNodeId": "input",
        "parameters": {"base": {"displayName": "Base", "defaultValue": "41", "type": "integer"}},
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "base": {"type": "core.workflow-value", "typeVersion": "1.0.0", "parameterId": "base", "outputDataType": "integer"},
            "one": {"type": "core.constant.integer", "typeVersion": "1.0.0", "integerConstant": {"value": 1}},
            "expected": {"type": "core.constant.integer", "typeVersion": "1.0.0", "integerConstant": {"value": 42}},
            "sum": {"type": "core.integer.add", "typeVersion": "1.0.0"},
            "equals": {"type": "core.integer.equals", "typeVersion": "1.0.0"},
            "branch": {"type": "core.branch.boolean", "typeVersion": "1.0.0"},
            "output": {"type": "core.output", "typeVersion": "1.0.0", "outputDataType": "integer"},
            "failure": {"type": "core.terminate", "typeVersion": "1.0.0", "termination": {"outcome": "failure"}},
        },
        "connections": {
            "base-left": {"source": {"nodeId": "base", "portId": "out"}, "target": {"nodeId": "sum", "portId": "left"}, "kind": "data"},
            "one-right": {"source": {"nodeId": "one", "portId": "out"}, "target": {"nodeId": "sum", "portId": "right"}, "kind": "data"},
            "sum-left": {"source": {"nodeId": "sum", "portId": "out"}, "target": {"nodeId": "equals", "portId": "left"}, "kind": "data"},
            "expected-right": {"source": {"nodeId": "expected", "portId": "out"}, "target": {"nodeId": "equals", "portId": "right"}, "kind": "data"},
            "comparison": {"source": {"nodeId": "equals", "portId": "out"}, "target": {"nodeId": "branch", "portId": "condition"}, "kind": "data"},
            "workflow-result": {"source": {"nodeId": "sum", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"},
        },
        "executionConnections": {
            "activate": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "branch", "portId": "execute"}, "kind": "execution"},
            "success": {"source": {"nodeId": "branch", "portId": "true"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
            "failure": {"source": {"nodeId": "branch", "portId": "false"}, "target": {"nodeId": "failure", "portId": "execute"}, "kind": "execution"},
        },
    }


def check(browser, screenshot_dir):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}") if response.status >= 400 else None)
    workflow_id = "integer-graph-smoke-" + uuid.uuid4().hex
    authored = definition(workflow_id)
    response = page.request.put(BASE + f"/api/workflows/{workflow_id}/draft", data={"definition": authored, "expectedGeneration": 0})
    assert response.ok, response.text()
    design_path = BASE + f"/api/workflows/{workflow_id}/design"
    design = page.request.get(design_path)
    assert design.ok, design.text()
    layout = design.json()["layout"]
    positions = {
        "input": (80, 280), "base": (420, 140), "one": (420, 420),
        "sum": (760, 140), "expected": (760, 420), "equals": (1100, 140),
        "branch": (1440, 140), "output": (1780, 60), "failure": (1780, 360),
    }
    for node_id, (x, y) in positions.items():
        layout["nodes"][node_id]["position"] = {"x": x, "y": y}
    saved_design = page.request.put(design_path, data={"definition": authored, "layout": layout, "expectedGeneration": design.json()["draft"]["generation"]})
    assert saved_design.ok, saved_design.text()

    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(designer.locator(".vaw-graph-editor")).to_have_attribute("data-ready", "true")
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    designer.get_by_role("button", name="Fit graph", exact=True).click()

    library = designer.locator(".vaw-library-panel")
    for key in ("core.constant.integer", "core.integer.add", "core.integer.equals"):
        expect(library.locator(f'[data-node-key="{key}"]')).to_be_visible()
    expect(library.get_by_role("button", name="Add Workflow value", exact=True)).to_be_visible()

    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas.locator("li[data-node-id='sum']")).to_contain_text("Checked integer addition")
    expect(canvas.locator("li[data-node-id='base']")).to_contain_text("Base")
    expect(canvas.locator("li[data-node-id='base']").locator("[data-socket='out']")).to_be_visible()
    expect(canvas.locator("li[data-node-id='sum']").locator("[data-socket='left']")).to_be_visible()

    designer.get_by_label("Node to organize", exact=True).select_option("one")
    constant = designer.get_by_label("Integer constant for one", exact=True)
    constant.fill("2")
    constant.blur()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    saved = page.request.get(BASE + f"/api/workflows/{workflow_id}")
    assert saved.ok, saved.text()
    saved_constant = saved.json()["definition"]["nodes"]["one"]["integerConstant"]["value"]
    assert saved_constant == 2, f"Integer constant edit did not persist; saved {saved_constant!r}"

    # Restore the expected result to match 41 + 1 and validate the recorded typed output.
    designer.get_by_label("Integer constant for one", exact=True).fill("1")
    designer.get_by_label("Integer constant for one", exact=True).press("Tab")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    if screenshot_dir:
        Path(screenshot_dir).mkdir(parents=True, exist_ok=True)
        page.screenshot(path=str(Path(screenshot_dir) / "integer-graph-editor.png"), full_page=True)
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    expect(designer).to_contain_text("Published and activated version 1.")
    revisions = page.request.get(BASE + f"/api/workflows/{workflow_id}/revisions")
    assert revisions.ok, revisions.text()
    revision_id = revisions.json()[0]["revision"]["revisionId"]
    started = page.request.post(BASE + f"/api/revisions/{revision_id}/runs", data={"input": "unused", "parameters": {"base": "41"}})
    assert started.status == 202, started.text()
    run_id = started.json()["header"]["runId"]
    deadline = time.monotonic() + 15
    while True:
        snapshot = page.request.get(BASE + f"/api/runs/{run_id}")
        assert snapshot.ok, snapshot.text()
        recorded = snapshot.json()
        if recorded["status"] in ("completed", "failed", "interrupted"):
            break
        assert time.monotonic() < deadline, f"Workflow run did not finish: {recorded['status']}"
        time.sleep(0.1)
    assert recorded["status"] == "completed", recorded
    sum_evidence = [event for event in recorded["events"] if event["kind"] == "data.evaluated" and event["nodeId"] == "sum"]
    assert any('"dataType":"integer"' in event["data"] and '"value":42' in event["data"] for event in sum_evidence), sum_evidence
    assert any(event["kind"] == "branch.selected" and event["nodeId"] == "branch" for event in recorded["events"])

    page.get_by_role("button", name="Run inspector", exact=True).click()
    expect(page.get_by_role("region", name="Workflow workspace")).to_be_visible()
    page.get_by_role("button", name="Refresh history", exact=True).click()
    page.get_by_label("Filter recorded runs", exact=True).fill(workflow_id)
    expect(page.locator(".vaw-history button")).to_have_count(1)
    page.locator(".vaw-history button").click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_role("button", name="Fit graph", exact=True).click()
    viewer.get_by_label("Node to organize", exact=True).select_option("sum")
    expect(viewer.locator(".vaw-graph-editor")).to_be_visible()
    expect(viewer.locator(".vaw-graph-editor")).to_contain_text("Add integers")
    expect(viewer).to_contain_text("42")
    if screenshot_dir:
        Path(screenshot_dir).mkdir(parents=True, exist_ok=True)
        page.screenshot(path=str(Path(screenshot_dir) / "integer-graph-inspector.png"), full_page=True)

    page.goto(BASE + "/functions", wait_until="networkidle")
    expect(page.get_by_role("heading", name="Function library", exact=True)).to_be_visible()
    input_type = page.locator('section[aria-label="Function inputs"] .vaw-function-port select')
    output_type = page.locator('section[aria-label="Function outputs"] .vaw-function-port select')
    input_type.select_option("integer")
    output_type.select_option("integer")
    function_canvas = page.locator(".vaw-graph-editor")
    expect(function_canvas.locator("li[data-node-id='input']")).to_contain_text("out · integer")
    expect(function_canvas.locator("li[data-node-id='output']")).to_contain_text("in · integer")
    page.get_by_role("button", name="Integer constant", exact=True).click()
    page.get_by_role("button", name="Add integers", exact=True).click()
    integer_add = function_canvas.locator("li").filter(has_text="Checked integer addition")
    expect(integer_add).to_contain_text("left · integer")
    expect(integer_add).to_contain_text("right · integer")
    expect(integer_add).to_contain_text("out · integer")
    if screenshot_dir:
        page.screenshot(path=str(Path(screenshot_dir) / "integer-function-editor.png"), full_page=True)
    assert not errors, errors
    print("Studio rendered typed integer workflow and function pins; the API run recorded Int64 values and selected the successful branch, and playback displayed 42.")
    page.close()


def main():
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--screenshots", default="/tmp/vaw-integer-graph-screenshots")
    args = parser.parse_args()
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True, executable_path="/usr/bin/chromium")
        try:
            check(browser, args.screenshots)
        finally:
            browser.close()


if __name__ == "__main__":
    main()
