"""Functional smoke check against an already running local development studio.

Uses only the standard library unless --browser is selected. Creates workflows,
publications, audit entries, and recorded runs; use disposable local data.
"""

from workbench_browser import workbench, inspect_control, new_workflow_fixture, show_runs

import argparse
import json
import time
import uuid
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5099"


def request(path, body=None, headers=None, method=None):
    return urllib.request.urlopen(
        urllib.request.Request(
            BASE + path,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Content-Type": "application/json", **(headers or {})},
            method=method,
        ),
        timeout=10,
    )


def get(path):
    with request(path) as response:
        return json.load(response)


def expect_denial(path, body, headers, status, method=None):
    try:
        with request(path, body, headers, method):
            raise AssertionError(f"Expected HTTP {status}")
    except urllib.error.HTTPError as error:
        assert error.code == status, error.code


def api_check():
    deadline = time.monotonic() + 15
    while True:
        try:
            definition = get("/api/templates/default")
            break
        except urllib.error.URLError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.1)
    with request("/api/runs", {"definition": definition, "input": "functional smoke"}) as response:
        assert response.status == 202
        started = json.load(response)
    run_id = started["header"]["runId"]
    deadline = time.monotonic() + 10
    while True:
        recorded = get("/api/runs/" + run_id)
        if recorded["isTerminal"]:
            break
        assert time.monotonic() < deadline, "Run did not finish"
        time.sleep(0.05)
    assert recorded["status"] == "completed"
    assert recorded["events"][-1]["data"] == "EKOMS LANOITCNUF"
    assert len(get(f"/api/runs/{run_id}/events?after=3")) == len(recorded["events"]) - 3
    expect_denial("/api/runs", {"definition": definition, "input": "x"}, {"Origin": "https://untrusted.example"}, 403)
    expect_denial("/api/runs", None, {"Host": "untrusted.example"}, 403)
    expect_denial("/api/runs", None, {"Origin": "https://untrusted.example", "Upgrade": "websocket"}, 403)
    invalid = {**definition, "schemaVersion": "unsupported"}
    expect_denial("/api/runs", {"definition": invalid, "input": "x"}, None, 400)
    print(f"API execution, output, event cursor, validation, and local access checks passed: {run_id}")


def catalog_check():
    definition = get("/api/templates/default")
    workflow_id = "smoke-" + uuid.uuid4().hex
    definition["workflowId"] = workflow_id
    path = "/api/workflows/" + workflow_id

    def mutate(suffix, body, method="POST"):
        with request(path + suffix, body, method=method) as response:
            return json.load(response)

    draft = mutate("/draft", {"definition": definition, "expectedGeneration": 0}, "PUT")
    expect_denial(path + "/draft", {"definition": definition, "expectedGeneration": 0}, None, 409, "PUT")
    expect_denial(path + "/runs", {"input": "abc"}, None, 409)
    first = mutate("/publish", {"expectedGeneration": draft["generation"]})
    first_id = first["revision"]["revisionId"]
    draft = get(path)
    definition["nodes"]["step-2"]["type"] = "core.text.uppercase"
    draft = mutate("/draft", {"definition": definition, "expectedGeneration": draft["generation"]}, "PUT")
    second = mutate("/publish", {"expectedGeneration": draft["generation"]})
    draft = get(path)
    draft = mutate("/restore", {"revisionId": first_id, "expectedGeneration": draft["generation"]})
    assert draft["activeRevisionId"] == second["revision"]["revisionId"]
    third = mutate("/publish", {"expectedGeneration": draft["generation"]})
    assert third["version"] == 3
    assert third["revision"]["contentHash"] == first["revision"]["contentHash"]
    assert third["revision"]["parentRevisionId"] == second["revision"]["revisionId"]
    draft = get(path)
    mutate("/activate", {"revisionId": first_id, "expectedGeneration": draft["generation"]})
    run = mutate("/runs", {"input": "abc"})
    deadline = time.monotonic() + 10
    while True:
        recorded = get("/api/runs/" + run["header"]["runId"])
        if recorded["isTerminal"]:
            break
        assert time.monotonic() < deadline
        time.sleep(0.05)
    assert recorded["status"] == "completed" and recorded["events"][-1]["data"] == "CBA"
    assert recorded["header"]["revisionId"] == first_id
    assert [r["version"] for r in get(path + "/revisions")] == [3, 2, 1]
    audit = get(path + "/audit")
    assert len(audit) == 7 and audit[-1]["kind"] == "revision.activated"
    assert all(entry["actor"] == "local-developer" for entry in audit)
    print("Catalog save/conflict, publish, restore, activate, pinned execution, and audit checks passed.")


