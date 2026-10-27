"""Exercise Monaco-backed project Markdown editing, save/reopen, themes, and layout."""

import argparse
import json
import os
import tempfile
import uuid
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


def verify(base_url: str, browser_executable: str | None, screenshot_directory: Path | None) -> None:
    with tempfile.TemporaryDirectory(prefix="vaw-markdown-editor-") as temporary_directory:
        project = Path(temporary_directory) / "project"
        project.mkdir()
        project_id = f"markdown-editor-{uuid.uuid4().hex}"
        project_name = f"Markdown editor smoke {uuid.uuid4().hex[:8]}"
        (project / "vaw.project.json").write_text(json.dumps({
            "formatVersion": "1.0",
            "projectId": project_id,
            "projectName": project_name,
            "files": [],
        }, indent=2) + "\n", encoding="utf-8")

        with sync_playwright() as playwright:
            options = {"headless": True}
            if browser_executable:
                options["executable_path"] = browser_executable
            browser = playwright.chromium.launch(**options)
            page = browser.new_page(viewport={"width": 1440, "height": 1000})
            page.set_default_timeout(8000)
            errors: list[str] = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            try:
                response = page.goto(f"{base_url}/source-project", wait_until="networkidle")
                assert response and response.status == 200
                open_folder_summary = page.locator(".vaw-open-project-details > summary")
                if open_folder_summary.count():
                    open_folder_summary.click()
                page.get_by_label("Absolute folder path", exact=True).fill(str(project))
                page.get_by_role("button", name="Open folder", exact=True).click()
                expect(page.get_by_role("heading", name=project_name, exact=True)).to_be_visible()
                assert page.locator(".vaw-ide-activitybar > a").evaluate_all(
                    "items => items.every(item => parseFloat(getComputedStyle(item).fontSize) >= 11)"
                ), "Studio navigation labels must remain at least 11px for legibility."

                page.get_by_role("button", name="Create project item", exact=True).click()
                page.get_by_role("menuitem", name="New file", exact=False).click()
                page.get_by_role("menuitem", name="Markdown .md", exact=True).click()
                editor = page.locator(".vaw-markdown-editor-host .monaco-editor")
                expect(editor).to_be_visible(timeout=15000)
                expect(page.get_by_role("button", name="Save Markdown", exact=True)).to_be_enabled()

                name = page.get_by_label("File name", exact=True)
                name.fill("Guide.md")
                editor.click(position={"x": 120, "y": 35})
                page.keyboard.press("Control+End")
                page.keyboard.press("Enter")
                page.keyboard.type("## Getting started")
                page.keyboard.press("Enter")
                page.keyboard.type("Use **Monaco** to edit project notes.")
                editor_text = page.evaluate("window.VAWMonaco.editor.getModels()[0].getValue()")
                assert "## Getting started" in editor_text and "Use **Monaco** to edit project notes." in editor_text, editor_text
                expect(page.get_by_role("button", name="Save Markdown", exact=True)).to_be_enabled()

                page.keyboard.type("!")
                page.get_by_role("button", name="Save Markdown", exact=True).click()
                expect(page.locator(".vaw-project-folder-editor:not([hidden]) > p[role='status']").first).to_contain_text("Saved docs/Guide.md")
                saved_path = project / "docs" / "Guide.md"
                saved_text = saved_path.read_text(encoding="utf-8")
                assert saved_text == f"# {project_name}\n\n\n## Getting started\nUse **Monaco** to edit project notes.!", repr(saved_text)
                manifest = json.loads((project / "vaw.project.json").read_text(encoding="utf-8"))
                assert any(item["path"] == "docs/Guide.md" and item["kind"] == "documentation" for item in manifest["files"])

                tree_file = page.get_by_role("button", name="Edit Markdown document Guide.md", exact=True)
                expect(tree_file).to_be_visible()
                tree_file.click()
                expect(editor).to_be_visible()
                expect(page.locator(".monaco-editor .view-lines")).to_contain_text("Getting started")
                editor.click(position={"x": 120, "y": 35})
                page.keyboard.press("Control+End")
                page.keyboard.press("Enter")
                page.keyboard.type("Saved with Ctrl+S")
                expect(page.get_by_role("button", name="Save Markdown", exact=True)).to_be_enabled()
                page.keyboard.press("Control+S")
                expect(page.get_by_role("button", name="Save Markdown", exact=True)).to_be_disabled()
                assert saved_path.exists() and saved_path.read_text(encoding="utf-8").endswith("Saved with Ctrl+S")

                theme_picker = page.get_by_label("Color theme", exact=True)
                for theme in ("dark", "light", "vs-code-dark-plus"):
                    theme_picker.select_option(theme)
                    page.wait_for_function(f"document.documentElement.dataset.vawTheme === '{theme}'")
                    editor_background = page.locator(".monaco-editor .monaco-editor-background").evaluate(
                        "element => getComputedStyle(element).backgroundColor")
                    page_background = page.locator("body").evaluate("element => getComputedStyle(element).backgroundColor")
                    assert editor_background == page_background, (
                        f"Monaco should match the active Studio canvas in {theme}: {editor_background} != {page_background}"
                    )
                    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), f"Editor must not cause page overflow in {theme}."
                    if screenshot_directory:
                        screenshot_directory.mkdir(parents=True, exist_ok=True)
                        page.screenshot(path=str(screenshot_directory / f"markdown-editor-{theme}.png"), full_page=True)

                page.set_viewport_size({"width": 390, "height": 844})
                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "Markdown editor must fit a phone viewport."
                assert page.locator(".vaw-markdown-editor-host").evaluate("element => element.getBoundingClientRect().height") >= 280
                assert page.locator(".vaw-ide-activitybar > a").evaluate_all(
                    "items => items.every(item => parseFloat(getComputedStyle(item).fontSize) >= 11 && item.getBoundingClientRect().height >= 44)"
                ), "Mobile activity rail must keep readable labels and comfortable tap targets."
                page.set_viewport_size({"width": 1440, "height": 1000})
                page.locator(".vaw-project-tab.is-active .vaw-project-tab-close").click()
                expect(page.get_by_role("tab", name=project_name, exact=True)).to_have_count(0)
                assert not errors, errors
            finally:
                browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    parser.add_argument("--screenshots", type=Path, help="Directory for desktop theme screenshots")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable, arguments.screenshots)
    print("Monaco Markdown editor creation, editing, save/reopen, themes, and responsive layout passed.")


if __name__ == "__main__":
    main()
