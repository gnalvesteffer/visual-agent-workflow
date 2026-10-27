"""Verify multiple folder tabs retain separate editor buffers and guard dirty closes."""

import argparse
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def create_project(directory: Path, project_id: str, name: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "vaw.project.json").write_text(
        json.dumps({"formatVersion": "1.0", "projectId": project_id, "projectName": name, "files": []}, indent=2) + "\n",
        encoding="utf-8",
    )


def verify(base_url: str, browser_executable: str | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-multi-root-") as temporary_directory:
        root = Path(temporary_directory)
        first = root / "alpha"
        second = root / "beta"
        create_project(first, "multi-root-alpha", "Alpha project")
        create_project(second, "multi-root-beta", "Beta project")

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
                assert response and response.status == 200, "Project workspace must load."
                folder_form = page.locator(".vaw-open-project-details")
                if folder_form.count() and not folder_form.evaluate("e => e.open"):
                    folder_form.locator(":scope > summary").click()
                initial_tabs = page.locator(".vaw-project-tab [role=tab]").evaluate_all("items => items.map(item => item.id)")
                path_input = page.get_by_label("Absolute folder path", exact=True)
                path_input.fill(str(first))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_role("tab", name="Alpha project")).to_be_visible()
                page.get_by_role("button", name="Create project item", exact=True).click()
                page.get_by_role("menuitem", name="New file", exact=False).click()
                page.get_by_role("menuitem", name="Workflow .workflow", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-document-save-state")).to_contain_text("Unsaved changes")
                node_label = page.locator(".vaw-project-folder-editor:not([hidden])").get_by_label("Node label", exact=True)
                node_label.fill("Alpha request")
                node_label.press("Tab")

                if not folder_form.evaluate("e => e.open"):
                    folder_form.locator(":scope > summary").click()
                page.set_viewport_size({"width": 390, "height": 844})
                path_input.fill(str(second))
                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "Open Folder must fit on mobile"
                page.screenshot(path="/tmp/vaw-open-folder-popover.png")
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_role("tab", name="Beta project")).to_be_visible()
                expect(folder_form).not_to_have_attribute("open", "")
                page.set_viewport_size({"width": 1280, "height": 720})
                page.get_by_role("button", name="Create project item", exact=True).click()
                page.get_by_role("menuitem", name="New file", exact=False).click()
                page.get_by_role("menuitem", name="Workflow .workflow", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-document-save-state")).to_contain_text("Unsaved changes")
                node_label = page.locator(".vaw-project-folder-editor:not([hidden])").get_by_label("Node label", exact=True)
                node_label.fill("Beta request")
                node_label.press("Tab")

                create_project(first, "multi-root-alpha", "Alpha project changed externally")
                expect(page.locator(".vaw-project-tab.is-active [role=tab]")).to_contain_text("Beta project")
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-source-change")).to_have_count(0)
                page.get_by_role("tab", name="Alpha project").click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-source-change")).to_contain_text("changed outside Studio")
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-document-save-state")).to_contain_text("Unsaved changes")
                expect(page.locator(".vaw-project-folder-editor:not([hidden])").get_by_label("Node label", exact=True)).to_have_value("Alpha request")
                page.get_by_role("tab", name="Beta project").click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-document-save-state")).to_contain_text("Unsaved changes")
                expect(page.locator(".vaw-project-folder-editor:not([hidden])").get_by_label("Node label", exact=True)).to_have_value("Beta request")

                page.get_by_role("button", name="Close Alpha project", exact=True).click()
                expect(page.get_by_role("alertdialog")).to_contain_text("unsaved editor changes")
                page.get_by_role("button", name="Keep project open", exact=True).click()
                expect(page.get_by_role("tab", name="Alpha project")).to_be_visible()

                page.get_by_role("button", name="Close Alpha project", exact=True).click()
                page.get_by_role("button", name="Discard and close", exact=True).click()
                expect(page.get_by_role("tab", name="Alpha project")).to_have_count(0)
                expect(page.get_by_role("tab", name="Beta project")).to_be_visible()
                page.get_by_role("button", name="Close Beta project", exact=True).click()
                page.get_by_role("button", name="Discard and close", exact=True).click()
                expect(page.locator(".vaw-project-tab")).to_have_count(len(initial_tabs))
                assert page.locator(".vaw-project-tab [role=tab]").evaluate_all("items => items.map(item => item.id)") == initial_tabs, "Closing these projects must preserve unrelated tabs"
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Multiple project tabs, independent editor state, and dirty-close guard passed.")


if __name__ == "__main__":
    main()
