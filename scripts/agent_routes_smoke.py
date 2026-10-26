"""Exercise the Agent route editor and its rendered execution pins in Studio."""

import os
import uuid
from playwright.sync_api import sync_playwright, expect

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5100")


def check(browser, screenshots):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.on("console", lambda message: errors.append(f"{message.text} ({message.location.get('url', '')})")
            if message.type == "error" and not message.location.get("url", "").endswith("/favicon.ico") else None)
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}") if response.status >= 400 else None)
    workflow_id = "agent-routes-smoke-" + uuid.uuid4().hex
    legacy_workflow_id = "agent-routes-legacy-smoke-" + uuid.uuid4().hex
    definition = {
        "schemaVersion": "1.30",
        "workflowId": workflow_id,
        "displayName": "Agent route browser smoke",
        "entryNodeId": "input",
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "agent": {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
                "providerId": "demo.scripted", "instructions": "Choose a route.", "tools": [],
                "routes": [
                    {"id": "research", "label": "Research", "description": "Gather supporting evidence"},
                    {"id": "answer", "label": "Answer", "description": "Return the final response"},
                ],
            }},
            "output": {"type": "core.output", "typeVersion": "1.0.0"},
        },
        "connections": {
            "agent-result": {"source": {"nodeId": "agent", "portId": "out"},
                             "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}
        },
        "executionConnections": {
            "activate-agent": {"source": {"nodeId": "input", "portId": "then"},
                               "target": {"nodeId": "agent", "portId": "execute"}, "kind": "execution"},
            "use-answer": {"source": {"nodeId": "agent", "portId": "route.answer"},
                               "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
            "use-research": {"source": {"nodeId": "agent", "portId": "route.research"},
                               "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
        },
    }
    response = page.request.put(BASE + "/api/workflows/" + workflow_id + "/draft",
                                data={"definition": definition, "expectedGeneration": 0})
    assert response.ok, response.text()
    legacy_definition = {
        "schemaVersion": "1.29",
        "workflowId": legacy_workflow_id,
        "displayName": "Legacy linear route editor smoke",
        "entryNodeId": "input",
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "agent": {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
                "providerId": "demo.scripted", "instructions": "Answer the question.", "tools": [],
            }},
            "output": {"type": "core.output", "typeVersion": "1.0.0"},
        },
        "connections": {
            "input-agent": {"source": {"nodeId": "input", "portId": "out"},
                            "target": {"nodeId": "agent", "portId": "in"}, "kind": "data"},
            "agent-output": {"source": {"nodeId": "agent", "portId": "out"},
                             "target": {"nodeId": "output", "portId": "in"}, "kind": "data"},
        },
    }
    response = page.request.put(BASE + "/api/workflows/" + legacy_workflow_id + "/draft",
                                data={"definition": legacy_definition, "expectedGeneration": 0})
    assert response.ok, response.text()

    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(designer.locator(".vaw-graph-editor")).to_have_attribute("data-ready", "true")
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(designer.locator("li[data-node-id='agent']")).to_be_visible()
    designer.get_by_label("Node to organize", exact=True).select_option("agent")

    route_editor = designer.get_by_label("Agent routes for agent", exact=True)
    route_editor.fill("research | Evidence | Gather supporting evidence\nanswer | Final answer | Return the final response")
    route_editor.press("Tab")
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    expect(designer.locator("li[data-node-id='agent'] [data-socket='route.research']")).to_be_visible()
    expect(designer.locator("li[data-node-id='agent'] [data-socket='route.answer']")).to_be_visible()
    expect(designer.locator("li[data-node-id='agent'] .vaw-output-ports")).to_contain_text("Evidence")
    expect(designer.locator("li[data-node-id='agent'] .vaw-output-ports")).to_contain_text("Final answer")

    designer.get_by_role("button", name="Save draft", exact=True).click()
    saved = page.request.get(BASE + "/api/workflows/" + workflow_id)
    assert saved.ok, saved.text()
    saved_definition = saved.json()["definition"]
    assert saved_definition["schemaVersion"] == "1.30"
    assert [route["label"] for route in saved_definition["nodes"]["agent"]["agent"]["routes"]] == ["Evidence", "Final answer"]

    # Removing a route is allowed in a draft even while its output pin is unwired.
    route_editor.fill("research | Evidence | Gather supporting evidence")
    route_editor.press("Tab")
    expect(designer.locator(".vaw-graph-issues")).to_contain_text("executionConnections.use-answer")
    expect(designer.locator("li[data-node-id='agent'] [data-socket='route.answer']")).to_have_count(0)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    incomplete = page.request.get(BASE + "/api/workflows/" + workflow_id)
    assert incomplete.ok, incomplete.text()
    assert not errors, errors

    if screenshots:
        page.screenshot(path=os.path.join(screenshots, "agent-routes-editor.png"), full_page=True)

    designer.get_by_label("Saved workflow", exact=True).select_option(legacy_workflow_id)
    designer.get_by_label("Node to organize", exact=True).select_option("agent")
    expect(designer.get_by_label("Agent routes for agent", exact=True)).to_have_count(0)
    expect(designer).to_contain_text("Agent-selected routes require explicit execution wires")
    assert not errors, errors
    print("Agent route editor rendered stable output pins, saved an invalid draft with visible validation, and explained the explicit-graph requirement on legacy workflows.")
    page.close()


def main():
    import argparse
    from pathlib import Path
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--screenshots", default="/tmp/vaw-agent-routes-screenshots")
    args = parser.parse_args()
    Path(args.screenshots).mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True, executable_path="/usr/bin/chromium")
        try:
            check(browser, args.screenshots)
        finally:
            browser.close()


if __name__ == "__main__":
    main()
