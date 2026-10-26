"""Exercise opt-in consumer observation and the reusable graph through Studio.

Starts and stops only its own consumer/Studio child processes on unused loopback
ports, with ephemeral server-side credentials and disposable journals.
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parent.parent


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(url, token=None, expected=200):
    headers = {"Authorization": "Bearer " + token} if token else {}
    try:
        response = urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=3)
    except urllib.error.HTTPError as error:
        assert error.code == expected, (url, error.code, expected)
        return None
    with response:
        assert response.status == expected, (url, response.status, expected)
        return json.load(response)


def wait_ready(url, process):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        assert process.poll() is None, "Child host exited before becoming ready"
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            time.sleep(0.05)
    raise AssertionError("Child host readiness timed out")


def stop(process):
    if process and process.poll() is None:
        process.send_signal(signal.SIGTERM)
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def check(dotnet, browser_executable, screenshots):
    consumer_port, studio_port = free_port(), free_port()
    while consumer_port == studio_port:
        studio_port = free_port()
    consumer_base = f"http://127.0.0.1:{consumer_port}"
    studio_base = f"http://127.0.0.1:{studio_port}"
    token = secrets.token_urlsafe(32)
    processes = []
    with tempfile.TemporaryDirectory(prefix="vaw-observation-") as temporary:
        directory = Path(temporary)
        consumer_command = [dotnet, str(ROOT / "samples/WorkflowService/bin/Release/net10.0/WorkflowService.dll"), "serve",
                            str(ROOT / "samples/WorkflowService/artifacts/pure.deployment.json"), str(directory / "runs"), "consumer visibility"]
        env = dict(os.environ)
        env["WorkflowService__Port"] = str(consumer_port)
        env["WorkflowObservation__Enabled"] = "false"

        def launch(command, environment, cwd):
            process = subprocess.Popen(command, cwd=cwd, env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            processes.append(process)
            return process

        try:
            disabled = launch(consumer_command, env, ROOT)
            wait_ready(consumer_base + "/health", disabled)
            request(consumer_base + "/workflow-observation/runs", expected=404)
            stop(disabled)
            env["WorkflowObservation__Enabled"] = "true"
            env["WorkflowObservation__Token"] = token
            consumer = launch(consumer_command, env, ROOT)
            wait_ready(consumer_base + "/health", consumer)
            request(consumer_base + "/workflow-observation/runs", expected=401)
            request(consumer_base + "/workflow-observation/runs", token="incorrect", expected=401)
            studio_env = dict(os.environ)
            studio_env["ASPNETCORE_ENVIRONMENT"] = "Development"
            studio_env["WorkflowStudio__Port"] = str(studio_port)
            studio_env["WorkflowStudio__DataDirectory"] = str(directory / "studio")
            studio_env["WorkflowObservation__Sources__consumer__BaseAddress"] = consumer_base + "/workflow-observation/"
            studio_env["WorkflowObservation__Sources__consumer__BearerToken"] = token
            studio_env["WorkflowObservation__Sources__denied__BaseAddress"] = consumer_base + "/workflow-observation/"
            studio_env["WorkflowObservation__Sources__denied__BearerToken"] = "incorrect"
            studio = launch([dotnet, str(ROOT / "src/VisualAgentWorkflow.Studio/bin/Release/net10.0/VisualAgentWorkflow.Studio.dll")],
                            studio_env, ROOT / "src/VisualAgentWorkflow.Studio")
            wait_ready(studio_base + "/observe", studio)
            with sync_playwright() as playwright:
                options = {"headless": True}
                if browser_executable:
                    options["executable_path"] = browser_executable
                browser = playwright.chromium.launch(**options)
                try:
                    page = browser.new_page(viewport={"width":1440,"height":1000})
                    errors, browser_destinations = [], []
                    page.on("pageerror", lambda error: errors.append(str(error)))
                    page.on("request", lambda req: browser_destinations.append(req.url))
                    page.goto(studio_base + "/observe")
                    expect(page.get_by_role("button", name="Connect to consumer", exact=True)).to_be_enabled()
                    page.get_by_label("Consumer source", exact=True).select_option("consumer")
                    page.get_by_role("button", name="Connect to consumer", exact=True).click()
                    panel = page.get_by_role("region", name="Workflow observation", exact=True)
                    viewer = panel.get_by_role("region", name="Workflow run viewer", exact=True)
                    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
                    expect(viewer.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("YTILIBISIV REMUSNOC")
                    assert panel.get_by_role("button", name="Cancel selected run").count() == 0
                    assert panel.get_by_role("button", name="Start independent run").count() == 0
                    assert panel.get_by_role("button", name="Save draft").count() == 0
                    assessment = panel.get_by_role("region", name="Run recovery assessment", exact=True)
                    expect(assessment).to_contain_text("The run is terminal")
                    assert assessment.get_by_role("button").count() == 0
                    assert token not in page.content()
                    summaries = request(consumer_base + "/workflow-observation/runs", token)
                    run_id = summaries[0]["runId"]
                    before = request(consumer_base + f"/workflow-observation/runs/{run_id}", token)
                    report_path = consumer_base + f"/workflow-observation/runs/{run_id}/reconciliation"
                    request(report_path, expected=401)
                    report = request(report_path, token)
                    assert report["lastSequence"] == len(before["events"]) and report["canResume"] is False
                    assert "consumer visibility" not in json.dumps(report) and "native" not in json.dumps(report).lower()
                    viewer.get_by_role("button", name="Previous event", exact=True).click()
                    expect(viewer.get_by_text("Recorded playback", exact=True)).to_be_visible()
                    expect(assessment).to_contain_text("independent of the playback position")
                    assert request(consumer_base + f"/workflow-observation/runs/{run_id}", token) == before
                    if screenshots:
                        screenshots.mkdir(parents=True, exist_ok=True)
                        page.screenshot(path=str(screenshots / "consumer-observation.png"), full_page=True)
                    # Replacing the source must remove the old source's graph/payloads.
                    page.get_by_label("Consumer source", exact=True).select_option("denied")
                    page.get_by_role("button", name="Connect to consumer", exact=True).click()
                    expect(panel.get_by_role("alert")).to_contain_text("Observation unavailable")
                    expect(viewer.locator('li[data-node-id="output"]')).to_have_count(0)
                    page.get_by_label("Consumer source", exact=True).select_option("consumer")
                    page.get_by_role("button", name="Connect to consumer", exact=True).click()
                    expect(viewer.locator('li[data-node-id="output"]')).to_be_visible()
                    stop(consumer)
                    expect(panel.get_by_role("alert")).to_contain_text("last received evidence", timeout=15000)
                    expect(assessment).to_contain_text("Assessment unavailable while the connection is stale")
                    expect(viewer.locator('li[data-node-id="output"]')).to_be_visible()
                    consumer = launch(consumer_command, env, ROOT)
                    wait_ready(consumer_base + "/health", consumer)
                    expect(panel.get_by_role("alert")).to_have_count(0, timeout=15000)
                    assert request(consumer_base + f"/workflow-observation/runs/{run_id}", token) == before
                    page.get_by_role("button", name="Disconnect observation", exact=True).click()
                    expect(panel).to_have_count(0)
                    assert request(consumer_base + f"/workflow-observation/runs/{run_id}", token) == before
                    assert all(not url.startswith(consumer_base) for url in browser_destinations), "Browser contacted consumer directly"
                    assert not errors, errors
                finally:
                    browser.close()
        finally:
            for process in reversed(processes):
                stop(process)
    print("Opt-in consumer API, credential denial, server-side Studio attachment, read-only graph/payload playback, source replacement, disconnect and restart/reconnect passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots", type=Path)
    args = parser.parse_args()
    check(args.dotnet, args.browser_executable, args.screenshots)
