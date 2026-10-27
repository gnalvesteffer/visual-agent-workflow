"""Functional smoke check against an already running local development studio.

Uses only the standard library unless --browser is selected. Creates workflows,
publications, audit entries, and recorded runs; use disposable local data.
"""

from workbench_browser import current_project, workbench, inspect_control, new_workflow_fixture, show_runs

import argparse
import json
import os
import re
import time
import uuid
import urllib.error
import urllib.request

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


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
    print("Actual MCP workflow execution, correlated recording, denied binding, and contract-drift checks passed.")


def agent_check():
    provider = next(p for p in get("/api/agent-providers") if p["providerId"] == "demo.scripted")
    assert provider["isDeterministic"] is True
    reverse = next(b for b in get("/api/tool-bindings") if b["bindingId"] == "demo.reverse")
    definition = get("/api/templates/default")
    definition["schemaVersion"] = "1.2"
    definition["nodes"]["step-1"] = {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
        "providerId": provider["providerId"],
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


def run_saved_workflow(page, designer, input_text, expected):
    from playwright.sync_api import expect
    designer.get_by_role("button", name="Run workflow", exact=True).click()
    # Native modal dialogs are exposed at the document level; the app's designer
    # region is not their DOM ancestor after the modal is opened.
    dialog = designer.page.locator(".vaw-run-dialog")
    dialog.get_by_label("Workflow input", exact=True).fill(input_text)
    run = dialog.get_by_role("button", name="Run workflow", exact=True)
    expect(run).to_be_enabled()
    run.click()
    expect(page.get_by_role("region", name="Workflow run viewer", exact=True)).to_be_visible()
    page.get_by_role("button", name="Designer", exact=True).click()
    history = designer.get_by_label("Workflow run history", exact=True)
    expect(history.locator('tr[aria-selected="true"]')).to_contain_text("completed", timeout=20000)
    designer.get_by_role("button", name="Output", exact=True).click()
    output = designer.get_by_role("region", name="Workflow output", exact=True)
    expect(output).to_be_visible()
    expect(output.locator(".vaw-workflow-result")).to_have_text(expected)


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
            page.goto(BASE, wait_until="networkidle")
            designer = page.get_by_role("region", name="Workflow designer", exact=True)
            new_workflow_fixture(designer)
            designer.locator("[data-graph-save]").click()
            expect(designer.get_by_role("button", name="Run workflow", exact=True)).to_be_enabled()
            run_saved_workflow(page, designer, "browser proof", "BROWSER PROOF")
            designer.get_by_label("Workflow run history", exact=True).get_by_role("button", name="Inspect run", exact=False).last.click()
            viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
            viewer.get_by_role("button", name="Fit graph", exact=True).click()
            viewer.get_by_role("button", name="Inspect node output", exact=True).click()
            inspector = page.get_by_role("region", name="Selected node payloads")
            expect(inspector.locator("pre").last).to_have_text("BROWSER PROOF")
            page.get_by_role("button", name="Previous event", exact=True).click()
            page.get_by_text("Recorded playback", exact=True).wait_for()
            page.reload()
            expect(page.get_by_role("button", name="Run workflow", exact=True)).to_be_enabled()
            page.set_viewport_size({"width": 390, "height": 844})
            assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Mobile horizontal overflow"
            assert not errors, errors
            print("Browser run controls, output inspector, playback, reload, and mobile overflow checks passed.")
            project_workspace_check(browser)
            designer_check(browser)
            mcp_designer_check(browser)
            agent_designer_check(browser)
        finally:
            browser.close()


def project_workspace_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page_name = "Smoke project " + uuid.uuid4().hex[:8]
    page.goto(BASE + "/projects", wait_until="networkidle")
    page.get_by_label("Project name", exact=True).fill(page_name)
    page.get_by_role("button", name="Create project", exact=True).click()
    expect(page.get_by_text(f"Created {page_name}.", exact=True)).to_be_visible()

    project_card = page.locator("article.vaw-provider-card").filter(has_text=page_name)
    project_url = project_card.get_by_role("link", name="Open project", exact=True).get_attribute("href")
    project_card.get_by_role("link", name="Open project", exact=True).click()
    page.wait_for_url("**" + project_url)
    expect(current_project(page)).to_have_text(page_name)

    page.goto(BASE + project_url + "/skills", wait_until="networkidle")
    page.get_by_label("Skill ID", exact=True).fill("smoke-project-skill")
    page.get_by_label("Version", exact=True).fill("1.0.0")
    page.get_by_label("Name", exact=True).fill("Project-only skill")
    page.get_by_label("Instructions", exact=True).fill("This skill belongs to one local project.")
    page.get_by_role("button", name="Save skill version", exact=True).click()
    expect(page.get_by_text("Skill revision saved.", exact=True)).to_be_visible()

    page.goto(BASE + "/projects/default/skills", wait_until="networkidle")
    expect(page.locator("main")).not_to_contain_text("Project-only skill")
    page.close()
    print("Browser project creation, selection, and project-scoped skill isolation passed.")


def designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for(state="attached")
    workflow_id = workbench(designer).locator("[data-vaw-workflow-selector]").input_value()
    nodes = workbench(designer).locator('ol[aria-label="Editable workflow nodes"] > li')
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
    workbench(designer).get_by_role("button", name="Save", exact=True).click()
    run_saved_workflow(page, designer, "abc", "ABC")

    other = browser.new_page(viewport={"width": 1280, "height": 1000})
    other.on("pageerror", lambda error: errors.append(str(error)))
    other.goto(BASE, wait_until="networkidle")
    second = other.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(second).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    expect(workbench(second).locator("[data-vaw-workflow-selector]")).to_be_enabled()
    other.goto(BASE + "/projects/default?workflow=" + workflow_id, wait_until="networkidle")
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option("core.text.reverse")
    workbench(designer).get_by_role("button", name="Save", exact=True).click()
    workbench(designer).get_by_text("Draft saved.", exact=True).wait_for(state="attached")
    inspect_control(second, "Add node after input").get_by_role("button", name="Add node after input", exact=True).click()
    workbench(second).get_by_role("button", name="Save", exact=True).click()
    expect(workbench(second).get_by_role("alert")).to_contain_text("Your local edits are preserved")
    expect(workbench(second).locator('ol[aria-label="Editable workflow nodes"] > li')).to_have_count(4)
    second.get_by_role("button", name="Workflow settings", exact=True).click()
    workbench(second).get_by_role("button", name="Discard local changes and reload", exact=True).click()
    expect(workbench(second).locator('ol[aria-label="Editable workflow nodes"] > li')).to_have_count(3)
    second.get_by_role("button", name="Workflow settings", exact=True).click()
    expect(inspect_control(second, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.reverse")
    other.close()

    designer.get_by_role("button", name="Workflow settings", exact=True).click()
    secondary = designer.locator("details.vaw-secondary-workflow-tools")
    if not secondary.evaluate("element => element.open"):
        secondary.locator(":scope > summary").click()
    workbench(designer).get_by_text("Import / export definition", exact=True).click()
    workbench(designer).get_by_role("button", name="Copy definition into JSON editor", exact=True).click()
    expect(workbench(designer).get_by_label("Workflow JSON", exact=True)).not_to_have_value("")
    exported = json.loads(workbench(designer).get_by_label("Workflow JSON", exact=True).input_value())
    assert exported["workflowId"] == workflow_id
    exported["workflowId"] = "unrelated-import-id"
    exported["nodes"]["step-1"]["type"] = "core.text.uppercase"
    workbench(designer).get_by_label("Workflow JSON", exact=True).fill(json.dumps(exported))
    workbench(designer).get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_role("button", name="Workflow settings", exact=True).click()
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.uppercase")
    assert workbench(designer).locator("[data-vaw-workflow-selector]").input_value() == workflow_id
    workbench(designer).get_by_role("button", name="Undo edit", exact=True).click()
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value("core.text.reverse")
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Designer mobile overflow"
    assert not errors, errors
    page.close()
    print("Browser graph edits/undo, saved-source run, cross-tab conflicts, and JSON import passed.")


def mcp_designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for(state="attached")
    workflow_id = workbench(designer).locator("[data-vaw-workflow-selector]").input_value()
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option(label="MCP · reverse")
    workbench(designer).get_by_text("MCP binding: demo.reverse", exact=False).wait_for()
    workbench(designer).get_by_role("button", name="Save", exact=True).click()
    previous_run_ids = {run["header"]["runId"] for run in get("/api/runs")}
    run_saved_workflow(page, designer, "abc", "cba")
    deadline = time.monotonic() + 15
    while True:
        new_runs = [run for run in get("/api/runs") if run["header"]["runId"] not in previous_run_ids]
        if new_runs:
            run_id = new_runs[0]["header"]["runId"]
            break
        assert time.monotonic() < deadline, "Studio did not start the saved workflow run"
        time.sleep(0.05)
    while True:
        recorded = get("/api/runs/" + run_id)
        if recorded["isTerminal"]:
            break
        assert time.monotonic() < deadline, "Workflow run did not finish"
        time.sleep(0.05)
    assert recorded["status"] == "completed" and recorded["events"][-1]["data"] == "cba"
    show_runs(page)
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_text("Recorded events at this position", exact=False).click()
    viewer.get_by_role("cell", name="tool.completed", exact=True).wait_for()
    assert get("/api/workflows/" + workflow_id)["definition"]["schemaVersion"] == "1.1"
    page.reload()
    expect(workbench(designer).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    page.goto(BASE + "/projects/default?workflow=" + workflow_id, wait_until="networkidle")
    saved_tool = get("/api/workflows/" + workflow_id)["definition"]["nodes"]["step-1"]["tool"]
    expect(inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True)).to_have_value(
        "mcp:demo.reverse:" + saved_tool["contractHash"] + ":" + saved_tool["descriptionHash"])
    page.close()
    print("Browser MCP binding selection, saved-source execution, recorded tool result, and reload passed.")


def agent_designer_check(browser):
    from playwright.sync_api import expect

    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(workbench(designer).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    new_workflow_fixture(designer)
    workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for(state="attached")
    workflow_id = workbench(designer).locator("[data-vaw-workflow-selector]").input_value()
    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1", exact=True).select_option(label="Deterministic demo agent")
    inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1", exact=True).fill("Respond concisely.")
    designer.get_by_role("button", name="Workflow settings", exact=True).click()
    parameters = workbench(designer).locator("details.vaw-parameter-editor")
    parameters.locator("summary").click()
    parameters.get_by_label("Parameter ID", exact=True).fill("response-limit")
    parameters.get_by_label("Display name", exact=True).fill("Response token cap")
    parameters.get_by_label("Default value", exact=True).fill("2048")
    parameters.get_by_role("button", name="Add parameter", exact=True).click()
    # Blazor's dynamically generated legend text is not exposed as a stable
    # accessible name in every Playwright/browser combination; the fieldset
    # is keyed by parameter ID and its only select is the parameter type.
    parameters.locator("fieldset").last.locator("select").select_option("integer")
    designer.get_by_role("button", name="Workflow settings", exact=True).click()
    inspect_control(designer, "Output token limit source for step-1").get_by_label("Output token limit source for step-1", exact=True).select_option(label="Workflow input: Response token cap")
    workbench(designer).get_by_role("button", name="Save", exact=True).click()
    previous_run_ids = {run["header"]["runId"] for run in get("/api/runs")}
    run_saved_workflow(page, designer, "abc", "Demo agent: abc")
    deadline = time.monotonic() + 15
    while True:
        new_runs = [run for run in get("/api/runs") if run["header"]["runId"] not in previous_run_ids]
        if new_runs:
            run_id = new_runs[0]["header"]["runId"]
            break
        assert time.monotonic() < deadline, "Studio did not start the agent workflow run"
        time.sleep(0.05)
    while True:
        recorded = get("/api/runs/" + run_id)
        if recorded["isTerminal"]:
            break
        assert time.monotonic() < deadline, "Agent workflow run did not finish"
        time.sleep(0.05)
    assert recorded["status"] == "completed" and recorded["events"][-1]["data"] == "Demo agent: abc"
    agent_started = next(event for event in recorded["events"] if event["kind"] == "agent.started")
    model_requested = next(event for event in recorded["events"] if event["kind"] == "model.requested")
    assert json.loads(agent_started["data"])["maxOutputTokens"] == 2048
    assert json.loads(model_requested["data"])["maxOutputTokens"] == 2048
    show_runs(page)
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    expect(viewer.locator(".vaw-agent-metrics")).to_contain_text("Model turns · 1", timeout=20000)
    viewer.get_by_text("Recorded events at this position", exact=False).click()
    expect(viewer.get_by_role("columnheader", name="Time (local)", exact=True)).to_be_visible()
    event_time = viewer.locator("tbody time").first
    precise_time = event_time.get_attribute("datetime")
    assert precise_time and "T" in precise_time, f"Expected an ISO timestamp, got {precise_time!r}"
    expect(event_time).to_have_attribute("title", precise_time)
    expect(event_time).to_have_attribute("aria-label", re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}"))
    expect(viewer.get_by_role("cell", name="model.responded", exact=True)).to_have_count(1)
    page.reload()
    expect(workbench(designer).get_by_role("button", name="Save", exact=True)).to_be_enabled()
    page.goto(BASE + "/projects/default?workflow=" + workflow_id, wait_until="networkidle")
    expect(inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1", exact=True)).to_have_value("Respond concisely.")
    expect(inspect_control(designer, "Output token limit source for step-1").get_by_label("Output token limit source for step-1", exact=True)).to_have_value("parameter:response-limit")
    saved_definition = get("/api/workflows/" + workflow_id)["definition"]
    assert saved_definition["schemaVersion"] == "1.29"
    assert saved_definition["nodes"]["step-1"]["agent"]["maxOutputTokensParameterId"] == "response-limit"
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Agent editor mobile overflow"
    page.close()
    print("Browser agent instructions, workflow parameter binding, saved-source execution limits, model evidence, and reload passed.")


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
