"""Exercise the Studio branch-count builder, stable labels, saved pins, and execution."""

import os
import time
import uuid

from playwright.sync_api import expect, sync_playwright

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def definition(workflow_id):
    return {
        "schemaVersion": "1.31",
        "workflowId": workflow_id,
        "displayName": "Configurable parallel browser smoke",
        "entryNodeId": "input",
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "condition": {"type": "core.constant.boolean", "typeVersion": "1.0.0", "booleanConstant": {"value": True}},
            "branch": {"type": "core.branch.boolean", "typeVersion": "1.0.0"},
            "output": {"type": "core.output", "typeVersion": "1.0.0"},
            "failure": {"type": "core.terminate", "typeVersion": "1.0.0", "termination": {"outcome": "failure"}},
        },
        "connections": {
            "condition": {"source": {"nodeId": "condition", "portId": "out"}, "target": {"nodeId": "branch", "portId": "condition"}, "kind": "data"},
            "output": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"},
        },
        "executionConnections": {
            "activate": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "branch", "portId": "execute"}, "kind": "execution"},
            "success": {"source": {"nodeId": "branch", "portId": "true"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
            "failure": {"source": {"nodeId": "branch", "portId": "false"}, "target": {"nodeId": "failure", "portId": "execute"}, "kind": "execution"},
        },
    }


def check(browser):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}") if response.status >= 400 else None)
    workflow_id = "parallel-authoring-" + uuid.uuid4().hex
    authored = definition(workflow_id)
    response = page.request.put(BASE + f"/api/workflows/{workflow_id}/draft", data={"definition": authored, "expectedGeneration": 0})
    assert response.ok, response.text()

    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(designer.get_by_role("region", name="Configurable parallel graph")).to_be_visible()
    designer.get_by_label("Number of parallel branches", exact=True).select_option("3")
    designer.get_by_role("button", name="Create parallel workflow", exact=True).click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    design = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()
    stored = design["draft"]["definition"]
    assert stored["schemaVersion"] == "1.32", stored["schemaVersion"]
    fork_id = next(node_id for node_id, node in stored["nodes"].items() if node["type"] == "core.parallel.fork")
    join_id = next(node_id for node_id, node in stored["nodes"].items() if node["type"] == "core.parallel.join")
    fork = stored["nodes"][fork_id]["parallelFork"]["branches"]
    assert len(fork) == 3, fork
    assert [item["id"] for item in fork] == ["branch_1", "branch_2", "branch_3"]

    designer.get_by_label("Node to organize", exact=True).select_option(fork_id)
    designer.get_by_label("Label for branch branch_2", exact=True).fill("Review")
    designer.get_by_label("Label for branch branch_2", exact=True).press("Tab")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert stored["nodes"][fork_id]["parallelFork"]["branches"][1]["id"] == "branch_2"
    assert stored["nodes"][fork_id]["parallelFork"]["branches"][1]["label"] == "Review"

    designer.get_by_role("button", name="Add parallel branch", exact=True).click()
    designer.get_by_role("button", name="Move branch branch_4 up", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert [item["id"] for item in stored["nodes"][fork_id]["parallelFork"]["branches"]] == ["branch_1", "branch_2", "branch_4", "branch_3"]
    assert stored["nodes"][fork_id]["parallelFork"]["branches"][2]["label"] == "Branch 4"
    designer.get_by_role("button", name="Move branch branch_4 down", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert [item["id"] for item in stored["nodes"][fork_id]["parallelFork"]["branches"]] == ["branch_1", "branch_2", "branch_3", "branch_4"], stored["nodes"][fork_id]["parallelFork"]["branches"]
    designer.get_by_role("button", name="Remove empty branch branch_4", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert [item["id"] for item in stored["nodes"][fork_id]["parallelFork"]["branches"]] == ["branch_1", "branch_2", "branch_3"], stored["nodes"][fork_id]["parallelFork"]["branches"]
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    restored_branch = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert [item["id"] for item in restored_branch["nodes"][fork_id]["parallelFork"]["branches"]] == ["branch_1", "branch_2", "branch_3", "branch_4"]
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert [item["id"] for item in stored["nodes"][fork_id]["parallelFork"]["branches"]] == ["branch_1", "branch_2", "branch_3"]

    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas.locator(f"li[data-node-id='{fork_id}'] [data-socket='branch.branch_1']")).to_be_visible()
    expect(canvas.locator(f"li[data-node-id='{fork_id}'] [data-socket='branch.branch_3']")).to_be_visible()
    expect(canvas.locator(f"li[data-node-id='{join_id}'] [data-socket='branch.branch_2']")).to_be_visible()

    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    expect(designer).to_contain_text("Published and activated version 1.")
    designer.get_by_role("textbox", name="Published run input", exact=True).fill("fan out")
    designer.get_by_role("button", name="Run selected version", exact=True).click()
    selected = page.locator(".vaw-history button[aria-pressed='true'] small")
    expect(selected).to_be_visible(timeout=30_000)
    summaries = page.request.get(BASE + "/api/runs").json()
    run_id = next(item["header"]["runId"] for item in summaries if item["header"]["definition"]["workflowId"] == workflow_id)
    deadline = time.monotonic() + 15
    while True:
        snapshot = page.request.get(BASE + f"/api/runs/{run_id}").json()
        if snapshot["status"] in ("completed", "failed", "interrupted"):
            break
        assert time.monotonic() < deadline, f"Parallel run did not finish: {snapshot['status']}"
        time.sleep(0.1)
    assert snapshot["status"] == "completed", snapshot
    assert any(event["kind"] == "node.completed" and event["nodeId"] == "output" and '"branch_2":"fan out"' in event["data"] for event in snapshot["events"])
    page.get_by_role("button", name="Designer", exact=True).click()
    designer.get_by_label("Node to organize", exact=True).select_option(fork_id)
    designer.get_by_role("button", name="Remove parallel block…", exact=True).click()
    designer.get_by_role("button", name="Remove parallel block and reconnect", exact=True).click()
    expect(designer.get_by_role("region", name="Configurable parallel graph")).to_be_visible()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    restored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert restored["schemaVersion"] == "1.31"
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    restored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert restored["schemaVersion"] == "1.32"
    assert not errors, errors
    print("Studio created, reordered, removed, and undo/redid a branch; saved stable labels and pins; ran the graph; and removed/undid the block.")
    page.close()


if __name__ == "__main__":
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True, executable_path="/usr/bin/chromium")
        try:
            check(browser)
        finally:
            browser.close()
