"""Exercise schema 1.34 branch authoring, draft recovery, and publish/run gates."""

import json
import os
import uuid

from playwright.sync_api import expect, sync_playwright

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def integer_graph(workflow_id):
    return {
        "schemaVersion": "1.31", "workflowId": workflow_id,
        "displayName": "Branch local authoring smoke", "entryNodeId": "input",
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


def valid_conditional_region(workflow_id):
    branches = [
        {"id": "branch_1", "label": "Branch 1", "anchorNodeId": "anchor_1"},
        {"id": "branch_2", "label": "Branch 2", "anchorNodeId": "anchor_2"},
    ]
    return {
        "schemaVersion": "1.34", "workflowId": workflow_id,
        "displayName": "Branch local authoring smoke", "entryNodeId": "input",
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "fork": {"type": "core.parallel.fork", "typeVersion": "1.0.0", "parallelFork": {"branches": branches}},
            "anchor_1": {"type": "core.parallel.branch", "typeVersion": "1.0.0", "parallelBranch": {"forkNodeId": "fork", "branchId": "branch_1"}},
            "anchor_2": {"type": "core.parallel.branch", "typeVersion": "1.0.0", "parallelBranch": {"forkNodeId": "fork", "branchId": "branch_2"}},
            "condition": {"type": "core.constant.boolean", "typeVersion": "1.0.0", "booleanConstant": {"value": True}},
            "route": {"type": "core.branch.boolean", "typeVersion": "1.0.0"},
            "shared": {"type": "core.agent", "typeVersion": "1.0.0", "agent": {"providerId": "demo.scripted", "instructions": "Branch local shared continuation", "tools": []}},
            "join": {"type": "core.parallel.join", "typeVersion": "1.0.0", "parallelJoin": {"forkNodeId": "fork", "branchIds": ["branch_1", "branch_2"]}},
            "output": {"type": "core.output", "typeVersion": "1.0.0", "outputDataType": "json"},
        },
        "connections": {
            "condition": {"source": {"nodeId": "condition", "portId": "out"}, "target": {"nodeId": "route", "portId": "condition"}, "kind": "data"},
            "result": {"source": {"nodeId": "join", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"},
        },
        "executionConnections": {
            "start": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "fork", "portId": "execute"}, "kind": "execution"},
            "fork-1": {"source": {"nodeId": "fork", "portId": "branch.branch_1"}, "target": {"nodeId": "anchor_1", "portId": "execute"}, "kind": "execution"},
            "fork-2": {"source": {"nodeId": "fork", "portId": "branch.branch_2"}, "target": {"nodeId": "anchor_2", "portId": "execute"}, "kind": "execution"},
            "anchor-route": {"source": {"nodeId": "anchor_1", "portId": "then"}, "target": {"nodeId": "route", "portId": "execute"}, "kind": "execution"},
            "route-true": {"source": {"nodeId": "route", "portId": "true"}, "target": {"nodeId": "shared", "portId": "execute"}, "kind": "execution"},
            "route-false": {"source": {"nodeId": "route", "portId": "false"}, "target": {"nodeId": "shared", "portId": "execute"}, "kind": "execution"},
            "shared-join": {"source": {"nodeId": "shared", "portId": "out"}, "target": {"nodeId": "join", "portId": "branch.branch_1"}, "kind": "execution"},
            "anchor-join": {"source": {"nodeId": "anchor_2", "portId": "then"}, "target": {"nodeId": "join", "portId": "branch.branch_2"}, "kind": "execution"},
            "finish": {"source": {"nodeId": "join", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
        },
    }


def check(browser):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}")
            if response.status >= 400 and not response.url.endswith("/favicon.ico") else None)
    workflow_id = "branch-local-" + uuid.uuid4().hex
    response = page.request.put(BASE + f"/api/workflows/{workflow_id}/draft",
                                data={"definition": integer_graph(workflow_id), "expectedGeneration": 0})
    assert response.ok, response.text()

    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    designer.get_by_label("Number of parallel branches", exact=True).select_option("2")
    designer.get_by_role("button", name="Create parallel workflow", exact=True).click()
    designer.locator(".vaw-node-library button[data-node-key='core.branch.boolean']").click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    invalid = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert invalid["schemaVersion"] == "1.34"
    assert sum(node["type"] == "core.branch.boolean" for node in invalid["nodes"].values()) == 2
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_be_visible()
    expect(designer.get_by_role("button", name="Publish & activate", exact=True)).to_be_disabled()
    expect(designer.get_by_role("button", name="Run selected version", exact=True)).to_be_disabled()

    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    page.reload(wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    saved_invalid = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert saved_invalid["schemaVersion"] == "1.34"
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_be_visible()

    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(valid_conditional_region(workflow_id)))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_have_count(0)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    valid = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert valid["schemaVersion"] == "1.34"
    expect(designer.get_by_role("button", name="Publish & activate", exact=True)).to_be_enabled()
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    expect(designer).to_contain_text("Published and activated version 1.")
    expect(designer.get_by_role("button", name="Run selected version", exact=True)).to_be_enabled()
    assert not errors, errors
    print("Branch-local route creation upgraded to 1.34; invalid draft save/reload and publish/run gating passed; valid exclusive reconvergence published.")
    page.close()


if __name__ == "__main__":
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True, executable_path="/usr/bin/chromium")
        try:
            check(browser)
        finally:
            browser.close()
