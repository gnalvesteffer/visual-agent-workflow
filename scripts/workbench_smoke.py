"""Exercise compact nodes, selected properties, view switching and native graph fullscreen.

Uses deterministic text workflows and disposable Studio data. Requires an already
running development Studio on port 5099; no model/MCP credentials or calls.
"""
import argparse
import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
from workbench_browser import workbench, inspect_control, show_runs, new_workflow_fixture

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def prompt_template_check(page, designer):
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    definition = page.request.get(BASE + "/api/templates/default").json()
    definition["schemaVersion"] = "1.7"
    definition["parameters"] = {"customer": {"displayName": "Customer", "defaultValue": "Ada"}}
    definition["nodes"]["step-1"] = {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
        "providerId": "demo.scripted", "providerVersion": "1.0.0", "instructions": "Answer briefly.", "tools": []}}
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    inspect_control(designer, "Operation for step-1")
    designer.get_by_role("button", name="Customize user message", exact=True).click()
    editor = designer.get_by_role("textbox", name="User message template", exact=True)
    editor.fill("What should I tell {{parameter:c")
    suggestion = designer.get_by_role("button", name="Customer · customer", exact=True)
    expect(suggestion).to_be_visible()
    editor.press("Tab")
    expect(suggestion).to_be_focused()
    page.keyboard.press("Enter")
    expect(editor).to_have_value("What should I tell {{parameter:customer}}")
    expect(designer.get_by_text("Preview with sample input: What should I tell Ada", exact=True)).to_be_visible()
    tool = designer.locator('input[aria-label="Assign demo.add-integers to step-1"]')
    expect(tool).to_have_count(1)
    tool.check()
    tool_details = tool.locator("xpath=ancestor::div[contains(@class, 'vaw-agent-tool-assignment')]").locator("details")
    tool_details.locator("summary").click()
    expect(tool_details).to_contain_text("Adds two base-10 integers")
    expect(tool_details).to_contain_text("left: integer required")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()


def text_template_node_check(page, designer):
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    workflow_id = designer.get_by_label("Saved workflow", exact=True).input_value()
    canvas = designer.locator(".vaw-graph-editor")
    wire=canvas.locator("path[data-wire]").first
    wire_point=wire.evaluate("""path=>{const point=path.getPointAtLength(path.getTotalLength()/2), matrix=path.getScreenCTM();return {x:matrix.a*point.x+matrix.c*point.y+matrix.e,y:matrix.b*point.x+matrix.d*point.y+matrix.f}}""")
    page.mouse.click(wire_point["x"],wire_point["y"],button="right")
    menu=canvas.get_by_role("dialog",name="Graph actions",exact=True)
    expect(menu).to_be_visible()
    menu.get_by_role("button",name="Add Text template",exact=True).click()
    expect(canvas.locator("li[data-node-type='core.text.template']")).to_have_count(1)
    template_id = canvas.locator("li[data-node-type='core.text.template']").get_attribute("data-node-id")
    parameters = designer.locator("details.vaw-parameter-editor > summary")
    if parameters.evaluate("element => !element.parentElement.open"):
        parameters.click()
    designer.get_by_label("Parameter ID", exact=True).fill("customer")
    designer.get_by_label("Display name", exact=True).fill("Customer")
    designer.get_by_label("Default value", exact=True).fill("Ada")
    designer.get_by_label("Description", exact=True).fill("Person whose question is being answered")
    designer.get_by_role("button", name="Add parameter", exact=True).click()
    inspect_control(designer, f"Operation for {template_id}")
    editor = designer.get_by_role("textbox", name="Template text", exact=True)
    editor.fill("Customer {{parameter:customer}} asks: {{input}}")
    editor.press("Tab")
    expect(designer.get_by_text("Preview with sample input: Customer Ada asks: Sample input", exact=True)).to_be_visible()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()
    assert saved["draft"]["definition"]["parameters"]["customer"]["description"] == "Person whose question is being answered"
    assert saved["draft"]["definition"]["nodes"][template_id]["type"] == "core.text.template"
    assert saved["draft"]["definition"]["nodes"][template_id]["textTemplate"]["parts"] == [
        {"kind": "literal", "value": "Customer "},
        {"kind": "parameter", "value": "customer"},
        {"kind": "literal", "value": " asks: "},
        {"kind": "input", "value": ""},
    ]
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    designer.get_by_text("Published and activated version 1.", exact=True).wait_for()
    revision = page.request.get(BASE + f"/api/workflows/{workflow_id}/revisions").json()[0]["revision"]
    response = page.request.post(BASE + f"/api/revisions/{revision['revisionId']}/runs",
        data={"input": "What is 1+1?", "parameters": {"customer": "Grace"}})
    assert response.ok, response.text()
    run_id = response.json()["header"]["runId"]
    page.wait_for_function("""async id => (await (await fetch('/api/runs/'+id)).json()).status==='completed'""", arg=run_id)
    run = page.request.get(BASE + f"/api/runs/{run_id}").json()
    assert any(event["nodeId"] == template_id and event["kind"] == "node.completed" and
               event.get("data") == "Customer Grace asks: What is 1+1?" for event in run["events"])


