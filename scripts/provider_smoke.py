"""Check host-configured official-SDK providers using a local protocol fixture.

This is NOT live OpenAI validation. Starts only owned loopback fixture/Studio
processes, uses ephemeral credentials, and executes deterministic local responses.
"""

from workbench_browser import workbench, inspect_control, new_workflow_fixture, show_runs
import argparse
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import tempfile
import threading
import time
import urllib.request
import uuid
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parent.parent


def unused_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stop(process):
    if process.poll() is None:
        process.send_signal(signal.SIGTERM)
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=5)


def parallel_user_template_check(page, base, get, state, state_lock):
    """Verify agent-template editing and execution in a published parallel graph."""
    branches = [
        {"id": "branch_1", "label": "Research", "anchorNodeId": "anchor_1"},
        {"id": "branch_2", "label": "Critique", "anchorNodeId": "anchor_2"},
        {"id": "branch_3", "label": "Synthesis", "anchorNodeId": "anchor_3"},
    ]
    nodes = {
        "input": {"type": "core.input", "typeVersion": "1.0.0"},
        "fork": {"type": "core.parallel.fork", "typeVersion": "1.0.0", "parallelFork": {"branches": branches}},
        "join": {"type": "core.parallel.join", "typeVersion": "1.0.0", "parallelJoin": {
            "forkNodeId": "fork", "branchIds": [branch["id"] for branch in branches]}},
        "output": {"type": "core.output", "typeVersion": "1.0.0", "outputDataType": "json"},
    }
    execution = {
        "start": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "fork", "portId": "execute"}, "kind": "execution"},
        "finish": {"source": {"nodeId": "join", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
    }
    task_prefixes = [
        "Research: identify evidence about ",
        "Critique: find weaknesses in ",
        "Synthesis: combine useful conclusions about ",
    ]
    for index, branch in enumerate(branches, start=1):
        branch_id = branch["id"]
        anchor_id = branch["anchorNodeId"]
        agent_id = f"agent_{index}"
        nodes[anchor_id] = {"type": "core.parallel.branch", "typeVersion": "1.0.0",
                            "parallelBranch": {"forkNodeId": "fork", "branchId": branch_id}}
        nodes[agent_id] = {"type": "core.agent", "typeVersion": "1.0.0", "agent": {
            "providerId": "openai.fixture", "providerVersion": "1.0.0",
            "instructions": f"Act as the {branch['label']} specialist.", "tools": [],
            "userMessage": {"parts": [
                {"kind": "literal", "value": task_prefixes[index - 1]},
                {"kind": "input", "value": ""},
                {"kind": "literal", "value": " for audience "},
                {"kind": "parameter", "value": "audience"},
            ]},
        }}
        execution[f"fork-{index}"] = {"source": {"nodeId": "fork", "portId": f"branch.{branch_id}"},
                                       "target": {"nodeId": anchor_id, "portId": "execute"}, "kind": "execution"}
        execution[f"agent-{index}"] = {"source": {"nodeId": anchor_id, "portId": "then"},
                                        "target": {"nodeId": agent_id, "portId": "execute"}, "kind": "execution"}
        execution[f"join-{index}"] = {"source": {"nodeId": agent_id, "portId": "out"},
                                       "target": {"nodeId": "join", "portId": f"branch.{branch_id}"}, "kind": "execution"}

    workflow_id = "parallel-template-" + uuid.uuid4().hex
    definition = {
        "schemaVersion": "1.32", "workflowId": workflow_id, "displayName": "Parallel template authoring proof",
        "entryNodeId": "input", "nodes": nodes,
        "connections": {"join-result": {"source": {"nodeId": "join", "portId": "out"},
                                         "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}},
        "executionConnections": execution,
        "parameters": {"audience": {"displayName": "Audience", "defaultValue": "default readers", "type": "string"}},
    }
    response = page.request.put(base + f"/api/workflows/{workflow_id}/draft",
                                data={"definition": definition, "expectedGeneration": 0})
    assert response.ok, response.text()
    calls_before = len(state["calls"])

    page.goto(base + "/projects/default?workflow=" + workflow_id, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(designer.locator("[data-graph-save]")).to_be_enabled()
    expect(designer.get_by_label("Workflow name", exact=True)).to_have_value("Parallel template authoring proof")
    inspect_control(designer, "Move node agent_1")
    editor = designer.get_by_role("textbox", name="User message template", exact=True)
    editor.fill("Research: verify {{input}} for audience {{parameter:audience}}")
    editor.press("Tab")
    expect(designer.locator(".vaw-definition-status")).to_contain_text("Unsaved changes", timeout=15_000)
    designer.locator("[data-graph-save]").click()
    expect(designer).to_contain_text("Draft saved.")

    design = get(f"/api/workflows/{workflow_id}/design")
    edited = design["draft"]["definition"]
    assert edited["schemaVersion"] == "1.32", edited["schemaVersion"]
    assert edited["nodes"]["agent_1"]["agent"]["userMessage"]["parts"] == [
        {"kind": "literal", "value": "Research: verify "}, {"kind": "input", "value": ""},
        {"kind": "literal", "value": " for audience "}, {"kind": "parameter", "value": "audience"},
    ]

    page.goto(base + "/projects/default?workflow=" + workflow_id, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    expect(designer.locator("[data-graph-save]")).to_be_enabled()
    inspect_control(designer, "Move node agent_1")
    expect(designer.get_by_role("textbox", name="User message template", exact=True)).to_have_value(
        "Research: verify {{input}} for audience {{parameter:audience}}")
    designer.get_by_role("button", name="Configure test input", exact=True).click()
    designer.get_by_label("Workflow input", exact=True).fill("climate adaptation")
    designer.get_by_label("Audience · audience", exact=True).fill("city planners")
    designer.get_by_role("button", name="Run workflow", exact=True).click()
    history = designer.get_by_label("Workflow run history", exact=True)
    expect(history.locator("tbody tr").filter(has_text="Parallel template authoring proof")).to_contain_text("completed", timeout=25000)
    run_id = next(item for item in get("/api/runs") if item["header"]["definition"]["workflowId"] == workflow_id)["header"]["runId"]
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        run = get(f"/api/runs/{run_id}")
        if run["status"] in ("completed", "failed", "interrupted"):
            break
        time.sleep(0.05)
    assert run["status"] == "completed", run
    requested = [event for event in run["events"] if event["kind"] == "model.requested"]
    expected = {
        "agent_1": "Research: verify climate adaptation for audience city planners",
        "agent_2": "Critique: find weaknesses in climate adaptation for audience city planners",
        "agent_3": "Synthesis: combine useful conclusions about climate adaptation for audience city planners",
    }
    actual = {}
    for event in requested:
        request = json.loads(event["data"])
        actual[event["nodeId"]] = request["messages"][0]["content"][0]["text"]
    assert actual == expected, actual
    with state_lock:
        assert len(state["calls"]) == calls_before + 3

    history.get_by_role("button", name="Inspect run " + run_id, exact=True).click()
    viewer = page.get_by_role("region", name="Workflow run viewer", exact=True)
    viewer.get_by_role("button", name="Previous event", exact=True).click()
    expect(viewer).to_contain_text("Recorded playback")
    for agent_id, message in expected.items():
        viewer.get_by_role("button", name=f"Inspect node {agent_id}", exact=True).click()
        expect(viewer.get_by_role("region", name="Selected node payloads")).to_contain_text(message)
    with state_lock:
        assert len(state["calls"]) == calls_before + 3, "Playback must not issue another model request"


def model_connection_form_check(page, base, endpoint, key, state, state_lock):
    page.goto(base + "/settings", wait_until="networkidle")
    form = page.locator("#add-provider")
    form.get_by_label("Connection ID", exact=False).fill("ui.fixture")
    form.get_by_label("Connection name", exact=False).fill("Protocol UI connection")
    form.get_by_label("Model ID", exact=False).fill("fixture-ui-model")
    form.get_by_label("API base URL", exact=False).fill(endpoint)
    form.get_by_label("Allow unencrypted HTTP for this endpoint", exact=True).check()
    form.get_by_label("API key (optional)", exact=False).fill(key)
    with state_lock:
        before = len(state["calls"])
    form.get_by_role("button", name="Test connection", exact=True).click()
    expect(form.get_by_role("status")).to_contain_text("Connection succeeded")
    with state_lock:
        assert len(state["calls"]) == before + 1
        assert state["calls"][-1]["model"] == "fixture-ui-model"
        assert state["calls"][-1].get("max_completion_tokens", state["calls"][-1].get("max_tokens")) == 1
    form.get_by_role("button", name="Add connection", exact=True).click()
    card = page.locator(".vaw-provider-card").filter(has_text="Protocol UI connection")
    expect(card).to_be_visible()
    assert key not in page.content()
    card.get_by_role("button", name="Test connection", exact=True).click()
    expect(card.get_by_role("status")).to_contain_text("Connection succeeded")
    card.get_by_role("button", name="Edit connection", exact=True).click()
    expect(form.get_by_label("Connection ID", exact=False)).to_be_disabled()
    expect(form.get_by_label("Model ID", exact=False)).to_have_value("fixture-ui-model")
    form.get_by_label("Model ID", exact=False).fill("fixture-ui-edited")
    form.get_by_label("Model ID", exact=False).press("Tab")
    form.get_by_role("button", name="Save connection", exact=True).click()
    expect(card).to_contain_text("fixture-ui-edited")
    with state_lock:
        assert len(state["calls"]) == before + 2, "Saving and editing must not invoke the model"
    card.get_by_role("button", name="Delete connection", exact=True).click()
    card.get_by_role("button", name="Confirm delete", exact=True).click()
    expect(card).to_have_count(0)


def check(dotnet, browser_executable, template_only=False):
    key = secrets.token_urlsafe(32)
    state = {"calls":[], "status":200}
    state_lock = threading.Lock()
    class Fixture(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            assert self.path == "/v1/chat/completions"
            assert self.headers.get("Authorization") == "Bearer " + key
            with state_lock:
                state["calls"].append(body)
                status = state["status"]
            if status != 200:
                payload = {"error":{"message":key,"type":"fixture_error"}}
            else:
                tool_result = next((m for m in reversed(body["messages"]) if m["role"] == "tool"), None)
                text = next((m["content"] for m in reversed(body["messages"]) if m["role"] == "user"), "")
                if body.get("tools") and tool_result is None:
                    message = {"role":"assistant","content":None,"tool_calls":[{"id":"fixture-call","type":"function",
                        "function":{"name":body["tools"][0]["function"]["name"],"arguments":json.dumps({"text":text})}}]}
                    finish = "tool_calls"
                else:
                    result_text = tool_result["content"] if tool_result else text
                    if tool_result:
                        # The official bridge serializes string function results as JSON text.
                        try:
                            decoded = json.loads(result_text)
                            if isinstance(decoded, str):
                                result_text = decoded
                        except (ValueError, TypeError):
                            pass
                    message = {"role":"assistant","content":"Protocol fixture: " + result_text}
                    finish = "stop"
                payload = {"id":"chatcmpl-local","object":"chat.completion","created":1700000000,"model":"fixture-resolved",
                    "choices":[{"index":0,"message":message,"finish_reason":finish}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13}}
            encoded = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)
    server = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    endpoint = f"http://127.0.0.1:{server.server_port}/v1/"
    studio_port = unused_port()
    base = f"http://127.0.0.1:{studio_port}"
    process = None
    with tempfile.TemporaryDirectory(prefix="vaw-provider-") as temporary:
        directory = Path(temporary)
        env = dict(os.environ)
        prefix = "WorkflowAgents__OpenAI__openai.fixture__"
        env.update({prefix+"Enabled":"true",prefix+"Version":"1.0.0",prefix+"Model":"fixture-model",prefix+"ApiKey":key,
                    prefix+"Endpoint":endpoint,prefix+"AllowInsecureLoopback":"true",prefix+"DisplayName":"Configured protocol fixture",
                    prefix+"TimeoutSeconds":"180",
                    "ASPNETCORE_ENVIRONMENT":"Development","WorkflowStudio__Port":str(studio_port),"WorkflowStudio__DataDirectory":str(directory/"studio")})
        def get(path):
            with urllib.request.urlopen(base + path, timeout=3) as response:
                return json.load(response)
        try:
            process = subprocess.Popen([dotnet, str(ROOT/"src/VisualAgentWorkflow.Studio/bin/Release/net10.0/VisualAgentWorkflow.Studio.dll")],
                cwd=ROOT/"src/VisualAgentWorkflow.Studio", env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                assert process.poll() is None, "Configured Studio exited before readiness"
                try:
                    providers = get("/api/agent-providers")
                    break
                except OSError:
                    time.sleep(0.05)
            else:
                raise AssertionError("Configured Studio readiness timed out")
            live = next(p for p in providers if p["providerId"] == "openai.fixture")
            assert live["modelId"] == "fixture-model" and live["isDeterministic"] is False
            assert key not in json.dumps(providers) and endpoint not in json.dumps(providers)
            assert not state["calls"], "Listing providers must not create model requests"
            with sync_playwright() as playwright:
                options = {"headless":True}
                if browser_executable:
                    options["executable_path"] = browser_executable
                browser = playwright.chromium.launch(**options)
                try:
                    page = browser.new_page(viewport={"width":1440,"height":1100})
                    errors, destinations = [], []
                    page.on("pageerror", lambda error: errors.append(str(error)))
                    page.on("request", lambda request: destinations.append(request.url))
                    if template_only:
                        page.goto(base)
                        designer = page.get_by_role("region", name="Workflow designer", exact=True)
                        expect(designer.locator("[data-graph-save]")).to_be_enabled()
                        parallel_user_template_check(page, base, get, state, state_lock)
                        assert not errors, errors
                        print("Parallel Agent user-message template: edit/save/reload/direct-run/exact playback evidence passed.")
                        return
                    model_connection_form_check(page, base, endpoint, key, state, state_lock)
                    page.goto(base)
                    designer = page.get_by_role("region",name="Workflow designer",exact=True)
                    expect(workbench(designer).locator("[data-graph-save]")).to_be_enabled()
                    new_workflow_fixture(designer)
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
                    workflow_id = workbench(designer).locator(".vaw-definition-status code").inner_text()
                    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1",exact=True).select_option("agent:openai.fixture")
                    expect(workbench(designer).get_by_text("External model: fixture-model.",exact=False)).to_be_visible()
                    instructions = inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1",exact=True)
                    instructions.fill("Local protocol test instructions"); instructions.press("Tab")
                    inspect_control(designer, "Assign demo.reverse to step-1").get_by_label("Assign demo.reverse to step-1",exact=True).check()
                    save_draft = workbench(designer).locator("[data-graph-save]")
                    save_draft.click()
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
                    turns = inspect_control(designer, "Maximum model turns for step-1").get_by_label("Maximum model turns for step-1",exact=True)
                    turns.fill("17"); turns.press("Tab")
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
                    save_draft.click()
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
                    saved_turns = get(f"/api/workflows/{workflow_id}")["definition"]["nodes"]["step-1"]["agent"]["maxTurns"]
                    assert saved_turns == 17, f"Expected 17 turns after the first edit, got {saved_turns}"
                    turns.fill("-1"); turns.press("Tab")
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
                    timeout = inspect_control(designer, "Agent timeout in seconds for step-1").get_by_label("Agent timeout in seconds for step-1",exact=True)
                    timeout.fill("150"); timeout.press("Tab")
                    save_draft.click()
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Draft saved")
                    configured_draft = get(f"/api/workflows/{workflow_id}")["definition"]
                    assert configured_draft["schemaVersion"] == "1.21"
                    assert configured_draft["nodes"]["step-1"]["agent"]["maxTurns"] == -1
                    assert configured_draft["nodes"]["step-1"]["agent"]["timeoutSeconds"] == 150
                    # Publication is an API consumer proof; the IDE runs saved source directly.
                    saved = get(f"/api/workflows/{workflow_id}")
                    publication = page.request.post(base + f"/api/workflows/{workflow_id}/publish", data={"expectedGeneration": saved["generation"]})
                    assert publication.ok, publication.text()
                    revision = publication.json()["revision"]
                    workbench(designer).get_by_role("button",name="Configure test input",exact=True).click()
                    workbench(designer).get_by_label("Workflow input",exact=True).fill("abc")
                    workbench(designer).get_by_role("button",name="Run workflow",exact=True).click()
                    history = designer.get_by_label("Workflow run history", exact=True)
                    expect(history.locator("tbody tr").first).to_contain_text("completed", timeout=20000)
                    history.get_by_role("button",name=re.compile("^Inspect run ")).first.click()
                    viewer = page.get_by_role("region",name="Workflow run viewer",exact=True)
                    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
                    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("Protocol fixture: cba")
                    runs = get("/api/runs")
                    recorded = next(r for r in runs if r["header"]["definition"]["workflowId"] == workflow_id)
                    assert recorded["status"] == "completed"
                    assert sum(e["kind"] == "model.responded" for e in recorded["events"]) == 2
                    assert sum(e["kind"] == "tool.completed" for e in recorded["events"]) == 1
                    assert key not in json.dumps(recorded)
                    assert key not in page.content()
                    assert all(not url.startswith(endpoint) for url in destinations)
                    before = len(state["calls"])
                    viewer.get_by_role("button",name="Previous event",exact=True).click()
                    expect(viewer.get_by_text("Recorded playback",exact=True)).to_be_visible()
                    assert len(state["calls"]) == before
                    parallel_user_template_check(page, base, get, state, state_lock)
                    before = len(state["calls"])
                    # An HTTP rate-limit failure makes one attempt and records no credential/error body.
                    with state_lock:
                        state["status"] = 429
                    response = page.request.post(base + f"/api/revisions/{revision['revisionId']}/runs",data={"input":"failure"})
                    assert response.status == 202
                    failed_id = response.json()["header"]["runId"]
                    deadline = time.monotonic()+15
                    while time.monotonic()<deadline:
                        failure = get("/api/runs/"+failed_id)
                        if failure["isTerminal"]:
                            break
                        time.sleep(0.025)
                    assert failure["status"] == "failed"
                    assert len(state["calls"]) == before+1
                    assert key not in json.dumps(failure)
                    failure_data = json.loads(next(e["data"] for e in failure["events"] if e["kind"] == "agent.failed"))
                    assert failure_data["failureKind"] == "RateLimited" and failure_data["statusCode"] == 429
                    assert not errors, errors
                    # Export a no-tool agent artifact for a service with independent host registration.
                    definition = json.loads(json.dumps(revision["definition"]))
                    definition["workflowId"] = "service-agent"
                    definition["nodes"]["step-1"]["agent"]["tools"] = []
                    response = page.request.put(base + "/api/workflows/service-agent/draft",data={"definition":definition,"expectedGeneration":0})
                    assert response.ok, response.text()
                    package = get("/api/workflows/service-agent/package")
                    assert key not in json.dumps(package) and endpoint not in json.dumps(package)
                    (directory/"agent.package.json").write_text(json.dumps(package))
                    manifest = {"packageFile":"agent.package.json","revisionId":str(uuid.uuid4()),"definitionHash":package["definitionHash"],
                                "author":"provider-smoke","createdAt":"2026-10-24T00:00:00+00:00"}
                    (directory/"agent.deployment.json").write_text(json.dumps(manifest))
                finally:
                    browser.close()
            stop(process) # Service proof runs with Studio stopped.
            with state_lock:
                state["status"] = 200
            for mode in ("direct","queued"):
                result = subprocess.run([dotnet,str(ROOT/"samples/WorkflowService/bin/Release/net10.0/WorkflowService.dll"),mode,
                    str(directory/"agent.deployment.json"),str(directory/mode),"service input"],env=env,cwd=ROOT,capture_output=True,text=True,timeout=35)
                assert result.returncode==0,(result.returncode,result.stderr)
                final = json.loads(result.stdout.splitlines()[-1])
                assert final["output"]=="Protocol fixture: service input"
                assert final["revisionId"]==manifest["revisionId"]
                assert key not in result.stdout and key not in result.stderr
            changed_model = dict(env)
            changed_model[prefix+"Model"] = "fixture-model-after-edit"
            edited = subprocess.run([dotnet,str(ROOT/"samples/WorkflowService/bin/Release/net10.0/WorkflowService.dll"),"direct",
                str(directory/"agent.deployment.json"),str(directory/"edited-model"),"same workflow revision"],env=changed_model,cwd=ROOT,
                capture_output=True,text=True,timeout=15)
            assert edited.returncode == 0, edited.stderr
            edited_result = json.loads(edited.stdout.splitlines()[-1])
            assert edited_result["revisionId"] == manifest["revisionId"]
            assert state["calls"][-1]["model"] == "fixture-model-after-edit"
            assert key not in edited.stdout and key not in edited.stderr

            calls_before_rejection = len(state["calls"])
            changed = dict(env)
            changed[prefix+"Enabled"] = "false"
            journal = directory / "rejected-disabled-provider"
            rejected = subprocess.run([dotnet,str(ROOT/"samples/WorkflowService/bin/Release/net10.0/WorkflowService.dll"),"direct",
                str(directory/"agent.deployment.json"),str(journal),"must not execute"],env=changed,cwd=ROOT,capture_output=True,text=True,timeout=15)
            assert rejected.returncode == 1
            assert not journal.exists()
            assert len(state["calls"]) == calls_before_rejection
            assert key not in rejected.stderr
        finally:
            if process:
                stop(process)
            server.shutdown(); server.server_close()
    print("Configured official-SDK protocol fixture: model connection add/test/edit/delete, Studio authoring, brokered MCP loop, bounded evidence, no retry/credential capture, read-only playback and independent direct/queued service execution passed. No live model was contacted.")


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet",default="dotnet")
    parser.add_argument("--browser-executable")
    parser.add_argument("--template-only",action="store_true",help="Run only the isolated parallel Agent-template browser proof")
    args=parser.parse_args()
    check(args.dotnet,args.browser_executable,args.template_only)
