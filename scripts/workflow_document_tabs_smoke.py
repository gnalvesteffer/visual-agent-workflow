"""Exercise workflow-document tab closing and discard confirmation in the IDE."""
import argparse
import contextlib
import re
from playwright.sync_api import expect, sync_playwright
from workbench_browser import create_workflow


def verify(base_url: str, executable: str | None) -> None:
    with sync_playwright() as playwright:
        options = {"headless": True}
        if executable:
            options["executable_path"] = executable
        browser = playwright.chromium.launch(**options)
        page = browser.new_page(viewport={"width": 1440, "height": 900})
        page.set_default_timeout(8000)
        errors: list[str] = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        try:
            page.goto(base_url + "/projects/default", wait_until="networkidle")
            tabs = page.get_by_role("navigation", name="Open workflow documents", exact=True)
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(1)

            # New, unsaved documents require an explicit discard decision.
            create_workflow(page)
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(2)
            new_tab = tabs.locator(".vaw-workflow-document-tab.is-active")
            new_tab.get_by_role("button", name=re.compile("Close .*workflow")).click()
            dialog = page.get_by_role("dialog", name="Discard unsaved changes?")
            expect(dialog).to_be_visible()
            dialog.get_by_role("button", name="Keep tab open", exact=True).click()
            expect(dialog).not_to_be_visible()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(2)
            new_tab = tabs.locator(".vaw-workflow-document-tab.is-active")
            new_tab.get_by_role("button", name=re.compile("Close .*workflow")).click()
            dialog.get_by_role("button", name="Discard and close", exact=True).click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(1)

            # Middle-click closes a saved tab and the final tab leaves a usable empty editor.
            only_tab = tabs.locator(".vaw-workflow-document-tab")
            only_tab.click(button="middle")
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(0)
            expect(page.locator(".vaw-canvas-viewport")).to_have_count(0)
            expect(page.locator(".vaw-global-explorer")).to_be_visible()

            # Open a saved file, create a second tab, and ensure closing an inactive tab
            # does not discard the active document's unsaved edits.
            explorer = page.locator(".vaw-global-explorer")
            saved_file = explorer.locator(".vaw-explorer-file[data-workflow-id]").first
            saved_file.click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(1)
            create_workflow(page)
            page.locator("[data-graph-save]").click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(2)
            active_tab = tabs.locator(".vaw-workflow-document-tab.is-active")
            settings = page.get_by_role("button", name="Workflow settings", exact=True)
            if settings.get_attribute("aria-expanded") != "true":
                settings.click()
            name = page.get_by_label("Workflow name", exact=True)
            name.fill("Unsaved close smoke")
            name.press("Tab")
            expect(page.locator(".vaw-document-save-state")).to_have_text("Unsaved changes")

            # Use the first tab explicitly; the active one is the newly created last tab.
            inactive_tab = tabs.locator(".vaw-workflow-document-tab").first
            expect(inactive_tab).not_to_have_class(re.compile("is-active"))
            inactive_tab.get_by_role("button", name=re.compile("Close .*workflow")).click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(1)
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value("Unsaved close smoke")
            expect(page.get_by_role("dialog", name="Discard unsaved changes?")).to_have_count(0)

            # A dirty active tab can be kept, then explicitly discarded.
            active_tab = tabs.locator(".vaw-workflow-document-tab.is-active")
            active_tab.get_by_role("button", name=re.compile("Close .*workflow")).click()
            dialog = page.get_by_role("dialog", name="Discard unsaved changes?")
            expect(dialog).to_be_visible()
            dialog.get_by_role("button", name="Keep tab open", exact=True).click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(1)
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value("Unsaved close smoke")
            active_tab = tabs.locator(".vaw-workflow-document-tab.is-active")
            active_tab.get_by_role("button", name=re.compile("Close .*workflow")).click()
            dialog.get_by_role("button", name="Discard and close", exact=True).click()
            expect(tabs.locator(".vaw-workflow-document-tab")).to_have_count(0)
            assert not errors, errors
        except Exception:
            with contextlib.suppress(Exception):
                page.screenshot(path="/tmp/vaw-document-tabs-failure.png")
            raise
        finally:
            browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:5099")
    parser.add_argument("--browser-executable")
    args = parser.parse_args()
    verify(args.base_url.rstrip("/"), args.browser_executable)
    print("Workflow tabs support clean/inactive close, middle-click, and explicit dirty/new-document discard confirmation.")
