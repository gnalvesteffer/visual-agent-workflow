"""Exercise the shared Studio IDE frame, graph canvas, and responsive navigation."""

import argparse
import os
import re
import uuid

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
            expect(page.locator(".vaw-ide-titlebar").get_by_role("link", name="Projects", exact=True)).to_have_count(0)
            expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
            expect(page.locator(".vaw-global-explorer")).to_be_visible()
            expect(page.get_by_label("Filter project files", exact=True)).to_be_visible()
            canvas = page.locator(".vaw-canvas-viewport")
            expect(canvas).to_be_visible()
            expect(page.locator(".vaw-document-header")).to_be_visible()
            expect(page.locator(".vaw-document-statusbar > span").first).to_have_text("Ready", timeout=15000)
            expect(page.locator(".vaw-document-settings")).not_to_be_visible()
            expect(page.get_by_role("navigation", name="Workspace views")).not_to_be_visible()
            expect(page.get_by_role("navigation", name="Open workflow documents")).to_be_visible()
            canvas_bounds = canvas.bounding_box()
            canvas_scroll = canvas.evaluate("e => [e.scrollLeft, e.scrollTop]")
            page.get_by_role("button", name="Workflow settings", exact=True).click()
            expect(page.locator(".vaw-document-settings.is-open")).to_be_visible()
            assert canvas.bounding_box() == canvas_bounds, "Opening workflow settings must not move or resize the graph canvas."
            assert canvas.evaluate("e => [e.scrollLeft, e.scrollTop]") == canvas_scroll, "Opening workflow settings must preserve graph pan."
            page.screenshot(path="/tmp/vaw-workflow-settings.png")
            workflow_name_control = page.get_by_label("Workflow name", exact=True)
            assert 30 <= workflow_name_control.evaluate("e => Math.round(e.getBoundingClientRect().height)") <= 40
            page.get_by_role("button", name="Workflow settings", exact=True).click()
            assert canvas.bounding_box() == canvas_bounds, "Closing workflow settings must preserve graph canvas bounds."
            assert canvas.evaluate("e => [e.scrollLeft, e.scrollTop]") == canvas_scroll, "Closing workflow settings must preserve graph pan."
            navigation_status = page.locator(".vaw-navigation-status")
            expect(navigation_status).to_have_count(1)
            expect(navigation_status).to_be_hidden()
            # Hold one destination after the real capture-phase loading handler runs.
            # This tests slow-navigation feedback without a sleep or a live slow service.
            page.evaluate("""() => {
                document.addEventListener('click', function holdNavigation(event) {
                    if (!event.target.closest('a[aria-label="Agents"]')) return;
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    document.removeEventListener('click', holdNavigation, true);
                }, true);
            }""")
            page.get_by_role("link", name="Agents", exact=True).click()
            expect(navigation_status).to_be_visible()
            expect(navigation_status).to_have_attribute("role", "status")
            page.locator(".vaw-host-settings > summary").click()
            expect(page.get_by_role("link", name="MCP connections", exact=True)).to_be_visible()
            page.keyboard.press("Escape")
            expect(page.locator(".vaw-host-settings")).not_to_have_attribute("open", "")
            page.locator(".vaw-host-settings > summary").click()
            page.get_by_role("link", name="Model connections", exact=True).click()
            expect(page.get_by_role("heading", name="Model connections", exact=True)).to_be_visible()
            expect(navigation_status).to_be_hidden()
            page.get_by_role("link", name="Explorer", exact=True).click()
            expect(page.locator(".vaw-document-header")).to_be_visible()
            assert page.locator(".vaw-explorer-manifest svg").count() == 1, "Project metadata should use a non-wrapping file icon."
            expect(page.locator(".vaw-canvas-world [data-node-id]")).to_have_count(2)
            expect(page.locator(".vaw-canvas-world .vaw-node-heading-icon svg")).to_have_count(2)
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
            expect(page.get_by_label("Workflow status")).to_contain_text("Project: default")
            inspector = page.get_by_label("Selected node properties", exact=True)
            inspector.get_by_role("button", name="Inputs", exact=True).click()
            expect(inspector).to_contain_text("This node has no input pins.")
            inspector.get_by_role("button", name="Outputs", exact=True).click()
            expect(inspector.locator(".vaw-inspector-ports")).to_contain_text("string")
            inspector.get_by_role("button", name="Configure", exact=True).click()
            expect(inspector.get_by_label("Node label", exact=True)).to_be_visible()
            label_height = inspector.get_by_label("Node label", exact=True).evaluate("e => e.getBoundingClientRect().height")
            assert 30 <= label_height <= 40, f"Node fields should match the IDE scale: {label_height}"
            viewport = canvas.bounding_box()
            old_scroll = canvas.evaluate("e=>e.scrollLeft")
            page.get_by_role("button", name="Pan", exact=True).click()
            page.mouse.move(viewport["x"] + 40, viewport["y"] + 40)
            page.mouse.down()
            page.mouse.move(viewport["x"] + 100, viewport["y"] + 60, steps=5)
            page.mouse.up()
            assert canvas.evaluate("e=>e.scrollLeft") != old_scroll, "Pan mode must move the viewport."
            page.get_by_role("button", name="Select", exact=True).click()
            page.get_by_role("button", name="Reset view", exact=True).click()

            page.locator("[data-graph-save]").click()
            expect(page.locator(".vaw-definition-status")).to_contain_text("Draft saved")
            page.get_by_role("button", name="Refresh project files", exact=True).click()
            workflow_file = page.locator(".vaw-global-explorer").get_by_role("button", name="Untitled workflow.workflow", exact=True)
            expect(workflow_file).to_be_visible()
            expect(page.locator(".vaw-explorer-file.is-selected")).to_have_count(1)
            expect(workflow_file).to_have_attribute("aria-current", "page")
            page.get_by_role("button", name="Workflow settings", exact=True).click()
            __import__("workbench_browser").create_workflow(page)
            name = page.get_by_label("Workflow name", exact=True)
            name.fill(f"Reopen smoke {uuid.uuid4().hex[:8]}")
            name.press("Tab")
            page.locator("[data-graph-save]").click()
            workflow_name = name.input_value()
            page.get_by_role("button", name="Workflow settings", exact=True).click()
            workflow_file = page.locator(".vaw-global-explorer").get_by_role("button", name=f"{workflow_name}.workflow", exact=True)
            expect(workflow_file).to_be_visible()
            filter_files = page.get_by_label("Filter project files", exact=True)
            filter_files.fill(workflow_name)
            expect(workflow_file).to_be_visible()
            expect(page.locator(".vaw-global-explorer .vaw-explorer-empty[role=status]")).to_have_count(0)
            filter_files.fill("no matching file")
            expect(page.locator(".vaw-global-explorer .vaw-explorer-empty[role=status]")).to_contain_text("No files match")
            page.get_by_role("button", name="Clear file filter", exact=True).click()
            expect(workflow_file).to_be_visible()
            workflow_file.click()
            expect(workflow_file).to_have_class("vaw-explorer-file is-selected")
            expect(page.locator(".vaw-explorer-file.is-selected")).to_have_count(1)
            page.wait_for_function("!document.querySelector('[data-vaw-workflow-selector]')?.disabled")
            selected_by_picker = page.evaluate("""() => {
                const select = document.querySelector('[data-vaw-workflow-selector]');
                const option = [...select.options].find(item => item.value !== select.value);
                window.vawStudioIde.selectWorkflow(option.value);
                return option.value;
            }""")
            expect(page.locator(".vaw-explorer-file.is-selected")).to_have_attribute("data-workflow-id", selected_by_picker)
            workflow_file.click()
            expect(workflow_file).to_have_attribute("aria-current", "page")
            expect(page).to_have_url(f"{base_url}/projects/default")
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value(workflow_name)
            page.wait_for_function("document.querySelector('.vaw-graph-editor')?.dataset.ready === 'true'")
            page.wait_for_timeout(350)
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value(workflow_name)
            expect(page.locator(".vaw-canvas-world [data-node-id]")).to_have_count(2)
            page.wait_for_function("""() => {
                const canvas = document.querySelector('.vaw-canvas-viewport')?.getBoundingClientRect();
                return canvas && [...document.querySelectorAll('.vaw-canvas-world [data-node-id]')].every(node => {
                    const box = node.getBoundingClientRect();
                    return box.width > 0 && box.height > 0 && box.right > canvas.left && box.left < canvas.right && box.bottom > canvas.top && box.top < canvas.bottom;
                });
            }""")
            page.locator(".vaw-global-explorer").get_by_role("button", name="Untitled workflow.workflow", exact=True).click()
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value("Untitled workflow")
            workflow_file.click()
            expect(page.get_by_label("Workflow name", exact=True)).to_have_value(workflow_name)
            expect(workflow_file).to_have_attribute("aria-current", "page")
            documents = page.get_by_role("navigation", name="Open workflow documents")
            workflow_tabs = documents.locator(".vaw-workflow-document-tab")
            expect(workflow_tabs).to_have_count(2)
            order_before = workflow_tabs.evaluate_all("tabs => tabs.map(tab => tab.dataset.workflowTabId)")
            active_before_reorder = documents.locator(".vaw-workflow-document-tab.is-active").get_attribute("data-workflow-tab-id")
            workflow_tabs.nth(0).locator("button").first.drag_to(workflow_tabs.nth(1))
            order_after = workflow_tabs.evaluate_all("tabs => tabs.map(tab => tab.dataset.workflowTabId)")
            assert order_after == list(reversed(order_before)), (order_before, order_after)
            assert documents.locator(".vaw-workflow-document-tab.is-active").get_attribute("data-workflow-tab-id") == active_before_reorder
            documents.evaluate("element => element.style.flex = '0 0 135px'")
            documents.get_by_role("button", name="Untitled workflow.workflow", exact=True).click()
            expect(page.locator(".vaw-explorer-file.is-selected")).to_contain_text("Untitled workflow.workflow")
            assert documents.evaluate("element => element.scrollWidth > element.clientWidth"), "Workflow tabs should scroll horizontally when they do not fit."
            active_intersection = documents.locator(".vaw-workflow-document-tab.is-active").evaluate("tab => { const a = tab.getBoundingClientRect(), s = tab.parentElement.getBoundingClientRect(); return Math.min(a.right, s.right) - Math.max(a.left, s.left); }")
            assert active_intersection > 0, f"Switching tabs must reveal the active tab in an overflowing strip: {active_intersection}"
            documents.evaluate("element => element.style.flex = ''")
            documents.get_by_role("button", name=f"{workflow_name}.workflow", exact=True).click()
            expect(workflow_file).to_have_attribute("aria-current", "page")
            page.screenshot(path="/tmp/vaw-studio-ide.png")

            expect(page.get_by_role("button", name="Publish & activate", exact=True)).to_have_count(0)
            expect(page.get_by_role("button", name="Versions", exact=True)).to_have_count(0)
            run_button = page.get_by_role("button", name="Run workflow", exact=True)
            expect(run_button).to_be_enabled()
            run_button.click()
            run_dialog = page.locator(".vaw-run-dialog")
            expect(run_dialog).to_be_visible()
            run_dialog.get_by_label("Workflow input", exact=True).fill("Run directly from the saved project source")
            run_dialog.get_by_role("button", name="Run workflow", exact=True).click()
            expect(page.locator(".vaw-viewer")).to_be_visible()
            page.get_by_role("button", name="Back to workflow", exact=True).click()
            history = page.get_by_role("region", name="Workflow designer").get_by_label("Workflow run history", exact=True)
            expect(history.locator("tbody tr")).to_have_count(1)
            expect(history.locator("tbody tr")).to_contain_text("completed")
            expect(page.locator(".vaw-canvas-viewport").first).to_be_visible()
            page.locator(".vaw-bottom-tray nav").get_by_role("button", name="Output", exact=True).click()
            expect(page.locator(".vaw-workflow-result")).to_have_text("Run directly from the saved project source")
            page.locator(".vaw-bottom-tray nav").get_by_role("button", name="Runs", exact=True).click()
            history.get_by_role("button", name="Inspect run", exact=False).click()
            expect(page.locator(".vaw-viewer")).to_be_visible()
            expect(page.locator(".vaw-run-header")).to_contain_text("Latest run status: completed")
            run_graph_nodes = page.locator(".vaw-viewer .vaw-canvas-world [data-node-id]")
            expect(run_graph_nodes).to_have_count(2)
            page.wait_for_function("""() => {
                const canvas = document.querySelector('.vaw-viewer .vaw-canvas-viewport')?.getBoundingClientRect();
                const nodes = [...document.querySelectorAll('.vaw-viewer .vaw-canvas-world [data-node-id]')];
                return canvas && canvas.top >= 0 && canvas.bottom <= innerHeight && nodes.length > 0 && nodes.some(node => {
                    const box = node.getBoundingClientRect();
                    return box.width > 0 && box.height > 0 && box.right > canvas.left && box.left < canvas.right && box.bottom > canvas.top && box.top < canvas.bottom && box.bottom <= innerHeight;
                });
            }""", timeout=10000)
            viewer_size = page.locator(".vaw-viewer").evaluate("e => ({height: e.clientHeight, scrollHeight: e.scrollHeight})")
            assert viewer_size["height"] > 0 and viewer_size["scrollHeight"] <= viewer_size["height"] + 2, (
                f"IDE run inspector should fit the graph without a tall outer scroll: {viewer_size}"
            )
            page.screenshot(path="/tmp/vaw-run-inspector-ide.png")
            page.get_by_role("button", name="Back to workflow", exact=True).click()

            collapse = page.get_by_role("button", name="Collapse workflow details")
            expect(collapse).to_have_attribute("aria-expanded", "true")
            collapse.click()
            expect(page.get_by_role("button", name="Expand workflow details")).to_have_attribute("aria-expanded", "false")
            expect(page.locator(".vaw-bottom-tray")).to_have_class("vaw-bottom-tray is-collapsed")

            # An invalid draft remains editable, with a clear problem list and a disabled Run action.
            invalid = page.request.get(f"{base_url}/api/templates/default").json()
            invalid["workflowId"] = "invalid-ide-" + uuid.uuid4().hex
            invalid["displayName"] = "Incomplete wiring"
            invalid["connections"].pop(next(iter(invalid["connections"])))
            saved = page.request.put(f"{base_url}/api/workflows/{invalid['workflowId']}/draft", data={"definition": invalid, "expectedGeneration": 0})
            assert saved.ok, saved.text()
            page.get_by_role("button", name="Refresh project files", exact=True).click()
            page.locator(".vaw-global-explorer").get_by_role("button", name="Incomplete wiring.workflow", exact=True).click()
            expect(page.get_by_role("button", name="Run workflow", exact=True)).to_be_disabled()
            page.locator(".vaw-bottom-tray nav").get_by_role("button", name="Problems", exact=False).click()
            expect(page.locator(".vaw-bottom-issues li").first).to_be_visible()
            expect(page.locator("[data-graph-save]")).to_be_enabled()
            workflow_file.click()
            expect(page.get_by_role("button", name="Run workflow", exact=True)).to_be_enabled()

            expect(page.get_by_label("Color theme", exact=True)).to_be_enabled()
            scrollbar_track_before = page.evaluate("getComputedStyle(document.documentElement).getPropertyValue('--vaw-scrollbar-track').trim()")
            page.get_by_label("Color theme", exact=True).select_option("neon")
            neon_styles = page.evaluate("""() => ({
                canvas: getComputedStyle(document.querySelector('.vaw-canvas-viewport')).boxShadow,
                wire: getComputedStyle(document.querySelector('.vaw-wire')).filter,
                selected: getComputedStyle(document.querySelector('.vaw-explorer-file.is-selected')).boxShadow,
                action: getComputedStyle(document.querySelector('.vaw-primary')).boxShadow
            })""")
            assert neon_styles["canvas"] != "none", neon_styles
            assert neon_styles["wire"].startswith("drop-shadow("), neon_styles
            assert neon_styles["selected"] != "none" and neon_styles["action"] != "none", neon_styles
            page.screenshot(path="/tmp/vaw-neon-glow.png")
            page.get_by_label("Color theme", exact=True).select_option("light")
            scrollbar_track_after = page.evaluate("getComputedStyle(document.documentElement).getPropertyValue('--vaw-scrollbar-track').trim()")
            assert scrollbar_track_after and scrollbar_track_after != scrollbar_track_before, (
                "Changing the theme should update the browser viewport scrollbar colors."
            )
            page.get_by_label("Color theme", exact=True).select_option("vs-code-dark-plus")
            expect(page.locator(".vaw-ide-activitybar a[aria-label='MCP library']")).to_have_count(0)
            route_links = (
                ("Agents", "/agents"), ("Skills", "/skills"), ("Functions", "/functions"),
                ("Observe", "/observe"), ("Manage projects…", "/projects"),
                ("MCP connections", "/mcp"), ("Model connections", "/settings"), ("Open folder", "/source-project"),
            )
            for name, route in route_links:
                if name in ("MCP connections", "Model connections"):
                    settings = page.locator(".vaw-host-settings")
                    if not settings.evaluate("element => element.open"):
                        page.locator(".vaw-host-settings > summary").click()
                if name == "Manage projects…":
                    page.get_by_role("button", name=re.compile("^Current project:")).click()
                    page.get_by_role("link", name=name, exact=True).click()
                else:
                    page.get_by_role("link", name=name, exact=True).click()
                expect(page).to_have_url(f"{base_url}{route}")
                expect(page.locator(".vaw-app-shell")).to_be_visible()
                expect(page.locator(".vaw-ide-activitybar")).to_be_visible()
                expect(page.get_by_label("Color theme", exact=True)).to_have_value("vs-code-dark-plus")
                visible_alerts = page.locator("[role=alert]:visible").all_inner_texts()
                assert not any("\n   at " in alert or "System." in alert for alert in visible_alerts), (
                    f"{route} should show concise user-facing errors, not raw exception details: {visible_alerts}"
                )
                if route in ("/agents", "/skills", "/functions", "/mcp", "/settings", "/source-project"):
                    form_control_heights = page.locator("main input:not([type=checkbox]):not([type=file]), main select").evaluate_all(
                        "els => els.filter(e => e.getBoundingClientRect().height > 0).map(e => Math.round(e.getBoundingClientRect().height))"
                    )
                    assert all(40 <= height <= 50 for height in form_control_heights), (
                        f"Form controls should share a comfortable ~44px size on {route}: {form_control_heights}"
                    )
                if route in ("/agents", "/skills", "/functions", "/mcp", "/observe", "/settings"):
                    if name in ("MCP connections", "Model connections"):
                        settings = page.locator(".vaw-host-settings")
                        if not settings.evaluate("element => element.open"):
                            page.locator(".vaw-host-settings > summary").click()
                    expect(page.get_by_role("link", name=name, exact=True)).to_have_attribute("aria-current", "page")
                    expect(page.get_by_role("link", name="Explorer", exact=True)).not_to_have_class("is-active")
                if route == "/settings":
                    expect(page.locator(".vaw-ide-route-heading")).to_have_text("Model connections")
                if route == "/projects":
                    expect(page.get_by_role("heading", name="Projects", exact=True)).to_be_visible()
                if route == "/agents":
                    description = page.get_by_label("Delegate description", exact=True)
                    assert description.evaluate("e => e.getBoundingClientRect().height") < 85, "Two-row descriptions should not inherit the instruction editor's tall minimum."
                if route == "/observe":
                    expect(page.get_by_role("heading", name="No consumer services configured")).to_be_visible()
                    expect(page.locator("pre")).to_contain_text("WorkflowObservation__Sources__consumer__BaseAddress")
            expect(page.get_by_label("Absolute folder path", exact=True)).to_be_visible()
            assert page.evaluate("performance.getEntriesByType('navigation').length") == 1, "Internal views should remain in one SPA document."

            page.set_viewport_size({"width": 390, "height": 844})
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "The shared shell must not create page-level horizontal scrolling on mobile."
            expect(page.locator(".vaw-ide-titlebar")).to_be_visible()
            assert not page_errors, page_errors
        except Exception:
            page.screenshot(path="/tmp/vaw-ide-failure.png")
            raise
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
