"""Export selected folder workflows and execute the bundle in the standalone consumer."""

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
import zipfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

ROOT = Path(__file__).resolve().parent.parent
SAMPLE = ROOT / "samples/WorkflowService"


def write_project(project_directory: Path) -> None:
    indexed_files = []
    for source_name, workflow_id in (("pure", "service-pure"), ("mcp", "service-mcp")):
        source_package = json.loads((SAMPLE / f"artifacts/{source_name}.package.json").read_text(encoding="utf-8"))
        source = {"formatVersion": "1.0", "definition": source_package["definition"], "layout": source_package["layout"]}
        relative_path = f"workflows/{workflow_id}.json"
        target = project_directory / relative_path
        target.parent.mkdir(parents=True, exist_ok=True)
        encoded = (json.dumps(source, indent=2) + "\n").encode()
        target.write_bytes(encoded)
        indexed_files.append({
            "kind": "workflow",
            "id": workflow_id,
            "version": "draft",
            "path": relative_path,
            "sha256": hashlib.sha256(encoded).hexdigest(),
        })
    draft_package = json.loads((SAMPLE / "artifacts/pure.package.json").read_text(encoding="utf-8"))
    draft_package["definition"]["workflowId"] = "draft-unfinished"
    draft_package["definition"]["connections"] = {}
    unfinished = {"formatVersion": "1.0", "definition": draft_package["definition"], "layout": draft_package["layout"]}
    draft_path = "workflows/draft-unfinished.json"
    draft_bytes = (json.dumps(unfinished, indent=2) + "\n").encode()
    (project_directory / draft_path).write_bytes(draft_bytes)
    indexed_files.append({
        "kind": "workflow",
        "id": "draft-unfinished",
        "version": "draft",
        "path": draft_path,
        "sha256": hashlib.sha256(draft_bytes).hexdigest(),
    })
    manifest = {
        "formatVersion": "1.0",
        "projectId": "folder-release-smoke",
        "projectName": "Folder release smoke project",
        "files": indexed_files,
    }
    (project_directory / "vaw.project.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def verify(base_url: str, browser_executable: str | None, dotnet: str) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-folder-release-") as temporary_directory:
        temp = Path(temporary_directory)
        project_directory = temp / "source"
        project_directory.mkdir()
        write_project(project_directory)

        with sync_playwright() as playwright:
            launch_options = {"headless": True}
            if browser_executable:
                launch_options["executable_path"] = browser_executable
            browser = playwright.chromium.launch(**launch_options)
            page = browser.new_page(accept_downloads=True)
            errors: list[str] = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            try:
                response = page.goto(f"{base_url}/source-project", wait_until="networkidle")
                assert response and response.status == 200, "Project-folder page must load."
                folder_form = page.locator(".vaw-open-project-details")
                if folder_form.count() and not folder_form.evaluate("e => e.open"):
                    folder_form.locator(":scope > summary").click()
                page.get_by_label("Absolute folder path", exact=True).fill(str(project_directory))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_label("Current project", exact=True).locator("option:checked")).to_have_text("Folder release smoke project")
                page.get_by_role("button", name="Project tools", exact=True).click()
                utility = page.locator('details.vaw-project-utility[aria-label="Build a consumer release"]')
                utility.locator(":scope > summary").click()
                expect(page.get_by_role("region", name="Build a consumer release")).to_be_visible()

                page.locator('input[name="workflowId"][value="service-pure"]').check()
                page.locator('input[name="workflowId"][value="service-mcp"]').check()
                with page.expect_download() as download_info:
                    page.get_by_role("button", name="Download selected workflows", exact=True).click()
                release_path = temp / "selected-release.vawpkg"
                download_info.value.save_as(release_path)

                with zipfile.ZipFile(release_path) as release:
                    manifest = json.loads(release.read("vaw.release.json"))
                    assert [item["workflowId"] for item in manifest["workflows"]] == ["service-mcp", "service-pure"]
                    assert len(release.namelist()) == 3, "The release should contain only its manifest and two selected workflow packages."
                    assert "draft-unfinished" not in {item["workflowId"] for item in manifest["workflows"]}
                    assert not any(".vaw/" in name or ".git" in name for name in release.namelist())
                    assert any(item["kind"] == "mcp-tool" and item["id"] == "demo.reverse" for item in manifest["requirements"])

                consumer = SAMPLE / "bin/Release/net10.0/WorkflowService.dll"
                pure_journal = temp / "consumer-pure"
                result = subprocess.run([dotnet, str(consumer), "bundle", "direct", str(release_path), "service-pure",
                                         str(pure_journal), "abc"], capture_output=True, text=True, timeout=40)
                assert result.returncode == 0, (result.stdout, result.stderr)
                completed = json.loads(result.stdout.splitlines()[-1])
                assert completed["status"] == "completed" and completed["output"] == "CBA"
                assert completed["contentHash"] == manifest["workflows"][1]["definitionHash"]

                denied_journal = temp / "consumer-denied"
                denied = subprocess.run([dotnet, str(consumer), "bundle", "direct", str(release_path), "service-mcp",
                                         str(denied_journal), "abc"], capture_output=True, text=True, timeout=40)
                assert denied.returncode == 1 and "ToolAccessDeniedException." in denied.stderr
                assert not denied_journal.exists(), "A bundle requirement must not grant host policy or admit a denied run."

                granted_journal = temp / "consumer-granted"
                granted = subprocess.run([dotnet, str(consumer), "bundle", "direct", str(release_path), "service-mcp",
                                          str(granted_journal), "abc", "--grant-demo-tool"],
                                         capture_output=True, text=True, timeout=40)
                assert granted.returncode == 0, (granted.stdout, granted.stderr)
                mcp_result = json.loads(granted.stdout.splitlines()[-1])
                assert mcp_result["status"] == "completed" and mcp_result["output"] == "cba"
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    parser.add_argument("--dotnet", default="dotnet")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable, arguments.dotnet)
    print("Selected folder workflow export, standalone clean-consumer execution, and host-owned MCP authorization passed.")


if __name__ == "__main__":
    main()
