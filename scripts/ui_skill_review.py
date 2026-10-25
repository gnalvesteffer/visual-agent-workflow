"""Browser checks for the Blazor/CSS skill fixes against the running local Studio.

Requires Playwright and Chromium. Uses only non-sensitive fixture text; creates
recorded development runs. Run studio_smoke.py separately for the full editor flow.
"""

from workbench_browser import workbench, inspect_control, show_runs

import argparse
import re
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

BASE = "http://127.0.0.1:5099"
THEMES = ("dark", "light", "vs-code-dark-plus", "solarized-dark", "nord", "monokai", "high-contrast")


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
        page.goto(BASE)
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
        theme_picker.select_option("dark")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'dark'")
        for theme in THEMES:
            theme_picker.select_option(theme)
            page.wait_for_function("theme => document.documentElement.dataset.vawTheme === theme", arg=theme)
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
                    input: pair('.vaw-sidebar textarea', '.vaw-sidebar textarea'),
                    notice: pair('.vaw-notice', '.vaw-studio')
                };
            }""")
            theme_ratios = {name: contrast(*colors) for name, colors in theme_samples.items()}
            assert all(ratio >= 4.5 for ratio in theme_ratios.values()), {theme: theme_ratios}
        theme_picker.select_option("dark")
        page.wait_for_function("document.documentElement.dataset.vawTheme === 'dark'")
        show_runs(page)
        start = page.get_by_role("button", name="Start independent run", exact=True)
        expect(start).to_be_enabled()
        designer = page.get_by_role("region", name="Workflow designer", exact=True)
        expect(workbench(designer).get_by_role("button", name="Save draft", exact=True)).to_be_enabled()
        assert page.locator("main main").count() == 0, "Reusable UI must not add nested main landmarks"
        assert page.locator(".vaw-studio button:not([type='button'])").count() == 0, "Embedded controls must not submit host forms"
        assert page.locator("head style").count() == 0, "Host styles belong in an external stylesheet"

        chrome=designer.locator(".vaw-canvas-toolbar")
        viewport=designer.locator(".vaw-canvas-viewport")
        assert "radial-gradient" in viewport.evaluate("element=>getComputedStyle(element).backgroundImage")
        assert viewport.evaluate("element=>getComputedStyle(element).backgroundColor") == "rgb(24, 24, 24)"
        assert viewport.evaluate("element=>getComputedStyle(element).backgroundColor") == page.locator(".vaw-app").evaluate("element=>getComputedStyle(element).backgroundColor"), "Dark Modern graph and shell surfaces must match"
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
                nested: getComputedStyle(document.querySelector('.vaw-designer')).backgroundColor,
                font: getComputedStyle(document.querySelector('.vaw-designer')).fontFamily,
                graph: getComputedStyle(document.querySelector('.vaw-canvas-viewport')).backgroundColor
            };
            studio.style.removeProperty('--vaw-color-surface');
            studio.style.removeProperty('--vaw-font-family');
            studio.style.removeProperty('--vaw-graph-color-canvas');
            return result;
        }""")
        assert themed["sidebar"] == themed["nested"] == "rgb(32, 52, 71)"
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
                input: pair('.vaw-sidebar textarea', '.vaw-sidebar textarea'),
                notice: pair('.vaw-notice', '.vaw-studio')
            };
        }""")
        ratios = {name: contrast(*colors) for name, colors in samples.items()}
        assert all(ratio >= 4.5 for ratio in ratios.values()), ratios
        show_runs(page)
        assert start.bounding_box()["height"] >= 44
        start.focus()
        page.keyboard.press("Tab")
        assert page.evaluate("document.activeElement.matches(':focus-visible') && getComputedStyle(document.activeElement).outlineStyle !== 'none'"), "Keyboard focus must remain visible"

        graph = workbench(designer).get_by_role("list", name="Editable pipeline nodes", exact=True)
        canvas = workbench(designer).get_by_label("Workflow graph canvas. Scroll or drag the background to pan.", exact=True)
        expect(workbench(designer).locator('.vaw-graph-editor')).to_have_attribute("data-ready", "true")
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
        assert "radial-gradient" in canvas.evaluate("element=>getComputedStyle(element).backgroundImage"), "The graph grid must fill the whole viewport"
        assert graph.locator('li').first.evaluate("element => getComputedStyle(element).position") == "absolute", "Canvas nodes must be freely positioned"
        canvas.focus()
        assert canvas.evaluate("element => element === document.activeElement"), "Scrollable graph must be keyboard focusable"
        assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "Wide graph must not expand the page"
        if screenshots:
            page.evaluate("window.scrollTo(0,0)")
            page.screenshot(path=str(screenshots / "desktop.png"))

        # A narrow embedded component must reflow even while the viewport stays wide.
        page.locator(".vaw-studio").evaluate("element => element.style.inlineSize = '360px'")
        dimensions = page.locator(".vaw-studio").evaluate("""element => ({
            width: element.clientWidth, content: element.scrollWidth,
            columns: getComputedStyle(element.querySelector('.vaw-workspace')).gridTemplateColumns.split(' ').length
        })""")
        assert dimensions["content"] <= dimensions["width"] + 1 and dimensions["columns"] == 1, dimensions
        assert canvas.evaluate("element => element.scrollWidth > element.clientWidth"), "Narrow hosts must contain graph scrolling"
        if screenshots:
            page.evaluate("window.scrollTo(0,0)")
            page.screenshot(path=str(screenshots / "narrow-embed.png"))
        page.locator(".vaw-studio").evaluate("element => element.style.removeProperty('inline-size')")

        page.set_viewport_size({"width": 390, "height": 844})
        page.evaluate("document.documentElement.style.fontSize = '200%'")
        assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "200% text must reflow on mobile"
        assert page.locator(".vaw-studio").evaluate("element => getComputedStyle(element).fontSize") == "32px"
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

        # Exercise a real control inside a consuming application's form.
        page.evaluate("""() => {
            const studio = document.querySelector('.vaw-studio');
            const form = document.createElement('form');
            window.hostSubmissions = 0;
            form.addEventListener('submit', event => {event.preventDefault(); window.hostSubmissions++;});
            studio.replaceWith(form); form.append(studio);
        }""")
        show_runs(page)
        page.get_by_role("textbox", name="Input", exact=True).fill("form safety")
        start.click()
        page.get_by_role("button", name="output core.output Completed", exact=True).click()
        expect(page.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("YTEFAS MROF")
        assert page.evaluate("window.hostSubmissions") == 0

        # Valid input near the domain limit must reach the server instead of disconnecting SignalR.
        field = page.get_by_role("textbox", name="Input", exact=True)
        field.fill("x" * 16384)
        field.press("Tab")
        viewer = page.get_by_role("region", name="Workflow run viewer")
        previous_run = re.search(r"Run ([0-9a-f-]{36})", viewer.inner_text()).group(1)
        start.click()
        # The old completed output remains clickable until admission selects the new run.
        # Wait for that identity change before inspecting, including with slower storage.
        expect(viewer).not_to_contain_text(previous_run)
        page.get_by_role("button", name="output core.output Completed", exact=True).click()
        expect(page.get_by_role("region", name="Selected node payloads").locator("pre").last).to_have_text("X" * 16384)

        # Successful polling must not immediately erase a failed user action.
        field.evaluate("element => element.removeAttribute('maxlength')")
        field.fill("x" * 16385)
        field.press("Tab")
        start.click()
        alert = page.get_by_role("alert").filter(has_text="The run could not be started")
        expect(alert).to_be_visible()
        definition = page.request.get(BASE + "/api/templates/default").json()
        response = page.request.post(BASE + "/api/runs", data={"definition": definition, "input": "history refresh proof"})
        assert response.status == 202
        run_id = response.json()["header"]["runId"]
        page.locator(".vaw-history button").filter(has_text=run_id[:8]).wait_for()
        expect(alert).to_be_visible()

        workbench(designer).get_by_text("Import / export definition", exact=True).click()
        workbench(designer).get_by_role("button", name="Copy definition into JSON editor", exact=True).click()
        exported = workbench(designer).get_by_label("Workflow JSON", exact=True)
        expect(exported).not_to_have_value("")
        exported.fill(exported.input_value() + "\n" + " " * 40000)
        workbench(designer).get_by_role("button", name="Import JSON into draft", exact=True).click()
        workbench(designer).get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for()
        assert not errors, errors
        print("Host style isolation, theme tokens, contrast, keyboard focus, narrow embed, 200% text, motion/forced colors, form safety, field transport limits, and persistent errors passed.")
        print("Sampled text contrast ratios:", {name: round(ratio, 2) for name, ratio in ratios.items()})
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
