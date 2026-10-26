"""Exercise external project-folder changes and safe Studio reload behavior."""

import argparse
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def write_manifest(path: Path, project_name: str) -> None:
    manifest = {
        "formatVersion": "1.0",
        "projectId": "folder-change-smoke",
        "projectName": project_name,
        "files": [],
    }
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def verify(base_url: str, browser_executable: str | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-folder-change-") as temporary_directory:
        project_directory = Path(temporary_directory) / "project"
        project_directory.mkdir()
        manifest_path = project_directory / "vaw.project.json"
        write_manifest(manifest_path, "Folder change smoke project")

        with sync_playwright() as playwright:
            launch_options = {"headless": True}
            if browser_executable:
                launch_options["executable_path"] = browser_executable
            browser = playwright.chromium.launch(**launch_options)
            page = browser.new_page()
            errors: list[str] = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            try:
                response = page.goto(f"{base_url}/source-project", wait_until="networkidle")
                assert response and response.status == 200, "Project-folder page must load."
                page.get_by_label("Absolute folder path", exact=True).fill(str(project_directory))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_text("Opened Folder change smoke project.")).to_be_visible()
                page.reload(wait_until="networkidle")
                expect(page.get_by_text("Opened Folder change smoke project.")).to_be_visible()
                theme = page.get_by_role("combobox", name="Color theme", exact=True)
                theme.select_option(label="Light Modern")
                theme.select_option(label="Dark Modern")

                write_manifest(manifest_path, "External clean change")
                expect(page.get_by_role("alert")).to_contain_text("changed outside Studio", timeout=25000)
                page.get_by_role("button", name="Reload project files", exact=True).click()
                expect(page.get_by_role("status")).to_contain_text("Reloaded the latest validated project files.")
                expect(page.get_by_text("External clean change", exact=True)).to_be_visible()

                page.get_by_role("button", name="New workflow", exact=True).click()
                expect(page.get_by_text("Unsaved changes")).to_be_visible()
                write_manifest(manifest_path, "External dirty change")
                expect(page.get_by_role("alert")).to_contain_text("changed outside Studio", timeout=25000)
                expect(page.get_by_role("button", name="Discard local edits and reload folder", exact=True)).to_be_visible()
                expect(page.get_by_text("Unsaved changes")).to_be_visible()

                page.get_by_role("button", name="Discard local edits and reload folder", exact=True).click()
                expect(page.get_by_role("status")).to_contain_text("Reloaded the latest validated project files.")
                expect(page.get_by_text("External dirty change", exact=True)).to_be_visible()
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Folder external-change detection and safe reload passed.")


if __name__ == "__main__":
    main()
