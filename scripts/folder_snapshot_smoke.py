"""Exercise local project restore-point capture, preview, and manifest-pointer restore."""

from workbench_browser import current_project

import argparse
import contextlib
import hashlib
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def write_manifest(path: Path, files: list[dict]) -> None:
    manifest = {
        "formatVersion": "1.0",
        "projectId": "folder-snapshot-smoke",
        "projectName": "Folder snapshot smoke project",
        "files": files,
    }
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def add_skill(project_directory: Path) -> dict:
    instructions = "A disposable skill used to verify project snapshot restore."
    content = {
        "skill": {
            "id": "snapshot-smoke-skill",
            "version": "1.0.0",
            "name": "Snapshot smoke skill",
            "instructions": instructions,
            "contentHash": hashlib.sha256(instructions.encode()).hexdigest(),
        },
        "folder": "",
    }
    bytes_content = (json.dumps(content, indent=2) + "\n").encode()
    content_hash = hashlib.sha256(bytes_content).hexdigest()
    relative_path = f"skills/snapshot-smoke-skill/1.0.0-{content_hash}.json"
    asset_path = project_directory / relative_path
    asset_path.parent.mkdir(parents=True, exist_ok=True)
    asset_path.write_bytes(bytes_content)
    return {
        "kind": "skill",
        "id": "snapshot-smoke-skill",
        "version": "1.0.0",
        "path": relative_path,
        "sha256": content_hash,
    }


def verify(base_url: str, browser_executable: str | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-folder-snapshot-") as temporary_directory:
        project_directory = Path(temporary_directory) / "project"
        project_directory.mkdir()
        manifest_path = project_directory / "vaw.project.json"
        write_manifest(manifest_path, [])

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
                folder_form = page.locator(".vaw-open-project-details")
                if folder_form.count() and not folder_form.evaluate("e => e.open"):
                    folder_form.locator(":scope > summary").click()
                page.get_by_label("Absolute folder path", exact=True).fill(str(project_directory))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(current_project(page)).to_have_text("Folder snapshot smoke project")
                page.get_by_role("button", name="Project tools", exact=True).click()
                utility = page.locator('details.vaw-project-utility[aria-label="Local restore points"]')
                utility.locator(":scope > summary").click()
                expect(page.get_by_role("region", name="Local restore points")).to_be_visible()
                page.get_by_role("button", name="Create restore point", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-folder-feedback")).to_contain_text("Created local restore point")
                expect(page.get_by_text("No local restore points yet.")).not_to_be_visible()
                assert "/.vaw/" in (project_directory / ".gitignore").read_text(encoding="utf-8")

                current_asset = add_skill(project_directory)
                write_manifest(manifest_path, [current_asset])
                expect(page.get_by_role("alert")).to_contain_text("changed outside Studio", timeout=25000)
                page.get_by_role("button", name="Reload project files", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-folder-feedback")).to_contain_text("Reloaded the latest validated project files.")
                page.get_by_role("button", name="Preview restore", exact=True).click()
                preview = page.get_by_role("group", name="Restore preview")
                expect(preview).to_contain_text("0 files will become indexed, 1 will leave the manifest")
                page.get_by_role("button", name="Restore this project snapshot", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-folder-feedback")).to_contain_text("Restored project source from")

                restored = json.loads(manifest_path.read_text(encoding="utf-8"))
                assert restored["files"] == [], "Restore must switch the source manifest to its captured state."
                assert (project_directory / current_asset["path"]).is_file(), "Newer immutable files must remain on disk."
                assert not errors, errors
            except Exception:
                with contextlib.suppress(Exception):
                    page.screenshot(path="/tmp/vaw-folder-snapshot-failure.png", timeout=5000)
                raise
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Folder restore-point capture, preview, and source restore passed.")


if __name__ == "__main__":
    main()
