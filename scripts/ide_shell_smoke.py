"""Exercise the shared Studio IDE frame, graph canvas, and responsive navigation."""

import argparse
import os

from playwright.sync_api import expect, sync_playwright


def verify(base_url: str, browser_executable: str | None) -> None:
    with sync_playwright() as playwright:
        launch_options = {"headless": True}
        if browser_executable:
            launch_options["executable_path"] = browser_executable
        browser = playwright.chromium.launch(**launch_options)
        page = browser.new_page(viewport={"width": 1600, "height": 1000})
        page.set_default_timeout(5000)
        page_errors: list[str] = []
        page.on("pageerror", lambda error: page_errors.append(str(error)))
        try:
            response = page.goto(f"{base_url}/projects/default", wait_until="domcontentloaded")
            assert response and response.status == 200
            expect(page.locator(".vaw-app-shell")).to_be_visible()
            expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
            expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
            expect(page.locator(".vaw-global-explorer")).to_be_visible()
            canvas = page.locator(".vaw-canvas-viewport")
            expect(canvas).to_be_visible()
            expect(page.locator(".vaw-canvas-world [data-node-id]")).to_have_count(2)
            page.wait_for_function("document.querySelector('.vaw-graph-editor')?.dataset.ready === 'true'")
            page.wait_for_function("""() => {
                const canvas = document.querySelector('.vaw-canvas-viewport')?.getBoundingClientRect();
                const nodes = [...document.querySelectorAll('.vaw-canvas-world [data-node-id]')];
                return canvas && nodes.length === 2 && nodes.every(node => {
                    const box = node.getBoundingClientRect();
                    return box.width > 0 && box.height > 0 && box.right > canvas.left && box.left < canvas.right && box.bottom > canvas.top && box.top < canvas.bottom;
                });
            }""")
            assert canvas.bounding_box()["height"] >= 300, "The workflow graph must retain useful height."

            page.locator("[data-graph-save]").click()
            expect(page.locator(".vaw-definition-status")).to_contain_text("Draft saved")
            page.get_by_role("button", name="Refresh project files", exact=True).click()
            workflow_file = page.get_by_role("button", name="Untitled workflow.workflow", exact=True)
            expect(workflow_file).to_be_visible()
            page.get_by_role("button", name="New workflow", exact=True).click()
            name = page.get_by_label("Workflow name", exact=True)
            name.fill("Reopen smoke")
            name.press("Tab")
            page.locator("[data-graph-save]").click()
            expect(page.get_by_role("button", name="Reopen smoke.workflow", exact=True)).to_be_visible()
            workflow_file.click()
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value("Untitled workflow")
            page.wait_for_function("document.querySelector('.vaw-graph-editor')?.dataset.ready === 'true'")
            expect(page.locator(".vaw-canvas-world [data-node-id]")).to_have_count(2)
            page.wait_for_function("""() => {
                const canvas = document.querySelector('.vaw-canvas-viewport')?.getBoundingClientRect();
                return canvas && [...document.querySelectorAll('.vaw-canvas-world [data-node-id]')].every(node => {
                    const box = node.getBoundingClientRect();
                    return box.width > 0 && box.height > 0 && box.right > canvas.left && box.left < canvas.right && box.bottom > canvas.top && box.top < canvas.bottom;
                });
            }""")
            page.screenshot(path="/tmp/vaw-studio-ide.png")

            expand = page.get_by_role("button", name="Expand workflow details")
            expect(expand).to_have_attribute("aria-expanded", "false")
            expand.click()
            expect(page.get_by_role("button", name="Collapse workflow details")).to_have_attribute("aria-expanded", "true")
            page.get_by_role("button", name="Collapse workflow details").click()
            expect(page.locator(".vaw-bottom-tray")).to_have_class("vaw-bottom-tray is-collapsed")

            expect(page.get_by_label("Color theme", exact=True)).to_be_enabled()
            page.get_by_label("Color theme", exact=True).select_option("vs-code-dark-plus")
            route_links = (
                ("Agents", "/agents"), ("Skills", "/skills"), ("Functions", "/functions"),
                ("MCP library", "/mcp"), ("Observe", "/observe"), ("Projects", "/projects"),
                ("Models", "/settings"), ("Open folder", "/source-project"),
            )
            for name, route in route_links:
                page.get_by_role("link", name=name, exact=True).click()
                expect(page).to_have_url(f"{base_url}{route}")
                expect(page.locator(".vaw-app-shell")).to_be_visible()
                expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
                expect(page.get_by_label("Color theme", exact=True)).to_have_value("vs-code-dark-plus")
            expect(page.get_by_label("Absolute folder path", exact=True)).to_be_visible()
            assert page.evaluate("performance.getEntriesByType('navigation').length") == 1, "Internal views should remain in one SPA document."

            page.set_viewport_size({"width": 390, "height": 844})
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "The shared shell must not create page-level horizontal scrolling on mobile."
            expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
            assert not page_errors, page_errors
        finally:
            browser.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099"))
    parser.add_argument("--browser-executable", help="Optional path to installed Chromium")
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip("/"), arguments.browser_executable)
    print("Shared Studio IDE shell passed desktop, route, theme, graph, and mobile checks.")


if __name__ == "__main__":
    main()
