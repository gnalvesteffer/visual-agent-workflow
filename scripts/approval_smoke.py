"""Exercise Agent -> Condition -> Approval -> MCP -> Output through real Studio controls.

Uses only scripted agents and the local read-only MCP fixture. No live model or
external side effect is performed. Creates disposable local workflows/runs.
"""

from workbench_browser import workbench, inspect_control, new_workflow_fixture, show_runs
import argparse
import json
from pathlib import Path
import time
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
    def post(path, body=None, status=202):
        response = page.request.post(BASE + path, data=body)
        assert response.status == status, (path, response.status, response.text())
        return response.json() if status < 400 and response.text() else None
    def pending(run):
        return next(json.loads(e["data"]) for e in reversed(run["events"]) if e["kind"] == "approval.requested")
    def selected_id():
        header = viewer.locator(".vaw-run-header small").inner_text()
        return header.split(" · revision ")[0].removeprefix("Run ").strip()
    def wait_status(run_id, status):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            snapshot = get("/api/runs/" + run_id)
            if snapshot["status"] == status:
                return snapshot
            time.sleep(0.025)
        raise AssertionError((run_id, "Expected " + status, snapshot["status"]))

    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="New workflow", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
    workflow_id = workbench(designer).locator(".vaw-definition-status strong").inner_text()
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option("agent:demo.scripted:1.0.0")
    inspect_control(designer, "Add conditional after step-1").get_by_role("button", name="Add conditional after step-1", exact=True).click()
    branch = workbench(designer).locator(".vaw-node[data-node-type='core.condition']").get_attribute("data-node-id")
    operand = inspect_control(designer, "Condition operand for " + branch).get_by_label("Condition operand for " + branch, exact=True)
    operand.fill("allow"); operand.press("Tab")
    # Resolve the true arm from the actual source-port connection in the staged definition export.
    details = workbench(designer).get_by_text("Import / export definition", exact=True)
    details.click()
    workbench(designer).get_by_role("button", name="Copy definition into JSON editor", exact=True).click()
    expect(workbench(designer).get_by_role("textbox", name="Workflow JSON", exact=True)).not_to_have_value("")
    definition = json.loads(workbench(designer).get_by_role("textbox", name="Workflow JSON", exact=True).input_value())
    true_id = next(e["target"]["nodeId"] for e in definition["connections"].values() if e["source"] == {"nodeId":branch,"portId":"true"})
    descriptor = next(b for b in get("/api/tool-bindings") if b["bindingId"] == "demo.reverse")
    inspect_control(designer, "Operation for " + true_id).get_by_label("Operation for " + true_id, exact=True).select_option("mcp:demo.reverse:" + descriptor["contractHash"])
    inspect_control(designer, "Require approval before " + true_id).get_by_role("button", name="Require approval before " + true_id, exact=True).click()
    approval_node = workbench(designer).locator(".vaw-node[data-node-type='core.approval']").get_attribute("data-node-id")
    prompt = inspect_control(designer, "Approval prompt for " + approval_node).get_by_label("Approval prompt for " + approval_node, exact=True)
    prompt.fill("Review this exact reverse call <script>review</script>"); prompt.press("Tab")
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(prompt).to_have_value("Review the exact MCP call before allowing it.")
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(prompt).to_have_value("Review this exact reverse call <script>review</script>")
    workbench(designer).get_by_role("button", name="Arrange nodes", exact=True).click()
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    saved = get(f"/api/workflows/{workflow_id}/design")
    assert saved["draft"]["definition"]["schemaVersion"] == "1.5"
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    revision = get(f"/api/workflows/{workflow_id}/revisions")[0]["revision"]
    workbench(designer).get_by_label("Published run input", exact=True).fill("allow abc")
    workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    review = viewer.get_by_role("region", name="Recorded action approval", exact=True)
    expect(review.get_by_role("button", name="Approve recorded action", exact=True)).to_be_enabled()
    first_id = selected_id()
    first = wait_status(first_id, "waiting-approval")
    request = pending(first)
    assert request["action"]["input"] == "Demo agent: allow abc"
    assert request["action"]["targetNodeId"] == true_id
    assert request["action"]["revisionId"] == revision["revisionId"]
    assert request["action"]["tool"] == {"bindingId":"demo.reverse","contractHash":descriptor["contractHash"]}
    assert not any(e["kind"].startswith("tool.") for e in first["events"])
    # Another run progresses while the approval is parked.
    other = post("/api/runs", {"definition":get("/api/templates/default"),"input":"other"})
    wait_status(other["header"]["runId"], "completed")
    decision_path = f"/api/runs/{first_id}/approvals/{request['requestId']}/decision"
    post(decision_path, {"actionHash":"0"*64,"approve":True}, status=409)
    post(decision_path, {"actionHash":request["actionHash"],"approve":True,"actor":"forged"}, status=400)
    denied = page.request.post(BASE + decision_path, data={"actionHash":request["actionHash"],"approve":True}, headers={"Origin":"https://untrusted.example"})
    assert denied.status == 403
    post(f"/api/runs/{first_id}/resume")
    assert get("/api/runs/" + first_id)["status"] == "waiting-approval"
    # Reload is observation lifetime only. The same pending identity remains available.
    page.reload()
    show_runs(page)
    expect(page.get_by_role("button", name="Start independent run", exact=True)).to_be_enabled()
    page.locator(".vaw-history button").filter(has_text=first_id[:8]).click()
    expect(review.get_by_role("button", name="Approve recorded action", exact=True)).to_be_enabled()
    assert pending(get("/api/runs/" + first_id))["requestId"] == request["requestId"]
    expect(review).to_contain_text("<script>review</script>")
    assert review.locator("script").count() == 0
    if screenshots:
        screenshots.mkdir(parents=True, exist_ok=True)
        viewer.screenshot(path=str(screenshots / "pending-approval.png"))
    review.get_by_role("button", name="Approve recorded action", exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("cba wolla :tnega omeD")
    finished = wait_status(first_id, "completed")
    tool = next(e for e in finished["events"] if e["kind"] == "tool.authorized")
    assert tool["invocationId"] == request["action"]["targetInvocationId"]
    assert sum(e["kind"] == "tool.completed" for e in finished["events"]) == 1
    assert sum(e["kind"] == "approval.approved" for e in finished["events"]) == 1
    post(decision_path, {"actionHash":request["actionHash"],"approve":True})
    assert get("/api/runs/" + first_id) == finished
    post(decision_path, {"actionHash":request["actionHash"],"approve":False}, status=409)
    sequence = next(e["sequence"] for e in finished["events"] if e["kind"] == "approval.requested")
    slider = viewer.get_by_label("Recorded position", exact=True)
    slider.evaluate("""(input,position)=>{input.value=position;input.dispatchEvent(new Event('input',{bubbles:true}));}""", sequence)
    expect(review).to_be_visible()
    expect(review.get_by_role("button", name="Approve recorded action", exact=True)).to_have_count(0)
    assert get("/api/runs/" + first_id) == finished
    # Rejection is terminal and does not dispatch the guarded call.
    rejected = post(f"/api/revisions/{revision['revisionId']}/runs", {"input":"allow reject"})
    reject_id = rejected["header"]["runId"]
    waiting = wait_status(reject_id, "waiting-approval")
    reject_request = pending(waiting)
    page.get_by_role("button", name="Refresh history", exact=True).click()
    page.locator(".vaw-history button").filter(has_text=reject_id[:8]).click()
    review.get_by_role("button", name="Reject recorded action", exact=True).click()
    failed = wait_status(reject_id, "failed")
    assert not any(e["kind"].startswith("tool.") for e in failed["events"])
    assert any(e["kind"] == "approval.rejected" for e in failed["events"])
    # The other branch must skip approval entirely.
    skipped = post(f"/api/revisions/{revision['revisionId']}/runs", {"input":"abc"})
    done = wait_status(skipped["header"]["runId"], "completed")
    assert done["events"][-1]["data"] == "cba :tnega omeD"
    assert not any(e["kind"].startswith("approval.") for e in done["events"])
    # Cancellation invalidates a pending request.
    cancelled = post(f"/api/revisions/{revision['revisionId']}/runs", {"input":"allow cancel"})
    cancel_id = cancelled["header"]["runId"]
    pending_cancel = pending(wait_status(cancel_id, "waiting-approval"))
    response = page.request.post(BASE + f"/api/runs/{cancel_id}/cancel")
    assert response.status == 204
    wait_status(cancel_id, "cancelled")
    post(f"/api/runs/{cancel_id}/approvals/{pending_cancel['requestId']}/decision", {"actionHash":pending_cancel["actionHash"],"approve":True}, status=409)
    assert not errors, errors
    page.close()
    print("Agent/condition/approval/MCP authoring, exact-action binding, browser reload, other-run progress, approve/reject/cancel, idempotent retry, actor/origin rejection, skipped arm and read-only playback passed.")


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
