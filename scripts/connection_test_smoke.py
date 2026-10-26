"""Exercise saved and unsaved model/MCP connection test actions with local fixtures only."""

import argparse
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
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright

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
            process.kill()
            process.wait(timeout=5)


def request_json(base, path):
    with urlopen(base + path, timeout=5) as response:
        return json.load(response)


def run(dotnet, browser_executable, mcp_only=False):
    key = secrets.token_urlsafe(24)
    state = {"requests": [], "slow_started": threading.Event(), "slow_finished": threading.Event()}

    class CompletionFixture(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
            state["last_request"] = {"method": "POST", "path": self.path,
                                     "authorizationMatched": self.headers.get("Authorization") == "Bearer " + key,
                                     "model": body.get("model")}
            if self.path != "/v1/chat/completions" or not state["last_request"]["authorizationMatched"]:
                self.send_response(401 if not state["last_request"]["authorizationMatched"] else 404)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            state["requests"].append(body)
            if body.get("model") == "slow-fixture":
                state["slow_started"].set()
                time.sleep(1.5)
            payload = {"id": "test", "object": "chat.completion", "created": 1, "model": body["model"],
                       "choices": [{"index": 0, "message": {"role": "assistant", "content": "OK"}, "finish_reason": "stop"}],
                       "usage": {"prompt_tokens": 3, "completion_tokens": 1, "total_tokens": 4}}
            encoded = json.dumps(payload).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            try:
                self.wfile.write(encoded)
            except BrokenPipeError:
                pass  # The browser cancels this deliberately delayed request after an edit.
            finally:
                if body.get("model") == "slow-fixture":
                    state["slow_finished"].set()

        def do_GET(self):
            state["last_request"] = {"method": "GET", "path": self.path, "authorizationMatched": False}
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def handle_error(self, request, client_address):
            state["handler_error"] = "request handler failed"

    model_server = ThreadingHTTPServer(("127.0.0.1", 0), CompletionFixture)
    model_server.daemon_threads = True
    threading.Thread(target=model_server.serve_forever, daemon=True).start()
    endpoint = f"http://127.0.0.1:{model_server.server_port}/v1/"
    studio_port = unused_port()
    base = f"http://127.0.0.1:{studio_port}"
    process = None

    with tempfile.TemporaryDirectory(prefix="vaw-connection-tests-") as directory:
        env = dict(os.environ)
        env.update({"ASPNETCORE_ENVIRONMENT": "Development", "WorkflowStudio__Port": str(studio_port),
                    "WorkflowStudio__DataDirectory": str(Path(directory) / "studio"),
                    "HTTP_PROXY": "", "HTTPS_PROXY": "", "ALL_PROXY": "", "NO_PROXY": "*",
                    "http_proxy": "", "https_proxy": "", "all_proxy": "", "no_proxy": "*"})
        process = subprocess.Popen([dotnet, str(ROOT / "src/VisualAgentWorkflow.Studio/bin/Release/net10.0/VisualAgentWorkflow.Studio.dll")],
                                   cwd=ROOT / "src/VisualAgentWorkflow.Studio", env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                assert process.poll() is None, "Studio stopped before readiness"
                try:
                    request_json(base, "/api/settings/model-providers")
                    break
                except OSError:
                    time.sleep(0.05)
            else:
                raise AssertionError("Studio readiness timed out")

            launch = {"headless": True}
            if browser_executable:
                launch["executable_path"] = browser_executable
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(**launch)
                try:
                    page = browser.new_page(viewport={"width": 1440, "height": 1000})
                    page.set_default_timeout(7000)
                    errors = []
                    page.on("pageerror", lambda error: errors.append(str(error)))

                    def fill_and_wait(label, value):
                        field = page.get_by_label(label, exact=False)
                        field.fill(value)
                        # @bind uses onchange, so blur and allow the Server event to
                        # settle before the next edit. Otherwise a slower round trip
                        # can overwrite this edit with stale state.
                        field.press("Tab")
                        expect(field).to_have_value(value)
                        page.wait_for_timeout(250)

                    if not mcp_only:
                        page.goto(base + "/settings")
                        fill_and_wait("Connection name", "Local fixture")
                        fill_and_wait("Model ID", "fixture-model")
                        fill_and_wait("API base URL", endpoint)
                        fill_and_wait("Connection timeout", "4")
                        fill_and_wait("API key (optional)", key)
                        http_checkbox = page.get_by_label("Allow unencrypted HTTP for this endpoint", exact=False)
                        http_checkbox.check()
                        expect(http_checkbox).to_be_checked()
                        page.wait_for_timeout(250)
                        page.get_by_role("button", name="Test connection", exact=True).click()
                        try:
                            expect(page.locator(".vaw-provider-form-section [role=status]")).to_contain_text("Connection succeeded")
                        except AssertionError as failure:
                            raise AssertionError(f"Provider connection action failed; local fixture observed {state}: {failure}") from failure
                        assert request_json(base, "/api/settings/model-providers") == [], "The unsaved provider test must not persist settings"
                        call = state["requests"][-1]
                        assert call["model"] == "fixture-model"
                        assert call.get("max_completion_tokens", call.get("max_tokens")) == 1

                        page.locator(".vaw-provider-form-actions").get_by_role("button", name="Add connection", exact=True).click()
                        provider_card = page.locator(".vaw-provider-card").filter(has_text="openai.primary")
                        expect(provider_card).to_be_visible()
                        provider_card.get_by_role("button", name="Test connection", exact=True).click()
                        expect(provider_card.get_by_role("status")).to_contain_text("Connection succeeded")
                        saved_providers = json.dumps(request_json(base, "/api/settings/model-providers"))
                        assert "openai.primary" in saved_providers and key not in saved_providers
                        provider_catalog = request_json(base, "/api/agent-providers")
                        assert any(item["providerId"] == "openai.primary" and item["modelId"] == "fixture-model"
                                   for item in provider_catalog), \
                            f"A saved model connection must be exposed by the agent provider catalog: {provider_catalog}"
                        assert len(state["requests"]) == 2

                        # Verify the same saved connection reaches the actual project editor,
                        # not just the settings store or its test-request path.
                        page.goto(base + "/projects/default")
                        page.get_by_role("heading", name="Workflow designer", exact=True).wait_for()
                        page.locator("select[aria-label='New node operation'] option[value='agent:openai.primary']").first.wait_for(state="attached")

                        page.goto(base + "/settings")
                        provider_card.get_by_role("button", name="Edit connection", exact=True).click()
                        expect(page.get_by_role("heading", name="Edit model connection", exact=True)).to_be_visible()
                        expect(page.get_by_label("Model ID", exact=False)).to_have_value("fixture-model")
                        fill_and_wait("Model ID", "slow-fixture")
                        page.locator(".vaw-provider-form-actions").get_by_role("button", name="Test connection", exact=True).click()
                        assert state["slow_started"].wait(timeout=5), "The delayed fixture did not receive the test request"
                        save_button = page.locator(".vaw-provider-form-actions").get_by_role("button", name="Save connection", exact=True)
                        expect(save_button).to_be_disabled()
                        fill_and_wait("Model ID", "edited-after-test-start")
                        assert state["slow_finished"].wait(timeout=5), "The delayed fixture did not finish"
                        page.wait_for_timeout(300)
                        expect(page.locator(".vaw-provider-form-section [role=status]")).to_have_count(0)

                    page.goto(base + "/mcp")
                    expect(page.get_by_role("heading", name="MCP library")).to_be_visible()
                    page.wait_for_timeout(500)
                    fill_and_wait("Server ID", "fixture.stdio")
                    fill_and_wait("Display name", "Local stdio fixture")
                    page.get_by_role("combobox", name="Connection type", exact=True).select_option("Stdio")
                    page.wait_for_timeout(250)
                    expect(page.get_by_label("Executable", exact=True)).to_be_visible()
                    fill_and_wait("Executable", dotnet)
                    fixture_assembly = ROOT / "src/VisualAgentWorkflow.Studio/bin/Release/net10.0/VisualAgentWorkflow.Studio.dll"
                    fill_and_wait("Arguments", str(fixture_assembly) + "\n--mcp-demo-server")
                    page.get_by_role("button", name="Test connection & discover tools", exact=True).click()
                    expect(page.locator("section[aria-label='MCP server setup'] [role=status]")).to_contain_text("supported")
                    assert not any(item["bindingId"].startswith("fixture.stdio.") for item in request_json(base, "/api/tool-bindings")), \
                        "Testing an unsaved MCP connection must not register its tools"
                    page.locator(".vaw-mcp-tool-choice input[type=checkbox]").first.check()
                    page.get_by_role("button", name="Save server and selected tools", exact=True).click()
                    server_card = page.locator(".vaw-provider-card").filter(has_text="fixture.stdio")
                    expect(server_card).to_be_visible()
                    server_card.get_by_role("button", name="Test connection", exact=True).click()
                    expect(server_card.get_by_role("status")).to_contain_text("Connection succeeded")
                    assert any(item["bindingId"].startswith("fixture.stdio.") for item in request_json(base, "/api/tool-bindings"))
                    assert not errors, errors
                finally:
                    browser.close()
        finally:
            stop(process)
            model_server.shutdown()
            model_server.server_close()

    print("MCP setup/saved transport tests passed with local fixtures." if mcp_only else
          "Model setup/saved connection tests and MCP setup/saved transport tests passed with local fixtures; no live provider was contacted.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--browser-executable")
    parser.add_argument("--mcp-only", action="store_true", help="Skip model tests and exercise MCP setup and saved-entry actions only.")
    args = parser.parse_args()
    run(args.dotnet, args.browser_executable, args.mcp_only)