def mcp_check():
    bindings = get("/api/tool-bindings")
    reverse = next(binding for binding in bindings if binding["bindingId"] == "demo.reverse")
    definition = get("/api/templates/default")
    definition["schemaVersion"] = "1.1"
    definition["nodes"]["step-1"] = {"type": "core.mcp-tool", "typeVersion": "1.0.0", "tool": {
        "bindingId": reverse["bindingId"], "contractHash": reverse["contractHash"]}}
    definition["nodes"]["step-2"]["type"] = "core.text.uppercase"

    def execute(candidate):
        with request("/api/runs", {"definition": candidate, "input": "abc"}) as response:
            started = json.load(response)
        deadline = time.monotonic() + 15
        while True:
            snapshot = get("/api/runs/" + started["header"]["runId"])
            if snapshot["isTerminal"]:
                return snapshot
            assert time.monotonic() < deadline
            time.sleep(0.05)

    result = execute(definition)
    assert result["status"] == "completed" and result["events"][-1]["data"] == "CBA"
    tool_events = [event for event in result["events"] if event["kind"].startswith("tool.")]
    assert [event["kind"] for event in tool_events] == ["tool.requested", "tool.authorized", "tool.completed"]
    assert all(event["nodeId"] == "step-1" and event["invocationId"] for event in tool_events)
    definition["nodes"]["step-1"]["tool"]["bindingId"] = "not-granted"
    denied = execute(definition)
    assert denied["status"] == "failed"
    assert any(event["kind"] == "tool.denied" for event in denied["events"])
    assert not any(event["kind"] == "tool.authorized" for event in denied["events"])
    definition["nodes"]["step-1"]["tool"] = {"bindingId": reverse["bindingId"], "contractHash": "0" * 64}
    drifted = execute(definition)
    assert drifted["status"] == "failed"
    assert not any(event["kind"] == "tool.authorized" for event in drifted["events"])
    print("Actual MCP pipeline execution, correlated recording, denied binding, and contract-drift checks passed.")


def agent_check():
    provider = next(p for p in get("/api/agent-providers") if p["providerId"] == "demo.scripted")
    assert provider["isDeterministic"] is True
    reverse = next(b for b in get("/api/tool-bindings") if b["bindingId"] == "demo.reverse")
    definition = get("/api/templates/default")
    definition["schemaVersion"] = "1.2"
    definition["nodes"]["step-1"] = {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
        "providerId": provider["providerId"], "providerVersion": provider["version"],
        "instructions": "Use only the assigned tool.",
        "tools": [{"bindingId": reverse["bindingId"], "contractHash": reverse["contractHash"]}]}}
    definition["nodes"]["step-2"]["type"] = "core.text.uppercase"

    def execute():
        with request("/api/runs", {"definition": definition, "input": "abc"}) as response:
            started = json.load(response)
        deadline = time.monotonic() + 15
        while True:
            snapshot = get("/api/runs/" + started["header"]["runId"])
            if snapshot["isTerminal"]:
                return snapshot
            assert time.monotonic() < deadline
            time.sleep(0.05)

    result = execute()
    assert result["status"] == "completed" and result["events"][-1]["data"] == "CBA"
    assert sum(e["kind"] == "model.requested" for e in result["events"]) == 2
    assert sum(e["kind"] == "model.responded" for e in result["events"]) == 2
    assert any(e["kind"] == "agent.completed" for e in result["events"])
    definition["nodes"]["step-1"]["agent"]["tools"][0]["bindingId"] = "not-granted"
    denied = execute()
    assert denied["status"] == "failed" and any(e["kind"] == "tool.denied" for e in denied["events"])
    assert any(e["kind"] == "node.failed" for e in denied["events"])
    assert not any(e["kind"] == "model.requested" for e in denied["events"])
    definition["nodes"]["step-1"]["agent"]["tools"] = []
    plain = execute()
    assert plain["status"] == "completed" and plain["events"][-1]["data"] == "DEMO AGENT: ABC"
    assert not any(e["kind"] == "tool.requested" for e in plain["events"])
    print("Framework agent/tool loop, recorded model rounds, denied tool preflight, and no-tool isolation passed (deterministic fixture).")


