"""Exercise bounded-loop authoring, real framework execution, and playback in Studio."""

import argparse
import json
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

    designer.get_by_role("button", name="Add Bounded loop", exact=True).click()
    nodes = designer.locator("li[data-node-id]")
    expect(nodes).to_have_count(5)
    loop = designer.locator("li[data-node-type='core.loop']")
    anchor = designer.locator("li[data-node-type='core.loop.body']")
    loop_id = loop.get_attribute("data-node-id")
    anchor_id = anchor.get_attribute("data-node-id")
    expect(loop.locator('[data-socket="body"]')).to_have_count(1)
    expect(loop.locator('[data-socket="done"]')).to_have_count(1)

    inspect_control(designer, f"Maximum loop iterations for {loop_id}").get_by_label(f"Maximum loop iterations for {loop_id}", exact=True).fill("2")
    operation = inspect_control(designer, f"New node operation for {anchor_id}")
    operation.get_by_label("New node operation", exact=True).select_option("core.text.uppercase")
    operation.get_by_role("button", name=f"Add node after {anchor_id}", exact=True).click()
    expect(nodes).to_have_count(6)

    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    definition = page.request.get(base_url + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert definition["schemaVersion"] == "1.17"
    assert definition["nodes"][loop_id]["loop"]["maximumIterations"] == 2
    assert definition["nodes"][loop_id]["loop"]["continueWhile"] == {"operator": "contains", "operand": "continue"}
    body_edge = next(edge for edge in definition["connections"].values()
                     if edge["source"] == {"nodeId": loop_id, "portId": "body"})
    assert body_edge["target"]["nodeId"] == anchor_id

    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    designer.get_by_text("Published and activated version 1.", exact=True).wait_for()
    designer.get_by_label("Published run input", exact=True).fill("continue")
    designer.get_by_role("button", name="Run selected version", exact=True).click()
    output = page.locator('li[data-node-id="output"]')
    expect(output).to_be_visible(timeout=30_000)
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    selected = page.locator(".vaw-history button[aria-pressed='true'] small").inner_text()
    summary = next(item for item in page.request.get(base_url + "/api/runs").json()
                   if item["header"]["runId"].startswith(selected)
                   and item["header"]["definition"]["workflowId"] == workflow_id)
    run_id = summary["header"]["runId"]
    snapshot = page.request.get(base_url + f"/api/runs/{run_id}").json()
    events = snapshot["events"]
    assert sum(event["kind"] == "loop.iteration.started" for event in events) == 1
    assert sum(event["kind"] == "loop.iteration.completed" for event in events) == 1
    stop = next(event for event in events if event["kind"] == "loop.stopped")
    assert json.loads(stop["data"])["reason"] == "predicate-false"
    assert events[-1]["kind"] == "run.completed"
    first_iteration = next(event for event in events if event["kind"] == "loop.iteration.started")
    slider = page.get_by_label("Recorded position", exact=True)
    slider.evaluate(
        "(input, position) => { input.value = position; input.dispatchEvent(new Event('input', { bubbles: true })); }",
        first_iteration["sequence"],
    )
    expect(page.locator(f".vaw-node[data-node-id='{loop_id}'] .vaw-node-activity")).to_contain_text("Iteration 1 of 2")
    viewer.get_by_role("button", name="Follow latest", exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("CONTINUE")

    page.get_by_role("button", name="Designer", exact=True).click()
    designer.get_by_role("button", name=f"Move node {loop_id}", exact=True).click()
    removal = designer.get_by_role("button", name=f"Remove bounded loop {loop_id}", exact=True)
    expect(removal).to_be_visible()
    removal.click()
    expect(nodes).to_have_count(4)
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(nodes).to_have_count(6)
    assert not errors, errors
    page.close()
    print("Bounded loop configuration, Agent Framework feedback execution, predicate exit, recorded iteration evidence, loop removal, and undo passed.")


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
