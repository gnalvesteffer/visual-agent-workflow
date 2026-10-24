"""Functional smoke check against an already running local development studio.

Uses only the standard library unless --browser is selected. Creates recorded test
runs in the studio's development journal; use a disposable local data directory.
"""

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
            expect(page.get_by_role("button", name="Start independent run", exact=True)).to_be_enabled()
            page.set_viewport_size({"width": 390, "height": 844})
            assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Mobile horizontal overflow"
            assert not errors, errors
            print("Browser run controls, output inspector, playback, reload, and mobile overflow checks passed.")
        finally:
            browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser", action="store_true", help="Also exercise real browser controls; requires Playwright and Chromium")
    parser.add_argument("--browser-executable", help="Optional path to an existing Chromium executable")
    arguments = parser.parse_args()
    api_check()
    catalog_check()
    if arguments.browser:
        browser_check(arguments.browser_executable)