def browser_check(executable):
    from playwright.sync_api import expect, sync_playwright

    with sync_playwright() as playwright:
        options = {"headless": True}
        if executable:
            options["executable_path"] = executable
        browser = playwright.chromium.launch(**options)
        try:
            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            errors = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            page.goto(BASE)
            show_runs(page)
            start = page.get_by_role("button", name="Start independent run", exact=True)
            expect(start).to_be_enabled()
            page.get_by_role("textbox", name="Input", exact=True).fill("browser proof")
            start.click()
            page.get_by_role("button", name="output core.output Completed", exact=True).click()
            inspector = page.get_by_role("region", name="Selected node payloads")
            expect(inspector.locator("pre").last).to_have_text("FOORP RESWORB")
            page.get_by_role("button", name="Previous event", exact=True).click()
            page.get_by_text("Recorded playback", exact=True).wait_for()
            page.reload()
            show_runs(page)
            expect(page.get_by_role("button", name="Start independent run", exact=True)).to_be_enabled()
            page.set_viewport_size({"width": 390, "height": 844})
            assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Mobile horizontal overflow"
            assert not errors, errors
            print("Browser run controls, output inspector, playback, reload, and mobile overflow checks passed.")
            designer_check(browser)
            mcp_designer_check(browser)
            agent_designer_check(browser)
        finally:
            browser.close()


def designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    workflow_id = workbench(designer).get_by_label("Saved workflow", exact=True).input_value()
    nodes = workbench(designer).locator('ol[aria-label="Editable pipeline nodes"] > li')
    expect(nodes).to_have_count(3)
    inspect_control(designer, "New node operation").get_by_label("New node operation", exact=True).select_option("core.text.reverse")
    inspect_control(designer, "Add node after step-1").get_by_role("button", name="Add node after step-1", exact=True).click()
    expect(nodes).to_have_count(4)
    inserted_id = nodes.nth(2).get_attribute("data-node-id")
    inspect_control(designer, f"Move {inserted_id} up").get_by_role("button", name=f"Move {inserted_id} up", exact=True).click()
    expect(nodes.nth(1)).to_have_attribute("data-node-id", inserted_id)
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(nodes.nth(2)).to_have_attribute("data-node-id", inserted_id)
    inspect_control(designer, f"Remove {inserted_id}").get_by_role("button", name=f"Remove {inserted_id}", exact=True).click()
    expect(nodes).to_have_count(3)
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(nodes).to_have_count(4)
    workbench(designer).get_by_role("button", name="Redo edit", exact=True).click()
    expect(nodes).to_have_count(3)
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    inspect_control(designer, "Add node after step-1").get_by_role("button", name="Add node after step-1", exact=True).click()
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 2.", exact=True).wait_for()
    workbench(designer).get_by_label("Published run input", exact=True).fill("abc")
    workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
    page.get_by_role("button", name="output core.output Completed", exact=True).click()
    expect(page.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("CBA")
    version_select = workbench(designer).get_by_label("Published version", exact=True)
    version_one = version_select.locator("option").evaluate_all("options => options.find(o => o.textContent.startsWith('Version 1 ')).value")
    version_select.select_option(version_one)
    workbench(designer).get_by_role("button", name="View selected version", exact=True).click()
    workbench(designer).get_by_text("Viewing published version 1 (read-only).", exact=True).wait_for()
    expect(nodes).to_have_count(3)
    # Read-only published revisions omit insertion controls and their disclosure entirely.
    expect(designer.get_by_role("button", name="Add node after input", exact=True)).to_have_count(0)
    workbench(designer).get_by_role("button", name="Return to draft", exact=True).click()
    expect(nodes).to_have_count(4)
    workbench(designer).get_by_role("button", name="Restore selected to draft", exact=True).click()
    expect(nodes).to_have_count(3)
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 3.", exact=True).wait_for()

    other = browser.new_page()
    other.on("pageerror", lambda error: errors.append(str(error)))
    other.goto(BASE)
    second = other.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(second).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    workbench(second).get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(workbench(second).get_by_label("Published version", exact=True).locator("option")).to_have_count(3)
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option("core.text.reverse")
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for()
    inspect_control(second, "Add node after input").get_by_role("button", name="Add node after input", exact=True).click()
    workbench(second).get_by_role("button", name="Save draft", exact=True).click()
    expect(workbench(second).get_by_role("alert")).to_contain_text("Your local edits are preserved")
    expect(workbench(second).locator('ol[aria-label="Editable pipeline nodes"] > li')).to_have_count(4)
    workbench(second).get_by_role("button", name="Discard local changes and reload", exact=True).click()
    expect(workbench(second).locator('ol[aria-label="Editable pipeline nodes"] > li')).to_have_count(3)
    expect(inspect_control(second, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.reverse")
    other.close()

    workbench(designer).get_by_text("Import / export definition", exact=True).click()
    workbench(designer).get_by_role("button", name="Copy definition into JSON editor", exact=True).click()
    expect(workbench(designer).get_by_label("Workflow JSON", exact=True)).not_to_have_value("")
    exported = json.loads(workbench(designer).get_by_label("Workflow JSON", exact=True).input_value())
    assert exported["workflowId"] == workflow_id
    exported["workflowId"] = "unrelated-import-id"
    exported["nodes"]["step-1"]["type"] = "core.text.uppercase"
    workbench(designer).get_by_label("Workflow JSON", exact=True).fill(json.dumps(exported))
    workbench(designer).get_by_role("button", name="Import JSON into draft", exact=True).click()
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.uppercase")
    assert workbench(designer).get_by_label("Saved workflow", exact=True).input_value() == workflow_id
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.reverse")
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Designer mobile overflow"
    assert not errors, errors
    page.close()
    print("Browser graph edits/undo, publication, pinned run, version preview/restore, cross-tab conflicts, and JSON import passed.")


def mcp_designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    workflow_id = workbench(designer).get_by_label("Saved workflow", exact=True).input_value()
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option(label="MCP · reverse")
    workbench(designer).get_by_text("MCP binding: demo.reverse", exact=False).wait_for()
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    workbench(designer).get_by_label("Published run input", exact=True).fill("abc")
    workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
    page.get_by_role("button", name="output core.output Completed", exact=True).click()
    expect(page.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("cba")
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_text("Recorded events at this position", exact=False).click()
    viewer.get_by_role("cell", name="tool.completed", exact=True).wait_for()
    assert get("/api/workflows/" + workflow_id)["definition"]["schemaVersion"] == "1.1"
    page.reload()
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    workbench(designer).get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value(
        "mcp:demo.reverse:" + get("/api/workflows/" + workflow_id)["definition"]["nodes"]["step-1"]["tool"]["contractHash"])
    page.close()
    print("Browser MCP binding selection, published execution, recorded tool result, and reload passed.")


def agent_designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page.goto(BASE)
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    workflow_id = workbench(designer).get_by_label("Saved workflow", exact=True).input_value()
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option(label="Deterministic demo agent")
    inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1", exact=True).fill("Use only assigned tools; return their output.")
    inspect_control(designer, "Assign demo.reverse to step-1").get_by_role("checkbox", name="Assign demo.reverse to step-1", exact=True).check()
    workbench(designer).get_by_role("button", name="Save draft", exact=True).click()
    workbench(designer).get_by_role("button", name="Publish & activate", exact=True).click()
    workbench(designer).get_by_text("Published and activated version 1.", exact=True).wait_for()
    workbench(designer).get_by_label("Published run input", exact=True).fill("abc")
    workbench(designer).get_by_role("button", name="Run selected version", exact=True).click()
    page.get_by_role("button", name="output core.output Completed", exact=True).click()
    expect(page.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("cba")
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_text("Recorded events at this position", exact=False).click()
    expect(viewer.get_by_role("cell", name="model.responded", exact=True)).to_have_count(2)
    page.reload()
    expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
    workbench(designer).get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(inspect_control(designer, "Assign demo.reverse to step-1").get_by_role("checkbox", name="Assign demo.reverse to step-1", exact=True)).to_be_checked()
    expect(inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1", exact=True)).to_have_value("Use only assigned tools; return their output.")
    assert get("/api/workflows/" + workflow_id)["definition"]["schemaVersion"] == "1.2"
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Agent editor mobile overflow"
    page.close()
    print("Browser agent instructions, node tool assignment, published execution, model evidence, and reload passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser", action="store_true", help="Also exercise real browser controls; requires Playwright and Chromium")
    parser.add_argument("--browser-executable", help="Optional path to an existing Chromium executable")
    arguments = parser.parse_args()
    api_check()
    catalog_check()
    mcp_check()
    agent_check()
    if arguments.browser:
        browser_check(arguments.browser_executable)
