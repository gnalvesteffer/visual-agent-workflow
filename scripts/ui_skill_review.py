"""Browser checks for UI and embedding behavior against the running local Studio.

Requires Playwright and Chromium. Uses only non-sensitive fixture text; creates
recorded development runs. Run studio_smoke.py separately for the full editor flow.
"""

from workbench_browser import current_project, workbench

import argparse
import contextlib
import uuid
import hashlib
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")
THEMES = ("dark", "light", "vs-code-dark-plus", "solarized-dark", "nord", "monokai", "github-dark", "github-light", "dracula", "high-contrast", "neon", "retro-drive")


def luminance(rgb):
    values = [v / 255 for v in rgb]
    values = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in values]
    return sum(v * weight for v, weight in zip(values, [0.2126, 0.7152, 0.0722]))


def contrast(first, second):
    a, b = sorted([luminance(first), luminance(second)])
    return (b + 0.05) / (a + 0.05)


def check(browser, screenshots):
    page = browser.new_page(viewport={"width": 1440, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    try:
        definition = page.request.get(BASE + "/api/templates/default").json()
        workflow_id = "ui-audit-" + uuid.uuid4().hex
        definition["workflowId"] = workflow_id
        saved = page.request.put(BASE + f"/api/workflows/{workflow_id}/draft", data={"definition": definition, "expectedGeneration": 0})
        assert saved.ok, saved.text()
        audit_url = BASE + "/projects/default?workflow=" + workflow_id
        routes = (
            ("/projects/default", "Explorer"),
            ("/projects/default/agents", "Agents"),
            ("/projects/default/skills", "Skills"),
            ("/projects/default/functions", "Functions"),
            ("/projects", "Projects"),
            ("/source-project", "Explorer"),
            ("/mcp", "MCP connections"),
            ("/settings", "Model connections"),
            ("/observe", "Observe"),
        )
        for route, active_page in routes:
            response = page.goto(BASE + route, wait_until="networkidle")
            assert response and response.status == 200, f"Studio page failed to load: {route}"
            navigation = page.locator(".vaw-ide-titlebar")
            if active_page in ("MCP connections", "Model connections"):
                settings = page.locator(".vaw-host-settings")
                if not settings.evaluate("element => element.open"):
                    page.locator(".vaw-host-settings > summary").click()
            if active_page == "Projects":
                expect(page.get_by_role("heading", name="Projects", exact=True)).to_be_visible()
            else:
                expect(page.get_by_role("link", name=active_page, exact=True)).to_have_attribute("aria-current", "page")
            assert abs(navigation.bounding_box()["width"] - page.viewport_size["width"]) < 1, f"Navigation must span the viewport on {route}"
            assert "gradient" not in navigation.evaluate("element => getComputedStyle(element).backgroundImage"), f"Navigation should use a solid theme surface on {route}"
            if screenshots:
                page.screenshot(path=str(screenshots / f"page-{active_page.lower().replace(' ', '-')}.png"))

        page.set_viewport_size({"width": 390, "height": 844})
        for route, active_page in routes:
            response = page.goto(BASE + route, wait_until="networkidle")
            assert response and response.status == 200, f"Studio page failed to load on mobile: {route}"
            navigation = page.locator(".vaw-ide-titlebar")
            if active_page in ("MCP connections", "Model connections"):
                settings = page.locator(".vaw-host-settings")
                if not settings.evaluate("element => element.open"):
                    page.locator(".vaw-host-settings > summary").click()
            if active_page == "Projects":
                expect(page.get_by_role("heading", name="Projects", exact=True)).to_be_visible()
            else:
                expect(page.get_by_role("link", name=active_page, exact=True)).to_have_attribute("aria-current", "page")
            expect(current_project(page)).to_be_visible()
            assert abs(navigation.bounding_box()["width"] - page.viewport_size["width"]) < 1, f"Mobile navigation must span the viewport on {route}"
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), f"The page must not create horizontal scrolling on mobile: {route}"
            if screenshots and route == "/settings":
                page.screenshot(path=str(screenshots / "mobile-models-navigation.png"))
            if route in ("/projects", "/source-project"):
                overflow = page.locator(".vaw-project-create input").evaluate_all("""inputs => inputs
                    .filter(input => input.getBoundingClientRect().right > innerWidth + 1)
                    .map(input => input.getAttribute('aria-label') || input.type || input.outerHTML)""")
                assert not overflow, f"Project form fields must fit the page on mobile: {route}: {overflow}"

        page.locator(".vaw-host-settings > summary").click()
        page.get_by_role("link", name="Model connections", exact=True).click()
        expect(page.get_by_role("heading", name="Model connections", exact=True)).to_be_visible()
        current_project(page).click()
        page.get_by_role("link", name="Manage projects…", exact=True).click()
        expect(page.get_by_role("heading", name="Projects", exact=True)).to_be_visible()

        page.goto(BASE + "/projects/default/functions", wait_until="networkidle")
        page.wait_for_function("document.querySelector('.vaw-graph-editor')?.dataset.ready === 'true'")
        assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "The function page must not create page-level horizontal scrolling on a phone viewport"
        assert page.locator(".vaw-function-editor .vaw-canvas-viewport").evaluate("element => element.getBoundingClientRect().width") >= 160, "The function graph must retain a usable canvas at a narrow viewport"
        page.set_viewport_size({"width": 1440, "height": 1100})

        # Pending navigation is exercised by ide_shell_smoke.py using a held click.
        page.goto(BASE + "/projects/default/agents", wait_until="networkidle")
        agent_section = page.locator('.vaw-settings-section[aria-label="Saved agent profiles"]')
        section_left = agent_section.bounding_box()["x"]
        title_left = agent_section.locator(":scope > h2").bounding_box()["x"]
        assert title_left - section_left >= 12, "Section titles must align to the shared content inset"

        print("Desktop/mobile routes, navigation access, function canvas and section insets passed.")
        with tempfile.TemporaryDirectory(prefix="vaw-ui-skill-") as project_directory:
            project_path = Path(project_directory) / "source-project"
            project_path.mkdir()
            (project_path / "vaw.project.json").write_text(json.dumps({
                "formatVersion": "1.0",
                "projectId": "ui-skill-project",
                "projectName": "UI skill project",
                "files": [],
            }, indent=2) + "\n", encoding="utf-8")
            page.goto(BASE + "/source-project", wait_until="networkidle")
            folder_form = page.locator(".vaw-open-project-details")
            if folder_form.count() and not folder_form.evaluate("e => e.open"):
                folder_form.locator(":scope > summary").click()
            page.get_by_label("Absolute folder path", exact=True).fill(str(project_path))
            page.get_by_role("button", name="Open folder", exact=True).click()
            expect(page.get_by_role("heading", name="UI skill project", exact=True)).to_be_visible()
            page.get_by_role("button", name="Create project item", exact=True).click()
            page.get_by_role("menuitem", name="New file", exact=False).click()
            page.get_by_role("menuitem", name="Skill .skill", exact=True).click()
            page.get_by_label("Skill ID", exact=True).fill("ui-skill")
            page.get_by_label("New version", exact=True).fill("1.0.0")
            page.get_by_label("Name", exact=True).fill("UI skill")
            page.get_by_label("Folder", exact=True).fill("Quality/Review")
            page.get_by_label("Instructions", exact=True).fill("Check each finding.")
            page.get_by_role("button", name="Save source skill version", exact=True).click()
            expect(page.locator("p[role='status']").filter(has_text="Saved skill revision ui-skill@1.0.0.")).to_be_attached()
            first_manifest = json.loads((project_path / "vaw.project.json").read_text(encoding="utf-8"))
            assert [(entry["id"], entry["version"]) for entry in first_manifest["files"]] == [("ui-skill", "1.0.0")]

            page.locator('.vaw-project-tree-file[aria-label^="Edit skill "]').first.click()
            expect(page.get_by_label("New version", exact=True)).to_have_value("1.0.0.1")
            page.get_by_label("Instructions", exact=True).fill("Check each finding and cite evidence.")
            page.get_by_role("button", name="Save source skill version", exact=True).click()
            expect(page.locator("p[role='status']").filter(has_text="Saved skill revision ui-skill@1.0.0.1.")).to_be_attached()
            second_manifest = json.loads((project_path / "vaw.project.json").read_text(encoding="utf-8"))
            assert [(entry["id"], entry["version"]) for entry in second_manifest["files"]] == [
                ("ui-skill", "1.0.0"), ("ui-skill", "1.0.0.1")
            ]
            for entry in second_manifest["files"]:
                content = (project_path / entry["path"]).read_bytes()
                assert hashlib.sha256(content).hexdigest() == entry["sha256"], "Skill revisions must be indexed by their content hash"
            assert b"Check each finding." in (project_path / first_manifest["files"][0]["path"]).read_bytes(), "Editing a skill must preserve its prior revision"

        print("Explorer skill creation, editing and source integrity passed.")
        page.goto(audit_url, wait_until="networkidle")
        theme_picker = page.get_by_label("Color theme", exact=True)
        expect(theme_picker.locator("option")).to_have_count(len(THEMES))
        expect(theme_picker).to_be_enabled()
        expect(theme_picker).to_have_value("dark")
        theme_picker.select_option("light")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'light'")
        assert page.locator("body").evaluate("element=>getComputedStyle(element).backgroundColor") == "rgb(244, 245, 246)"
        page.reload()
        theme_picker = page.get_by_label("Color theme", exact=True)
        expect(theme_picker).to_have_value("light")
        theme_picker.select_option("high-contrast")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'high-contrast'")
        assert page.locator("body").evaluate("element=>getComputedStyle(element).backgroundColor") == "rgb(0, 0, 0)"
        theme_picker.select_option("nord")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'nord'")
        page.locator(".vaw-host-settings > summary").click()
        page.get_by_role("link", name="Model connections", exact=True).click()
        expect(page.get_by_role("heading", name="Model connections", exact=True)).to_be_visible()
        expect(page.get_by_label("Color theme", exact=True)).to_have_value("nord")
        expect(page.locator("html")).to_have_attribute("data-vaw-theme", "nord")
        page.get_by_role("link", name="Explorer", exact=True).click()
        expect(page.get_by_role("region", name="Workflow designer", exact=True)).to_be_visible()
        expect(page.get_by_label("Color theme", exact=True)).to_have_value("nord")
        page.get_by_role("link", name="Observe", exact=True).click()
        expect(page.get_by_role("heading", name="Observe consumer workflows")).to_be_visible()
        expect(page.get_by_label("Color theme", exact=True)).to_have_value("nord")
        page.get_by_role("link", name="Explorer", exact=True).first.click()
        expect(page.get_by_role("region", name="Workflow designer", exact=True)).to_be_visible()
        expect(page.get_by_label("Color theme", exact=True)).to_have_value("nord")
        page.goto(audit_url, wait_until="networkidle")
        theme_picker.select_option("dark")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'dark'")
        designer = page.get_by_role("region", name="Workflow designer", exact=True)
        for theme in THEMES:
            theme_picker.select_option(theme)
            page.wait_for_function("theme => document.documentElement.dataset.vawTheme === theme", arg=theme)
            assert "gradient" not in page.locator(".vaw-ide-titlebar").evaluate("element => getComputedStyle(element).backgroundImage"), f"Navigation must stay solid in {theme}"
            if screenshots:
                page.screenshot(path=str(screenshots / f"theme-{theme}.png"), full_page=True)
            theme_samples = page.evaluate("""() => {
                const rgb = value => value.match(/[0-9.]+/g).slice(0,3).map(Number);
                const pair = (foreground, background) => [rgb(getComputedStyle(document.querySelector(foreground)).color),
                    rgb(getComputedStyle(document.querySelector(background)).backgroundColor)];
                return {
                    text: pair('.vaw-studio', '.vaw-studio'),
                    primary: pair('.vaw-primary', '.vaw-primary'),
                    graphText: pair('.vaw-node-drag', '.vaw-node-drag'),
                    input: pair('.vaw-theme-picker select', '.vaw-theme-picker select'),
                    notice: pair('.vaw-document-breadcrumb', '.vaw-studio')
                };
            }""")
            theme_ratios = {name: contrast(*colors) for name, colors in theme_samples.items()}
            assert all(ratio >= 4.5 for ratio in theme_ratios.values()), {theme: theme_ratios}
        theme_picker.select_option("dark")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'dark'")
        start = designer.get_by_role("button", name="Run workflow", exact=True)
        expect(start).to_be_enabled()
        expect(designer.get_by_role("button", name="Save", exact=True)).to_be_enabled()
        assert page.locator("main main").count() == 0, "Reusable UI must not add nested main landmarks"
        assert page.locator(".vaw-studio button:not([type='button'])").count() == 0, "Embedded controls must not submit host forms"
        assert page.locator("head style").count() == 0, "Host styles belong in an external stylesheet"

        print("All theme palettes and navigation persistence passed.")
        chrome=designer.locator(".vaw-canvas-toolbar")
        viewport=designer.locator(".vaw-canvas-viewport")
        assert "linear-gradient" in viewport.evaluate("element=>getComputedStyle(element).backgroundImage")
        assert viewport.evaluate("element=>getComputedStyle(element).backgroundColor") == "rgb(24, 24, 24)"
        assert viewport.evaluate("element=>getComputedStyle(element).backgroundColor") == page.locator(".vaw-app-shell").evaluate("element=>getComputedStyle(element).backgroundColor"), "Dark Modern graph and shell surfaces must match"
        assert designer.locator(".vaw-canvas-world").evaluate("element=>getComputedStyle(element).backgroundImage") == "none"
        assert "none" == page.locator(".vaw-studio").evaluate("element=>getComputedStyle(element).backgroundImage")

        node_surface=designer.locator(".vaw-edit-node article").first
        assert node_surface.evaluate("element=>getComputedStyle(element).boxShadow") == "none"

        # Compare an unrelated host control with/without the library stylesheet.
        untouched = page.evaluate("""() => {
            const host = document.createElement('section');
            host.innerHTML = '<button>Host control</button><label>Host label</label><input aria-label="Host input">';
            host.style.cssText = 'color:rgb(25,35,45);font:20px Georgia,serif;background:rgb(240,241,242)';
            document.body.append(host);
            const link = document.querySelector('link[href*="workflow.css"]');
            const measure = () => [...host.children].map(element => {
                const css = getComputedStyle(element);
                return [css.color, css.backgroundColor, css.fontFamily, css.fontSize, css.borderRadius, css.display];
            });
            link.disabled = true; const before = measure();
            link.disabled = false; const after = measure();
            host.remove();
            return JSON.stringify(before) === JSON.stringify(after);
        }""")
        assert untouched, "Library styles changed unrelated host controls"

        themed = page.evaluate("""() => {
            const studio = document.querySelector('.vaw-studio');
            studio.style.setProperty('--vaw-color-surface', 'rgb(32,52,71)');
            studio.style.setProperty('--vaw-font-family', 'Georgia, serif');
            studio.style.setProperty('--vaw-graph-color-canvas', 'rgb(24,42,48)');
            const result = {
                sidebar: getComputedStyle(document.querySelector('.vaw-sidebar')).backgroundColor,
                nested: getComputedStyle(document.querySelector('.vaw-designer')).getPropertyValue('--vaw-color-surface').replaceAll(' ', ''),
                font: getComputedStyle(document.querySelector('.vaw-designer')).fontFamily,
                graph: getComputedStyle(document.querySelector('.vaw-canvas-viewport')).backgroundColor
            };
            studio.style.removeProperty('--vaw-color-surface');
            studio.style.removeProperty('--vaw-font-family');
            studio.style.removeProperty('--vaw-graph-color-canvas');
            return result;
        }""")
        assert themed["sidebar"] == "rgb(32, 52, 71)" and themed["nested"] == "rgb(32,52,71)", themed
        assert "Georgia" in themed["font"], "Host tokens must inherit through nested components"
        assert themed["graph"] == "rgb(24, 42, 48)", "Graph-specific host tokens must reach the canvas"

        samples = page.evaluate("""() => {
            const rgb = value => value.match(/[0-9.]+/g).slice(0,3).map(Number);
            const pair = (foreground, background) => [rgb(getComputedStyle(document.querySelector(foreground)).color),
                rgb(getComputedStyle(document.querySelector(background)).backgroundColor)];
            return {
                text: pair('.vaw-studio', '.vaw-studio'),
                primary: pair('.vaw-primary', '.vaw-primary'),
                graphText: pair('.vaw-node-drag', '.vaw-node-drag'),
                input: pair('.vaw-bottom-panel textarea', '.vaw-bottom-panel textarea'),
                notice: pair('.vaw-document-breadcrumb', '.vaw-studio')
            };
        }""")
        ratios = {name: contrast(*colors) for name, colors in samples.items()}
        assert all(ratio >= 4.5 for ratio in ratios.values()), ratios
        assert 30 <= start.bounding_box()["height"] <= 40
        start.focus()
        page.keyboard.press("Tab")
        assert page.evaluate("document.activeElement.matches(':focus-visible') && getComputedStyle(document.activeElement).outlineStyle !== 'none'"), "Keyboard focus must remain visible"

        graph = workbench(designer).get_by_role("list", name="Editable workflow nodes", exact=True)
        canvas = workbench(designer).get_by_label("Workflow graph canvas. Drag empty space to select nodes; middle-drag to pan.", exact=True)
        expect(workbench(designer).locator('.vaw-graph-editor')).to_have_attribute("data-ready", "true")
        assert abs(canvas.locator('.vaw-canvas-world').evaluate("element=>new DOMMatrixReadOnly(getComputedStyle(element).transform).a") - 1) < 0.001, "The initial graph view must start at 100%"
        page.wait_for_function("""() => {
            const viewport=document.querySelector('.vaw-canvas-viewport');
            const nodes=[...document.querySelectorAll('.vaw-canvas-viewport li[data-node-id]')];
            if (!viewport || !nodes.length) return false;
            const bounds=nodes.map(node=>node.getBoundingClientRect());
            const left=Math.min(...bounds.map(rect=>rect.left)), right=Math.max(...bounds.map(rect=>rect.right));
            const top=Math.min(...bounds.map(rect=>rect.top)), bottom=Math.max(...bounds.map(rect=>rect.bottom));
            const canvas=viewport.getBoundingClientRect();
            return Math.abs((left+right)/2-(canvas.left+canvas.right)/2)<3 &&
                Math.abs((top+bottom)/2-(canvas.top+canvas.bottom)/2)<3;
        }""")
        assert "linear-gradient" in canvas.evaluate("element=>getComputedStyle(element).backgroundImage"), "The graph grid must fill the whole viewport"
        assert graph.locator('li').first.evaluate("element => getComputedStyle(element).position") == "absolute", "Canvas nodes must be freely positioned"
        canvas.focus()
        assert canvas.evaluate("element => element === document.activeElement"), "Scrollable graph must be keyboard focusable"
        assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "Wide graph must not expand the page"
        if screenshots:
            page.evaluate("window.scrollTo(0,0)")
            page.screenshot(path=str(screenshots / "desktop.png"))

        print("Host CSS isolation, token inheritance, contrast and keyboard focus passed.")
        # A narrow embedded component must reflow even while the viewport stays wide.
        page.locator(".vaw-studio").evaluate("element => element.style.inlineSize = '360px'")
        dimensions = page.locator(".vaw-studio").evaluate("""element => ({
            width: element.clientWidth, content: element.scrollWidth,
            columns: getComputedStyle(element.querySelector('.vaw-workspace')).gridTemplateColumns.split(' ').length
        })""")
        assert dimensions["content"] <= dimensions["width"] + 1, dimensions
        assert canvas.evaluate("element => element.scrollWidth > element.clientWidth"), "Narrow hosts must contain graph scrolling"
        if screenshots:
            page.evaluate("window.scrollTo(0,0)")
            page.screenshot(path=str(screenshots / "narrow-embed.png"))
        page.locator(".vaw-studio").evaluate("element => element.style.removeProperty('inline-size')")

        page.set_viewport_size({"width": 390, "height": 844})
        page.evaluate("document.documentElement.style.fontSize = '200%'")
        for route, _ in routes:
            response = page.goto(BASE + route, wait_until="networkidle")
            assert response and response.status == 200, f"Studio page failed to load with enlarged text: {route}"
            page.evaluate("document.documentElement.style.fontSize = '200%'")
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), f"200% text must reflow on mobile: {route}"
        page.goto(audit_url, wait_until="networkidle")
        page.evaluate("document.documentElement.style.fontSize = '200%'")
        assert float(page.locator(".vaw-studio").evaluate("element => parseFloat(getComputedStyle(element).fontSize)")) >= 26, "Editor type must scale with enlarged text"
        if screenshots:
            page.evaluate("window.scrollTo(0,0)")
            page.screenshot(path=str(screenshots / "mobile-large-text.png"))
        page.evaluate("document.documentElement.style.removeProperty('font-size')")
        page.set_viewport_size({"width": 1440, "height": 1100})

        # The test rule precedes the library so its reduced-motion rule can override it.
        page.evaluate("""() => {
            const fixture = document.createElement('style');
            fixture.textContent = '@keyframes vaw-test-motion {from {opacity:.99} to {opacity:1}} :where(.vaw-studio) :where(.vaw-node) {animation:vaw-test-motion 10s infinite}';
            document.head.insertBefore(fixture, document.querySelector('link[href*="workflow.css"]'));
        }""")
        assert page.locator(".vaw-node").first.evaluate("element => getComputedStyle(element).animationName") == "vaw-test-motion"
        page.emulate_media(reduced_motion="reduce")
        assert page.locator(".vaw-node").first.evaluate("element => getComputedStyle(element).animationName") == "none"
        page.emulate_media(forced_colors="active")
        assert page.locator(".vaw-graph").first.evaluate("element => getComputedStyle(element).backgroundImage") == "none"
        assert chrome.evaluate("element=>getComputedStyle(element).backgroundImage") == "none"
        assert node_surface.evaluate("element=>getComputedStyle(element).boxShadow") == "none"
        page.emulate_media(forced_colors="none", reduced_motion="no-preference")

        print("Narrow embedding, enlarged text, reduced motion and forced colors passed.")
        # Exercise a real control inside a consuming application's form.
        page.evaluate("""() => {
            const studio = document.querySelector('.vaw-studio');
            const form = document.createElement('form');
            form.style.cssText = 'display:flex;flex:1 1 0;min-block-size:0;min-inline-size:0;block-size:100%';
            window.hostSubmissions = 0;
            form.addEventListener('submit', event => {event.preventDefault(); window.hostSubmissions++;});
            studio.replaceWith(form); form.append(studio);
        }""")
        def run_and_check(value, expected):
            start.click()
            dialog = designer.locator(".vaw-run-dialog")
            field = dialog.get_by_role("textbox", name="Workflow input", exact=True)
            field.fill(value)
            selected = designer.locator('.vaw-ide-run-table tr[aria-selected="true"] code')
            previous_run = selected.inner_text() if selected.count() else None
            dialog.get_by_role("button", name="Run workflow", exact=True).click()
            history = designer.get_by_label("Workflow run history", exact=True)
            row = history.locator('tr[aria-selected="true"]')
            if previous_run:
                expect(row).not_to_contain_text(previous_run)
            expect(row).to_contain_text("completed", timeout=20000)
            designer.get_by_role("button", name="Output", exact=True).click()
            expect(designer.locator(".vaw-workflow-result")).to_have_text(expected)

        run_and_check("form safety", "YTEFAS MROF")
        assert page.evaluate("window.hostSubmissions") == 0
        # Near-limit input must reach the server without disconnecting SignalR.
        run_and_check("x" * 16384, "X" * 16384)
        start.click()
        dialog = designer.locator(".vaw-run-dialog")
        field = dialog.get_by_role("textbox", name="Workflow input", exact=True)
        # Successful polling must not immediately erase a failed user action.
        field.evaluate("element => element.removeAttribute('maxlength')")
        field.fill("x" * 16385)
        field.press("Tab")
        dialog.get_by_role("button", name="Run workflow", exact=True).click()
        alert = dialog.get_by_role("alert").first
        expect(alert).to_be_visible()
        definition = page.request.get(BASE + "/api/templates/default").json()
        response = page.request.post(BASE + "/api/runs", data={"definition": definition, "input": "history refresh proof"})
        assert response.status == 202
        run_id = response.json()["header"]["runId"]
        designer.get_by_role("button", name="Runs", exact=True).click()
        history = designer.get_by_label("Workflow run history", exact=True)
        history.get_by_role("button", name="Refresh history", exact=True).click()
        expect(history.locator("tbody tr").filter(has_text=run_id[:8])).to_have_count(1)
        expect(alert).to_be_visible()

        designer.get_by_role("button", name="Workflow settings", exact=True).click()
        secondary = designer.locator("details.vaw-secondary-workflow-tools")
        if not secondary.evaluate("e => e.open"):
            secondary.locator(":scope > summary").click()
        workbench(designer).get_by_text("Import / export definition", exact=True).click()
        workbench(designer).get_by_role("button", name="Copy definition into JSON editor", exact=True).click()
        exported = workbench(designer).get_by_label("Workflow JSON", exact=True)
        expect(exported).not_to_have_value("")
        exported.fill(exported.input_value() + "\n" + " " * 40000)
        workbench(designer).get_by_role("button", name="Import JSON into draft", exact=True).click()
        workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for(state="attached")
        assert not errors, errors
        print("Host style isolation, theme tokens, contrast, keyboard focus, narrow embed, 200% text, motion/forced colors, form safety, field transport limits, and persistent errors passed.")
        print("Sampled text contrast ratios:", {name: round(ratio, 2) for name, ratio in ratios.items()})
    except Exception:
        with contextlib.suppress(Exception):
            page.screenshot(path="/tmp/vaw-ui-audit-failure.png", timeout=5000)
        raise
    finally:
        page.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable", help="Optional path to an installed Chromium")
    parser.add_argument("--screenshots", type=Path, help="Optional directory for review screenshots")
    args = parser.parse_args()
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        options = {"headless": True}
        if args.browser_executable:
            options["executable_path"] = args.browser_executable
        browser = playwright.chromium.launch(**options)
        try:
            check(browser, args.screenshots)
        finally:
            browser.close()
