"""Check host-configured official-SDK providers using a local protocol fixture.

This is NOT live OpenAI validation. Starts only owned loopback fixture/Studio
processes, uses ephemeral credentials, and executes deterministic local responses.
"""

from workbench_browser import workbench, inspect_control, new_workflow_fixture, show_runs
import argparse
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


def check(dotnet, browser_executable):
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
                    page.goto(base)
                    designer = page.get_by_role("region",name="Workflow designer",exact=True)
                    expect(workbench(designer).get_by_role("button",name="New workflow",exact=True)).to_be_enabled()
                    new_workflow_fixture(designer)
                    expect(workbench(designer).locator(".vaw-definition-status")).to_contain_text("Unsaved changes")
                    workflow_id = workbench(designer).locator(".vaw-definition-status strong").inner_text()
                    inspect_control(designer, "Operation for step-1").get_by_label("Operation for step-1",exact=True).select_option("agent:openai.fixture")
                    expect(workbench(designer).get_by_text("External model: fixture-model.",exact=False)).to_be_visible()
                    instructions = inspect_control(designer, "Agent instructions for step-1").get_by_label("Agent instructions for step-1",exact=True)
                    instructions.fill("Local protocol test instructions"); instructions.press("Tab")
                    inspect_control(designer, "Assign demo.reverse to step-1").get_by_label("Assign demo.reverse to step-1",exact=True).check()
                    turns = inspect_control(designer, "Maximum model turns for step-1").get_by_label("Maximum model turns for step-1",exact=True)
                    turns.fill("17"); turns.press("Tab")
                    turns.fill("-1"); turns.press("Tab")
                    timeout = inspect_control(designer, "Agent timeout in seconds for step-1").get_by_label("Agent timeout in seconds for step-1",exact=True)
                    timeout.fill("150"); timeout.press("Tab")
                    workbench(designer).get_by_role("button",name="Save draft",exact=True).click()
                    workbench(designer).get_by_text("Draft saved.",exact=True).wait_for()
                    configured_draft = get(f"/api/workflows/{workflow_id}")["definition"]
                    assert configured_draft["schemaVersion"] == "1.21"
                    assert configured_draft["nodes"]["step-1"]["agent"]["maxTurns"] == -1
                    assert configured_draft["nodes"]["step-1"]["agent"]["timeoutSeconds"] == 150
                    workbench(designer).get_by_role("button",name="Publish & activate",exact=True).click()
                    workbench(designer).get_by_text("Published and activated version 1.",exact=True).wait_for()
                    revision = get(f"/api/workflows/{workflow_id}/revisions")[0]["revision"]
                    workbench(designer).get_by_label("Published run input",exact=True).fill("abc")
                    workbench(designer).get_by_role("button",name="Run selected version",exact=True).click()
                    viewer = page.get_by_role("region",name="Workflow run viewer",exact=True)
                    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
                    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("Protocol fixture: cba")
                    runs = get("/api/runs")
                    recorded = next(r for r in runs if r["header"]["revisionId"] == revision["revisionId"])
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
    print("Configured official-SDK protocol fixture: Studio catalog/authoring, brokered MCP loop, bounded evidence, no retry/credential capture, read-only playback and independent direct/queued service execution passed. No live model was contacted.")


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet",default="dotnet")
    parser.add_argument("--browser-executable")
    args=parser.parse_args()
    check(args.dotnet,args.browser_executable)