def typed_json_projection_check(page, designer):
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    workflow_id = designer.get_by_label("Saved workflow", exact=True).input_value()
    definition = page.request.get(BASE + "/api/templates/default").json()
    definition.update({"schemaVersion": "1.13", "workflowId": workflow_id, "entryNodeId": "input"})
    definition["nodes"] = {
        "input": {"type": "core.input", "typeVersion": "1.0.0", "inputDataType": "json"},
        "project": {"type": "core.json.project", "typeVersion": "1.0.0", "jsonProjection": {
            "fields": [{"name": "name", "pointer": "/name"}, {"name": "items", "pointer": "/items"}]}},
        "output": {"type": "core.output", "typeVersion": "1.0.0", "outputDataType": "json"}}
    definition["connections"] = {
        "json": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "project", "portId": "in"}, "kind": "data"},
        "result": {"source": {"nodeId": "project", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}}
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    designer.get_by_label("Node to organize", exact=True).select_option("project")
    designer.get_by_label("Expected JSON type for field 0 on project", exact=True).select_option("object")
    designer.get_by_label("JSON output property 0 for project", exact=True).fill("customer")
    designer.get_by_label("JSON pointer 0 for project", exact=True).fill("/customer")
    designer.get_by_role("button", name="Add object property", exact=True).nth(0).click()
    designer.get_by_label("JSON field 0 customer property 0 name", exact=True).fill("displayName")
    designer.get_by_label("JSON field 0 customer property 0 pointer", exact=True).fill("/fullName")
    designer.get_by_label("JSON field 0 customer property 0 value kind", exact=True).select_option("string")
    designer.get_by_label("Expected JSON type for field 1 on project", exact=True).select_option("array")
    designer.get_by_label("JSON field 1 items array item value kind", exact=True).select_option("object")
    designer.get_by_role("button", name="Add object property", exact=True).nth(1).click()
    designer.get_by_label("JSON field 1 items array item property 0 name", exact=True).fill("itemId")
    designer.get_by_label("JSON field 1 items array item property 0 pointer", exact=True).fill("/id")
    designer.get_by_label("JSON field 1 items array item property 0 value kind", exact=True).select_option("integer")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert saved["schemaVersion"] == "1.16"
    assert saved["nodes"]["project"]["jsonProjection"]["fields"] == [
        {"name": "customer", "pointer": "/customer", "valueSchema": {
            "type": "object", "properties": [{"name": "displayName", "pointer": "/fullName", "schema": {"type": "string"}}]}},
        {"name": "items", "pointer": "/items", "valueSchema": {"type": "array", "items": {
            "type": "object", "properties": [{"name": "itemId", "pointer": "/id", "schema": {"type": "integer"}}]}}}]


