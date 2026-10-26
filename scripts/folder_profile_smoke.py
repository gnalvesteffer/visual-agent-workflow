"""Exercise project-folder agent profile authoring in a running local Studio."""

import argparse
import hashlib
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def verify(base_url: str, browser_executable: str | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-folder-profile-") as temporary_directory:
        project_directory = Path(temporary_directory) / "project"
        project_directory.mkdir()
        manifest_path = project_directory / "vaw.project.json"
        manifest_path.write_text(json.dumps({
            "formatVersion": "1.0",
            "projectId": "profile-smoke-project",
            "projectName": "Profile smoke project",
            "files": [],
        }, indent=2) + "\n", encoding="utf-8")

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
                expect(page.get_by_text("Opened Profile smoke project.")).to_be_visible()

                page.get_by_role("button", name="New agent profile", exact=True).click()
                page.get_by_label("Profile ID", exact=True).fill("reviewer")
                page.get_by_label("New version", exact=True).fill("1.0.0")
                page.get_by_label("Name", exact=True).fill("Reviewer")
                page.get_by_label("Delegate description", exact=True).fill("Reviews a request and returns findings.")
                page.get_by_label("Model connection ID", exact=True).fill("model.unconfigured")
                page.get_by_label("System instructions", exact=True).fill("Review the supplied request.")
                save_button = page.get_by_role("button", name="Save source profile version", exact=True)
                expect(save_button).to_be_enabled()
                save_button.click()
                expect(page.get_by_role("status")).to_contain_text("Saved agent profile revision reviewer@1.0.0.")

                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                assert len(manifest["files"]) == 1, "Saving a profile should add one project asset."
                asset = manifest["files"][0]
                content = (project_directory / asset["path"]).read_bytes()
                assert hashlib.sha256(content).hexdigest() == asset["sha256"], "The profile path must match its manifest hash."

                page.locator('.vaw-project-tree-file[aria-label^="Edit agent profile "]').first.click()
                expect(page.get_by_label("New version", exact=True)).to_have_value("1.0.1")
                expect(page.get_by_label("Model connection ID", exact=True)).to_have_value("model.unconfigured")
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Folder-backed agent profile authoring passed.")


if __name__ == "__main__":
    main()
