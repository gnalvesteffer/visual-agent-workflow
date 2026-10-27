"""Exercise immutable graph-function authoring in a folder-backed project."""

import argparse
import hashlib
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def verify(base_url: str, browser_executable: str | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-folder-function-") as temporary_directory:
        project_directory = Path(temporary_directory) / "project"
        project_directory.mkdir()
        manifest_path = project_directory / "vaw.project.json"
        manifest_path.write_text(json.dumps({
            "formatVersion": "1.0",
            "projectId": "function-smoke-project",
            "projectName": "Function smoke project",
            "files": [],
        }, indent=2) + "\n", encoding="utf-8")

        with sync_playwright() as playwright:
            launch_options = {"headless": True}
            if browser_executable:
                launch_options["executable_path"] = browser_executable
            browser = playwright.chromium.launch(**launch_options)
            page = browser.new_page(viewport={"width": 1600, "height": 1000})
            page.set_default_timeout(5000)
            errors: list[str] = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            try:
                response = page.goto(f"{base_url}/source-project", wait_until="networkidle")
                assert response and response.status == 200, "Project-folder page must load."
                expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
                expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
                expect(page.get_by_role("navigation", name="Studio navigation", exact=True)).to_have_count(0)
                open_folder = page.locator(".vaw-open-project-details")
                if open_folder.count():
                    open_folder.locator("summary").click()
                page.get_by_label("Absolute folder path", exact=True).fill(str(project_directory))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_role("heading", name="Function smoke project", exact=True)).to_be_visible()
                if open_folder.evaluate("e => e.open"):
                    open_folder.locator("summary").click()
                workspace = page.get_by_role("region", name="Project explorer and project editor", exact=True)
                expect(workspace.locator(".vaw-project-explorer")).to_be_visible()
                expect(workspace.locator(".vaw-folder-workflow-editor")).to_be_visible()
                expect(workspace.get_by_label("Project manifest vaw.project.json, read only", exact=True)).to_be_visible()
                assert workspace.evaluate("e => getComputedStyle(e).gridTemplateColumns.split(' ').length") == 2

                page.get_by_role("button", name="Create project item", exact=True).click()
                page.get_by_role("menuitem", name="New file", exact=False).click()
                page.get_by_role("menuitem", name="Function .function", exact=True).click()
                expect(page.get_by_label("Project graph function editor")).to_be_visible()
                expect(page.locator(".vaw-canvas-workspace")).to_be_visible()
                save_button = page.get_by_role("button", name="Save graph function revision", exact=True)
                expect(save_button).to_be_enabled()
                input_type = page.locator('section[aria-label="Function inputs"] .vaw-function-port select')
                output_type = page.locator('section[aria-label="Function outputs"] .vaw-function-port select')
                canvas = page.locator(".vaw-graph-editor")
                input_type.select_option("integer")
                expect(canvas.locator("li[data-node-id='input']")).to_contain_text("out · integer")
                input_type.select_option("string")
                output_type.select_option("integer")
                expect(canvas.locator("li[data-node-id='output']")).to_contain_text("in · integer")
                output_type.select_option("string")
                save_button.click()
                expect(page.get_by_text("Saved graph function revision text.append-suffix@1.", exact=True)).to_be_attached()

                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                asset = manifest["files"][0]
                assert asset["path"].endswith(".function"), asset["path"]
                content = (project_directory / asset["path"]).read_bytes()
                assert hashlib.sha256(content).hexdigest() == asset["sha256"], "The graph-function path must match its manifest hash."
                assert asset["kind"] == "graph-function" and asset["version"] == "1"
                expect(page.get_by_label("New version", exact=True)).to_have_value("2")
                expect(save_button).to_be_disabled()

                page.locator('.vaw-project-tree-file[aria-label^="Edit graph function "]').first.click()
                expect(page.get_by_label("New version", exact=True)).to_have_value("2")
                expect(page.get_by_label("Project graph function editor")).to_be_visible()

                page.get_by_role("button", name="Create project item", exact=True).click()
                page.get_by_role("menuitem", name="New file", exact=False).click()
                page.get_by_role("menuitem", name="Workflow .workflow", exact=True).click()
                expect(page.locator(".vaw-graph-editor")).to_be_visible()
                page.get_by_role("button", name="Save source file", exact=True).click()
                expect(page.get_by_text("Saved workflows/", exact=False)).to_be_attached()
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                workflow_file = next(item for item in manifest["files"] if item["kind"] == "workflow")
                assert workflow_file["path"].endswith(".workflow"), workflow_file["path"]

                page.locator('.vaw-project-tree-file[aria-label^="Edit graph function "]').first.click()
                tabs = page.get_by_role("navigation", name="Open project files", exact=True)
                expect(tabs.get_by_role("tab")).to_have_count(2)
                function_tab = tabs.locator('button[role="tab"][aria-label$=".function"]')
                expect(function_tab).to_contain_text(".function")
                function_tab.click()
                expect(page.get_by_label("Project graph function editor")).to_be_visible()
                tabs.locator('button[role="tab"][aria-label$=".workflow"]').click()
                expect(page.locator(".vaw-graph-editor")).to_be_visible()
                canvas = page.locator(".vaw-graph-editor")
                canvas.get_by_role("button", name="Fit graph", exact=True).click()
                viewport = canvas.locator(".vaw-canvas-viewport")
                expect(viewport.locator("li[data-node-id]")).to_have_count(4)
                page.wait_for_function("""() => {
                    const viewport = document.querySelector('.vaw-graph-editor .vaw-canvas-viewport');
                    const rect = viewport.getBoundingClientRect();
                    return [...viewport.querySelectorAll('li[data-node-id]')].every(node => {
                        const box = node.getBoundingClientRect();
                        return box.left >= rect.left && box.right <= rect.right && box.top >= rect.top && box.bottom <= rect.bottom;
                    });
                }""")
                details = page.get_by_role("navigation", name="Workflow detail tabs", exact=True)
                details.get_by_role("tab", name="Problems", exact=False).click()
                expect(page.get_by_role("tabpanel", name="Workflow issues", exact=True)).to_be_visible()
                details.get_by_role("tab", name="Output", exact=True).click()
                expect(page.get_by_role("tabpanel", name="Workflow output", exact=True)).to_be_visible()
                details.get_by_role("tab", name="Runs", exact=True).click()
                expect(page.get_by_role("tabpanel", name="Workflow run history", exact=True)).to_be_visible()
                assert page.locator(".vaw-app-shell").evaluate("e => Math.abs(e.getBoundingClientRect().height - innerHeight) < 2")
                page.evaluate("window.scrollTo(0, 0)")
                page.screenshot(path="/tmp/vaw-project-ide-smoke.png", full_page=False)
                page.set_viewport_size({"width": 390, "height": 844})
                expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
                expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) .vaw-canvas-viewport")).to_be_visible()
                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "The mobile project workspace must not overflow horizontally."
                page.screenshot(path="/tmp/vaw-project-ide-mobile.png", full_page=False)
                page.set_viewport_size({"width": 1600, "height": 1000})
                page.get_by_label("Color theme", exact=True).select_option("vs-code-dark-plus")
                expect(page.get_by_label("Color theme", exact=True)).to_have_value("vs-code-dark-plus")
                settings = page.locator(".vaw-host-settings")
                if not settings.evaluate("element => element.open"):
                    page.locator(".vaw-host-settings > summary").click()
                page.get_by_role("link", name="MCP connections", exact=True).click()
                expect(page.locator(".vaw-app-shell")).to_be_visible()
                expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
                expect(page.get_by_label("Color theme", exact=True)).to_have_value("vs-code-dark-plus")
                page.get_by_role("link", name="Open folder", exact=True).click()
                expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
                expect(page.get_by_label("Color theme", exact=True)).to_have_value("vs-code-dark-plus")
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Folder-backed graph-function authoring passed.")


if __name__ == "__main__":
    main()
