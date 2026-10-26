"""Exercise parallel block authoring, publishing, concurrent run, and playback in Studio."""

import argparse
from playwright.sync_api import expect, sync_playwright
from workbench_browser import inspect_control, new_workflow_fixture, workbench


def check(browser, base_url):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(base_url)
    designer = workbench(page.get_by_role("region", name="Workflow designer", exact=True))
    new_workflow_fixture(designer)
    workflow_id = designer.locator(".vaw-definition-status strong").inner_text()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    wire = designer.locator('path[data-wire="link-1"]').first
    wire.click(button="right")
    actions = designer.get_by_role("dialog", name="Graph actions", exact=True)
    actions.get_by_role("button", name="Add Parallel block", exact=True).click()
    nodes = designer.locator("li[data-node-id]")
    expect(nodes).to_have_count(8)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    design_path = f"/api/workflows/{workflow_id}/design"
    definition = page.request.get(base_url + design_path).json()["draft"]["definition"]
    assert definition["schemaVersion"] == "1.14"
    fork = next(node_id for node_id, node in definition["nodes"].items() if node["type"] == "core.parallel.fork")
    branch_targets = {
        edge["source"]["portId"]: edge["target"]["nodeId"]
        for edge in definition["connections"].values()
        if edge["source"]["nodeId"] == fork
    }
    for port, node_type in (("branch_1", "core.text.uppercase"), ("branch_2", "core.text.reverse")):
        anchor = branch_targets[port]
        editor = inspect_control(designer, f"New node operation for {anchor}")
        editor.get_by_label("New node operation", exact=True).select_option(node_type)
        editor.get_by_role("button", name=f"Add node after {anchor}", exact=True).click()

    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    designer.get_by_text("Published and activated version 1.", exact=True).wait_for()

    designer.get_by_role("textbox", name="Published run input", exact=True).fill("Abc")
    designer.get_by_role("button", name="Run selected version", exact=True).click()
    history = page.locator(".vaw-history button[aria-pressed='true'] small")
    expect(history).to_be_visible(timeout=30_000)
    output = page.locator('li[data-node-id="output"]:visible')
    expect(output).to_be_visible(timeout=30_000)
    selected = page.locator(".vaw-history button[aria-pressed='true'] small").inner_text()
    summaries = page.request.get(base_url + "/api/runs").json()
    run_id = next(item["header"]["runId"] for item in summaries
                  if item["header"]["runId"].startswith(selected) and item["header"]["definition"]["workflowId"] == workflow_id)
    snapshot = page.request.get(base_url + f"/api/runs/{run_id}").json()
    event_kinds = [event["kind"] for event in snapshot["events"]]
    assert "parallel.join.progress" in event_kinds
    assert any(event["kind"] == "node.completed" and event["nodeId"] == "output" and "BRANCH_1" in event["data"] for event in snapshot["events"])
    join_id = next(node_id for node_id, node in definition["nodes"].items() if node["type"] == "core.parallel.join")
    waiting = next(event for event in snapshot["events"] if event["kind"] == "parallel.join.progress" and "waiting" in event["data"].lower())
    page.get_by_role("button", name="Run inspector", exact=True).click()
    viewer = page.locator(".vaw-viewer:visible")
    for _ in range(len(snapshot["events"]) - waiting["sequence"]):
        viewer.get_by_role("button", name="Previous event", exact=True).click()
    expect(viewer.get_by_role("button", name=f"{join_id} core.parallel.join Running", exact=True)).to_be_visible()
    expect(viewer.locator(f".vaw-node[data-node-id='{join_id}'] .vaw-node-activity")).to_contain_text("waiting for the other branch")
    page.get_by_role("button", name="Designer", exact=True).click()
    designer = workbench(page.get_by_role("region", name="Workflow designer", exact=True))
    fork_id = next(node_id for node_id, node in definition["nodes"].items() if node["type"] == "core.parallel.fork")
    designer.get_by_label("Node to organize", exact=True).select_option(fork_id)
    designer.get_by_role("button", name="Remove parallel block…", exact=True).click()
    designer.get_by_role("button", name="Remove parallel block and reconnect", exact=True).click()
    expect(designer.locator("li[data-node-id]")).to_have_count(3)
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(designer.locator("li[data-node-id]")).to_have_count(10)
    assert not errors, errors
    page.close()
    print("Parallel authoring, branch execution, join activity/playback, block removal, reconnect, and undo passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:5099")
    parser.add_argument("--browser-executable")
    args = parser.parse_args()
    options = {"headless": True}
    if args.browser_executable:
        options["executable_path"] = args.browser_executable
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(**options)
        try:
            check(browser, args.base_url.rstrip("/"))
        finally:
            browser.close()
