"""Exercise saved and restore-point workflow runs from the Studio browser UI."""

import argparse
import hashlib
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


ROOT = Path(__file__).resolve().parent.parent


def source_asset(package: dict, *, reverse: bool) -> bytes:
    definition = package["definition"]
    layout = package["layout"]
    if not reverse:
        del definition["nodes"]["step-2"]
        del definition["connections"]["link-2"]
        definition["connections"]["link-output"]["source"]["nodeId"] = "step-1"
        del layout["nodes"]["step-2"]
    return (json.dumps({"formatVersion": "1.0", "definition": definition, "layout": layout}, indent=2) + "\n").encode()


def write_project(project: Path, package: dict, *, reverse: bool) -> tuple[Path, str]:
    workflow_id = package["definition"]["workflowId"]
    content = source_asset(json.loads(json.dumps(package)), reverse=reverse)
    asset_hash = hashlib.sha256(content).hexdigest()
    workflow_hash = hashlib.sha256(workflow_id.encode()).hexdigest()[:8]
    relative_path = f"workflows/{workflow_id}-{workflow_hash}-{asset_hash[:16]}.json"
    path = project / relative_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)
    manifest = {
        "formatVersion": "1.0",
        "projectId": "folder-execution-smoke",
        "projectName": "Folder execution smoke project",
        "files": [{
            "kind": "workflow",
            "id": workflow_id,
            "version": "draft",
            "path": relative_path,
            "sha256": asset_hash,
        }],
    }
    manifest_path = project / "vaw.project.json"
    temporary = manifest_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(manifest_path)
    return path, asset_hash


def verify(base_url: str, browser_executable: str | None) -> None:
    package = json.loads((ROOT / "samples/WorkflowService/artifacts/pure.package.json").read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="vaw-folder-execution-") as temporary_directory:
        project = Path(temporary_directory) / "project"
        project.mkdir()
        asset_path, saved_asset_hash = write_project(project, package, reverse=True)

        with sync_playwright() as playwright:
            options = {"headless": True}
            if browser_executable:
                options["executable_path"] = browser_executable
            browser = playwright.chromium.launch(**options)
            page = browser.new_page()
            page_errors: list[str] = []
            page.on("pageerror", lambda error: page_errors.append(str(error)))
            try:
                response = page.goto(f"{base_url}/source-project", wait_until="networkidle")
                assert response and response.status == 200, "Project-folder page must load."
                page.get_by_label("Absolute folder path", exact=True).fill(str(project))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_text("Opened Folder execution smoke project.")).to_be_visible()
                page.get_by_role("button", name="Open workflow service-pure", exact=True).click()

                saved_run = page.get_by_role("region", name="Run saved workflow")
                saved_run.get_by_label("Workflow input", exact=True).fill("Hello folder!")
                saved_run.get_by_role("button", name="Run saved workflow", exact=True).click()
                expect(saved_run).to_contain_text("completed", timeout=20_000)
                expect(saved_run).to_contain_text("!REDLOF OLLEH")
                expect(saved_run).to_contain_text(saved_asset_hash[:12])
                expect(page.get_by_role("alert").filter(has_text="empty string")).to_have_count(0)
                expect(page.get_by_role("alert").filter(has_text="layout edit could not reach the editor")).to_have_count(0)

                page.get_by_role("button", name="Create restore point", exact=True).click()
                expect(page.get_by_role("status")).to_contain_text("Created local restore point")
                snapshot_row = page.locator(".vaw-snapshot-list li").first

                # Replace the active source with uppercase-only while the restore point retains
                # uppercase-plus-reverse. The snapshot run must use its archived bytes in memory.
                _, current_asset_hash = write_project(project, package, reverse=False)
                expect(page.get_by_role("alert")).to_contain_text("changed outside Studio", timeout=25_000)
                page.get_by_role("button", name="Reload project files", exact=True).click()
                expect(page.get_by_role("status")).to_contain_text("Reloaded the latest validated project files.")
                page.get_by_role("button", name="Open workflow service-pure", exact=True).click()
                saved_run.get_by_label("Workflow input", exact=True).fill("Hello folder!")
                saved_run.get_by_role("button", name="Run saved workflow", exact=True).click()
                expect(saved_run).to_contain_text("completed", timeout=20_000)
                expect(saved_run).to_contain_text("HELLO FOLDER!")
                expect(saved_run).to_contain_text(current_asset_hash[:12])

                snapshot_row.get_by_role("button", name="Test selected workflow", exact=True).click()
                expect(saved_run).to_contain_text("!REDLOF OLLEH", timeout=20_000)
                expect(saved_run).to_contain_text(saved_asset_hash[:12])
                assert json.loads((project / "vaw.project.json").read_text(encoding="utf-8"))["files"][0]["sha256"] == current_asset_hash
                assert not (project / ".vaw" / "runs").exists(), "Execution evidence must stay outside the source project."
                assert not page_errors, page_errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Saved and restore-point folder workflow execution passed in the Studio UI.")


if __name__ == "__main__":
    main()