def explicit_graph_preview_check(page, designer):
    """Exercise opt-in explicit pin authoring, draft save, execution and read-only playback."""
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    workflow_id = designer.get_by_label("Saved workflow", exact=True).input_value()
    designer.get_by_role("button", name="Start typed graph preview", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert saved["schemaVersion"] == "1.23"

    definition = page.request.get(BASE + "/api/templates/default").json()
    definition.update({"schemaVersion": "1.23", "workflowId": workflow_id, "entryNodeId": "input"})
    definition["nodes"] = {
        "input": {"type": "core.input", "typeVersion": "1.0.0"},
        "constant": {"type": "core.constant.string", "typeVersion": "1.0.0", "stringConstant": {"value": "!"}},
        "concat": {"type": "core.text.concat", "typeVersion": "1.0.0"},
        "output": {"type": "core.output", "typeVersion": "1.0.0"}}
    definition["connections"] = {
        "input-value": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "concat", "portId": "left"}, "kind": "data"},
        "suffix": {"source": {"nodeId": "constant", "portId": "out"}, "target": {"nodeId": "concat", "portId": "right"}, "kind": "data"},
        "result": {"source": {"nodeId": "concat", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}}
    definition["executionConnections"] = {
        "activate-output": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"}}
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(1)
    expect(canvas.locator("path[data-kind='data']")).to_have_count(3)
    designer.get_by_label("Node to organize", exact=True).select_option("constant")
    designer.get_by_label("String constant value for constant", exact=True).fill("?")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    definition["nodes"]["constant"]["stringConstant"]["value"] = "?"
    response = page.request.post(BASE + "/api/runs", data={"input": "Hello", "definition": definition})
    assert response.ok, response.text()
    run_id = response.json()["header"]["runId"]
    run = page.evaluate("""async id => {
        for (let attempt = 0; attempt < 100; attempt++) {
            const run = await (await fetch('/api/runs/'+id)).json();
            if (run.status === 'completed' && run.events.filter(event => event.kind === 'data.evaluated').length === 3) return run;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return await (await fetch('/api/runs/'+id)).json();
    }""", run_id)
    values = [event for event in run["events"] if event["kind"] == "data.evaluated"]
    data_connections = sorted(event.get("connectionId") for event in values)
    assert data_connections == ["data:input-value", "data:result", "data:suffix"], data_connections
    assert [json.loads(event["data"])["value"] for event in values] == ["Hello", "?", "Hello?"]

    show_runs(page)
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    page.get_by_role("button", name="Refresh history", exact=True).click()
    page.get_by_label("Filter recorded runs", exact=True).fill(workflow_id)
    expect(page.locator(".vaw-history button")).to_have_count(1)
    page.locator(".vaw-history button").click()
    run_canvas = viewer.locator(".vaw-graph-editor")
    expect(run_canvas.locator("path.vaw-wire-delivered[data-kind='data']")).to_have_count(3)
    run_canvas.get_by_role("button", name="concat core.text.concat Evaluated", exact=True).click()
    expect(viewer.get_by_role("region", name="Selected node payloads")).to_contain_text("Hello?")
    slider = viewer.get_by_label("Recorded position", exact=True)
    slider.evaluate("e => { e.value=0; e.dispatchEvent(new Event('input',{bubbles:true})); }")
    expect(run_canvas.locator("path.vaw-wire-delivered[data-kind='data']")).to_have_count(0)

    workbench(designer)
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    branch_workflow_id = designer.get_by_label("Saved workflow", exact=True).input_value()
    designer.get_by_role("button", name="Start typed graph preview", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    designer.get_by_role("button", name="Enable Boolean Branch preview", exact=True).click()
    library = designer.locator(".vaw-node-library")
    library.get_by_role("button", name="Add Compare strings", exact=True).click()
    library.get_by_role("button", name="Add Boolean Branch", exact=True).click()
    library.get_by_role("button", name="Add Terminate workflow", exact=True).click()
    branch = designer.locator("li[data-node-type='core.branch.boolean']")
    expect(branch).to_have_count(1)
    expect(branch.locator("[data-socket='condition'][data-socket-kind='data'][data-socket-type='boolean']")).to_have_count(1)
    expect(branch.locator("[data-socket='true'][data-socket-kind='execution']")).to_have_count(1)
    expect(branch.locator("[data-socket='false'][data-socket-kind='execution']")).to_have_count(1)
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_contain_text("unresolved issue")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved_branch = page.request.get(BASE + f"/api/workflows/{branch_workflow_id}/design").json()["draft"]["definition"]
    assert saved_branch["schemaVersion"] == "1.24"
    assert {node["type"] for node in saved_branch["nodes"].values()} >= {
        "core.text.equals", "core.branch.boolean", "core.terminate"}

    # The guarded schema upgrade is shown only for a valid proof graph. Import a
    # complete 1.24 graph so the smoke verifies that workflow path, not a bypass.
    branch_definition = page.request.get(BASE + "/api/templates/default").json()
    branch_definition.update({"schemaVersion": "1.24", "workflowId": branch_workflow_id, "entryNodeId": "input"})
    branch_definition["nodes"] = {
        "input": {"type": "core.input", "typeVersion": "1.0.0"},
        "expected": {"type": "core.constant.string", "typeVersion": "1.0.0", "stringConstant": {"value": "yes"}},
        "equals": {"type": "core.text.equals", "typeVersion": "1.0.0"},
        "branch": {"type": "core.branch.boolean", "typeVersion": "1.0.0"},
        "output": {"type": "core.output", "typeVersion": "1.0.0"},
        "terminate": {"type": "core.terminate", "typeVersion": "1.0.0", "termination": {"outcome": "failure"}}}
    branch_definition["connections"] = {
        "input-left": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "equals", "portId": "left"}, "kind": "data"},
        "expected-right": {"source": {"nodeId": "expected", "portId": "out"}, "target": {"nodeId": "equals", "portId": "right"}, "kind": "data"},
        "comparison": {"source": {"nodeId": "equals", "portId": "out"}, "target": {"nodeId": "branch", "portId": "condition"}, "kind": "data"},
        "output-value": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}}
    branch_definition["executionConnections"] = {
        "activate-branch": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "branch", "portId": "execute"}, "kind": "execution"},
        "true-route": {"source": {"nodeId": "branch", "portId": "true"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
        "false-route": {"source": {"nodeId": "branch", "portId": "false"}, "target": {"nodeId": "terminate", "portId": "execute"}, "kind": "execution"}}
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(branch_definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()

    designer.get_by_role("button", name="Enable Boolean primitives", exact=True).click()
    library.get_by_role("button", name="Add Boolean constant", exact=True).click()
    library.get_by_role("button", name="Add Boolean NOT", exact=True).click()
    boolean_constant = designer.locator("li[data-node-type='core.constant.boolean']")
    boolean_not = designer.locator("li[data-node-type='core.boolean.not']")
    expect(boolean_constant).to_have_count(1)
    expect(boolean_constant.locator("[data-socket='out'][data-socket-kind='data'][data-socket-type='boolean']")).to_have_count(1)
    expect(boolean_not.locator("[data-socket='value'][data-socket-kind='data'][data-socket-type='boolean']")).to_have_count(1)
    expect(boolean_not.locator("[data-socket='out'][data-socket-kind='data'][data-socket-type='boolean']")).to_have_count(1)
    constant_id = boolean_constant.get_attribute("data-node-id")
    boolean_constant.get_by_role("button", name=f"Move node {constant_id}").click()
    designer.get_by_label(f"Boolean constant value for {constant_id}", exact=True).select_option("true")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved_boolean = page.request.get(BASE + f"/api/workflows/{branch_workflow_id}/design").json()["draft"]["definition"]
    assert saved_boolean["schemaVersion"] == "1.25"
    assert saved_boolean["nodes"][constant_id]["booleanConstant"]["value"] is True
    assert {node["type"] for node in saved_boolean["nodes"].values()} >= {
        "core.constant.boolean", "core.boolean.not", "core.branch.boolean"}

    topology_definition = page.request.get(BASE + "/api/templates/default").json()
    topology_definition.update({"schemaVersion": "1.25", "workflowId": branch_workflow_id, "entryNodeId": "input"})
    topology_definition["nodes"] = {
        "input": {"type": "core.input", "typeVersion": "1.0.0"},
        "constant": {"type": "core.constant.boolean", "typeVersion": "1.0.0", "booleanConstant": {"value": True}},
        "not": {"type": "core.boolean.not", "typeVersion": "1.0.0"},
        "branch": {"type": "core.branch.boolean", "typeVersion": "1.0.0"},
        "output": {"type": "core.output", "typeVersion": "1.0.0"},
        "terminate": {"type": "core.terminate", "typeVersion": "1.0.0", "termination": {"outcome": "failure"}}}
    topology_definition["connections"] = {
        "constant-not": {"source": {"nodeId": "constant", "portId": "out"}, "target": {"nodeId": "not", "portId": "value"}, "kind": "data"},
        "not-branch": {"source": {"nodeId": "not", "portId": "out"}, "target": {"nodeId": "branch", "portId": "condition"}, "kind": "data"},
        "output-value": {"source": {"nodeId": "input", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}}
    topology_definition["executionConnections"] = {
        "activate-branch": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "branch", "portId": "execute"}, "kind": "execution"},
        "true-route": {"source": {"nodeId": "branch", "portId": "true"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
        "false-route": {"source": {"nodeId": "branch", "portId": "false"}, "target": {"nodeId": "terminate", "portId": "execute"}, "kind": "execution"}}
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(topology_definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
    designer.get_by_role("button", name="Enable branch topologies", exact=True).click()
    library.get_by_role("button", name="Add Boolean Branch", exact=True).click()
    library.get_by_role("button", name="Add Terminate workflow", exact=True).click()
    designer.get_by_role("button", name="Save draft", exact=True).click()
    designer.get_by_text("Draft saved.", exact=True).wait_for()
    saved_topology = page.request.get(BASE + f"/api/workflows/{branch_workflow_id}/design").json()["draft"]["definition"]
    assert saved_topology["schemaVersion"] == "1.26"
    assert sum(node["type"] == "core.branch.boolean" for node in saved_topology["nodes"].values()) == 2
    assert sum(node["type"] == "core.terminate" for node in saved_topology["nodes"].values()) == 2


def check(browser, screenshots):
    page = browser.new_page(viewport={"width":1920,"height":1080})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE)
    designer = page.get_by_role("region",name="Workflow designer",exact=True)
    expect(designer.get_by_role("button",name="New workflow",exact=True)).to_be_enabled()
    designer.get_by_role("button",name="New workflow",exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.",exact=True).wait_for()
    expect(designer.locator("li[data-node-id]")).to_have_count(2)
    designer.get_by_role("button",name="Add Uppercase",exact=True).click()
    expect(designer.locator("li[data-node-id]")).to_have_count(3)
    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas).to_have_attribute("data-ready","true")
    added=canvas.locator("li[data-node-type='core.text.uppercase']")
    added_id=added.get_attribute("data-node-id")
    # A library click creates a standalone draft node. It does not silently alter existing routes.
    assert canvas.locator("path[data-wire]").count() == 1
    added.get_by_role("button",name=f"Move node {added_id}",exact=True).click()
    page.keyboard.press("Delete")
    expect(designer.locator("li[data-node-id]")).to_have_count(2)
    # The remaining workbench checks use an explicitly connected fixture.
    new_workflow_fixture(designer)
    canvas = designer.locator(".vaw-graph-editor")
    step = "step-1"
    workflow_id = designer.get_by_label("Saved workflow",exact=True).input_value()
    assert canvas.locator("li[data-node-id] input, li[data-node-id] textarea, li[data-node-id] select").count() == 0
    assert canvas.locator("li[data-node-id]").evaluate_all("nodes => Math.max(...nodes.map(n=>n.getBoundingClientRect().height))") < 260
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_be_visible()
    # Node actions are contextual and retain a single undo entry for definition + placement.
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click(button="right")
    menu=canvas.get_by_role("dialog",name="Graph actions",exact=True)
    expect(menu).to_be_visible()
    expect(menu.get_by_label("Search node actions",exact=True)).to_be_focused()
    menu.get_by_role("button",name="Duplicate node",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    viewport=canvas.locator(".vaw-canvas-viewport")
    viewport.focus(); page.keyboard.press("Control+z")
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    # Space opens searchable creation without a mouse; Escape cancels without an edit.
    viewport.focus(); page.keyboard.press("Space")
    expect(menu).to_be_visible()
    menu.get_by_label("Search node actions",exact=True).fill("reverse")
    assert menu.locator("[data-node-key]").count() >= 1
    page.keyboard.press("Escape")
    expect(menu).to_have_count(0)
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    # Dragging a library entry onto empty space places it there on the selected path.
    viewport.scroll_into_view_if_needed()
    bounds=viewport.bounding_box()
    destination={"x":bounds["x"]+bounds["width"]*.5,"y":bounds["y"]+350}
    library=canvas.get_by_role("complementary",name="Workflow node library",exact=True)
    library.get_by_role("button",name="Add Reverse",exact=True).drag_to(viewport,target_position={"x":bounds["width"]*.5,"y":350})
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    dropped=canvas.locator("li[data-node-type='core.text.reverse']")
    # Chromium may auto-scroll the document during native drag-and-drop. Compare
    # against the viewport's post-drop position while keeping the drop offset.
    after_drop_bounds=viewport.bounding_box()
    after_drop_destination={"x":after_drop_bounds["x"]+after_drop_bounds["width"]*.5,"y":after_drop_bounds["y"]+350}
    assert abs(dropped.bounding_box()["x"]-after_drop_destination["x"])<12
    assert abs(dropped.bounding_box()["y"]-after_drop_destination["y"])<12, f"dropped node {dropped.bounding_box()} did not reach screen destination {after_drop_destination}; viewport {after_drop_bounds}"
    viewport.focus();page.keyboard.press("Control+z")
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    # An output-pin gesture opens creation for that exact existing path.
    canvas.get_by_role("button",name="Fit graph",exact=True).click()
    pin=canvas.locator(f"li[data-node-id='{step}'] .vaw-output-ports [data-socket]")
    pin.scroll_into_view_if_needed(); box=pin.bounding_box();bounds=viewport.bounding_box()
    page.mouse.move(box["x"]+box["width"]/2,box["y"]+box["height"]/2)
    page.mouse.down();page.mouse.move(bounds["x"]+bounds["width"]*.5,bounds["y"]+bounds["height"]-24,steps=8);page.mouse.up()
    expect(menu).to_be_visible()
    if screenshots:
        screenshots.mkdir(parents=True,exist_ok=True)
        page.screenshot(path=str(screenshots / "contextual-creation.png"))
    menu.get_by_role("button",name="Add Reverse",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    reverse_id=canvas.locator("li[data-node-type='core.text.reverse']").get_attribute("data-node-id")
    canvas.get_by_role("button",name=f"Move node {reverse_id}",exact=True).click()
    assert canvas.get_attribute("data-context-menu") == "true"
    canvas.evaluate("element => { window.__vawDeleteKeys=[]; element.addEventListener('keydown', event => window.__vawDeleteKeys.push({key:event.key,target:event.target.closest('[data-drag-node]')?.dataset.dragNode}), true); }")
    page.keyboard.press("Delete")
    page.wait_for_timeout(300)
    assert page.evaluate("window.__vawDeleteKeys") == [{"key":"Delete","target":reverse_id}], f"Delete did not reach the focused node: {page.evaluate('window.__vawDeleteKeys')}"
    assert canvas.get_attribute("data-ready") == "true", canvas.locator("[data-canvas-error]").inner_text()
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    canvas.get_by_role("button",name=f"Move node {step}",exact=True).click()
    canvas.get_by_label("Node label",exact=True).fill("Normalize message")
    canvas.get_by_label("Node comment",exact=True).fill("Deterministic text processing. Inspect the recorded result after execution.")
    canvas.get_by_label("Node comment",exact=True).press("Tab")
    canvas.get_by_label(f"Operation for {step}",exact=True).select_option("core.text.reverse")
    expect(canvas.get_by_role("button",name=f"Move node {step}",exact=True)).to_contain_text("Normalize message")
    canvas.get_by_text("Labels, comments, sections, and wire routing",exact=True).click()
    canvas.get_by_role("button",name="Add reroute point",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(1)
    canvas.get_by_role("button",name="Arrange nodes",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(0)
    designer.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(1)
    designer.get_by_role("button",name="Redo edit",exact=True).click()
    expect(canvas.locator(".vaw-reroute")).to_have_count(0)
    canvas.get_by_text("Labels, comments, sections, and wire routing",exact=True).click()
    designer.get_by_role("button",name="Save draft",exact=True).click()
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")
    before = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()

    # A narrow consuming host must not constrain native fullscreen to its old container query width.
    page.locator(".vaw-studio").evaluate("element => element.style.inlineSize = '780px'")
    canvas.get_by_role("button",name="Fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement !== null")
    assert page.evaluate("document.fullscreenElement.classList.contains('vaw-graph-editor')")
    assert canvas.bounding_box()["width"] >= 1900
    canvas.get_by_role("button",name="Fit graph",exact=True).click()
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_have_value("core.text.reverse")
    assert canvas.get_by_role("complementary",name="Selected node properties",exact=True).bounding_box()["x"] > 1000
    if screenshots:
        screenshots.mkdir(parents=True,exist_ok=True)
        page.screenshot(path=str(screenshots / "fullscreen-designer.png"))
    viewport.focus(); page.keyboard.press("Space")
    expect(menu).to_be_visible()
    page.keyboard.press("Escape")
    expect(menu).to_have_count(0)
    assert page.evaluate("document.fullscreenElement !== null"), "Closing actions must preserve fullscreen"
    # Tab wraps within fullscreen and does not escape into hidden application controls.
    page.evaluate("""() => {
        const root=document.fullscreenElement;
        const controls=[...root.querySelectorAll('button,input,select,textarea,summary,a[href],[tabindex]')]
            .filter(e=>e.tabIndex>=0&&!e.matches(':disabled')&&e.getClientRects().length);
        controls.at(-1).focus();
    }""")
    page.keyboard.press("Tab")
    assert page.evaluate("document.fullscreenElement.contains(document.activeElement)")
    page.keyboard.press("Escape")
    page.wait_for_function("document.fullscreenElement === null")
    expect(canvas.get_by_role("button",name="Fullscreen graph",exact=True)).to_be_focused()
    page.locator(".vaw-studio").evaluate("element => element.style.removeProperty('inline-size')")
    assert page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json() == before
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")

    designer.get_by_role("button",name="Publish & activate",exact=True).click()
    designer.get_by_text("Published and activated version 1.",exact=True).wait_for()
    designer.get_by_label("Published run input",exact=True).fill("Agent workflow")
    designer.get_by_role("button",name="Run selected version",exact=True).click()
    viewer=page.get_by_role("region",name="Workflow run viewer",exact=True)
    viewer.get_by_role("button",name="output core.output Completed",exact=True).click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("wolfkrow tnegA")
    assert not designer.is_visible()
    viewer.get_by_role("button",name="Fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement !== null")
    viewer.get_by_role("button",name="Fit graph",exact=True).click()
    assert viewer.locator(".vaw-wire-delivered").first.evaluate("e => Number.parseFloat(getComputedStyle(e).strokeWidth)") >= 5
    assert viewer.locator(".vaw-wire-delivered").first.evaluate("e => getComputedStyle(e).strokeDasharray") != "none"
    if screenshots:
        page.screenshot(path=str(screenshots / "fullscreen-execution.png"))
    slider=viewer.get_by_label("Recorded position",exact=True)
    slider.evaluate("e=>{e.value=0;e.dispatchEvent(new Event('input',{bubbles:true}));}")
    expect(viewer.locator(".vaw-wire-delivered")).to_have_count(0)
    viewer.get_by_role("button",name="Exit fullscreen graph",exact=True).click()
    page.wait_for_function("document.fullscreenElement === null")
    workbench(designer)
    inspect_control(designer,f"Operation for {step}")
    expect(canvas.get_by_label(f"Operation for {step}",exact=True)).to_have_value("core.text.reverse")
    designer.get_by_label("Find workflow",exact=True).fill("does-not-match-any-name")
    expect(designer.get_by_label("Saved workflow",exact=True)).to_have_value(workflow_id)
    show_runs(page)
    page.get_by_label("Filter recorded runs",exact=True).fill(workflow_id)
    expect(page.locator(".vaw-history button")).to_have_count(1)
    page.get_by_label("Filter recorded runs",exact=True).fill("no-matching-run")
    expect(page.locator(".vaw-history button")).to_have_count(0)
    page.get_by_label("Filter recorded runs",exact=True).fill("")
    workbench(designer)
    designer.get_by_label("Find workflow",exact=True).fill("")
    if screenshots:
        canvas.get_by_role("button",name="Fit graph",exact=True).click()
        page.wait_for_function("""() => {
            const v=document.querySelector('.vaw-designer .vaw-canvas-viewport').getBoundingClientRect();
            return [...document.querySelectorAll('.vaw-designer li[data-node-id]')].every(n=>{
                const b=n.getBoundingClientRect();return b.left>=v.left&&b.right<=v.right&&b.top>=v.top&&b.bottom<=v.bottom;});
        }""")
        page.evaluate("window.scrollTo(0,0)")
        page.screenshot(path=str(screenshots / "studio-designer.png"))
    # Library additions remain disconnected; users explicitly rewire pins when ready.
    library.get_by_role("button",name="Add Condition",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    assert canvas.locator("li[data-node-type='core.condition']").count() == 1
    assert canvas.locator("li[data-node-type='core.merge']").count() == 0
    branch=canvas.locator("li[data-node-type='core.condition']").get_attribute("data-node-id")
    false_pin=canvas.locator(f"li[data-node-id='{branch}'] [data-socket='false']")
    false_pin.scroll_into_view_if_needed();box=false_pin.bounding_box();bounds=viewport.bounding_box()
    page.mouse.move(box["x"]+box["width"]/2,box["y"]+box["height"]/2)
    page.mouse.down();page.mouse.move(bounds["x"]+80,bounds["y"]+bounds["height"]-30,steps=8);page.mouse.up()
    expect(menu).to_be_visible()
    menu.get_by_role("button",name="Add Reverse",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(5)
    assert canvas.locator(f"path[data-source='{branch}'][data-source-port='false']").count() == 0
    expect(designer.get_by_role("alert",name="Workflow validation issues")).to_contain_text("unresolved issue")
    canvas.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(4)
    canvas.get_by_role("button",name="Undo edit",exact=True).click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(3)
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Draft saved")
    # Permission rejection leaves an explicit error and a usable inline editor.
    canvas.evaluate("root => root.requestFullscreen = () => Promise.reject(new Error('fixture denial'))")
    canvas.get_by_role("button",name="Fullscreen graph",exact=True).click()
    expect(canvas.locator("[data-fullscreen-error]")).to_be_visible()
    assert page.evaluate("document.fullscreenElement === null")
    canvas.evaluate("root => delete root.requestFullscreen")
    page.set_viewport_size({"width":390,"height":844})
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "Narrow viewport overflow"
    page.set_viewport_size({"width":1920,"height":1080})
    prompt_template_check(page, designer)
    text_template_node_check(page, designer)
    typed_json_projection_check(page, designer)
    explicit_graph_preview_check(page, designer)
    assert not errors,errors
    page.close()
    print("Blank creation, node library/drop, context search/cancel/duplicate, output-pin creation, guarded keyboard deletion/undo, compact properties, route arrangement, native fullscreen/focus/rejection, narrow embeds, fit, recorded paths/playback, workspace filters, keyboard prompt-token insertion, deterministic text-template authoring/replay, recursive JSON projection authoring, explicit typed-graph authoring/execution/replay, Boolean branch authoring, typed Boolean primitive upgrade/configuration, bounded branch topology upgrade and node authoring, draft save, and MCP tool detail review passed.")


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots",type=Path)
    args=parser.parse_args()
    with sync_playwright() as p:
        options={"headless":True}
        if args.browser_executable:options["executable_path"]=args.browser_executable
        browser=p.chromium.launch(**options)
        try:check(browser,args.screenshots)
        finally:browser.close()
