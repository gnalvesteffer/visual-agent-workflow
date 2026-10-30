"""Browser regression for nested designer/canvas pane controllers; uses no Studio data."""
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright, expect


HTML = """<!doctype html><style>
body { margin: 0; }
.shell { width: 1200px; }
.vaw-canvas-workspace { display: grid; grid-template-columns: 1fr 8px var(--vaw-inspector-width, 304px); height: 300px; }
.vaw-pane-resizer { width: 8px; padding: 0; border: 0; background: #aaa; touch-action: none; z-index: 2; }
.vaw-bottom-tray { height: var(--vaw-bottom-height, 192px); }
</style><main class="shell" id="designer"><section id="canvas">
<div class="vaw-canvas-workspace"><div>Graph</div>
<button class="vaw-pane-resizer" data-pane-resizer="inspector" aria-label="Resize node inspector" aria-valuenow="304"></button><aside>Inspector</aside></div>
</section><button class="vaw-pane-resizer" data-pane-resizer="bottom" aria-label="Resize details" aria-valuenow="192">Details</button>
<div class="vaw-bottom-tray"></div></main>"""


def check(executable):
    source = (Path(__file__).resolve().parents[1] / "src/VisualAgentWorkflow.Blazor/wwwroot/paneLayout.js").read_text()
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, **({"executable_path": executable} if executable else {}))
        try:
            page = browser.new_page(viewport={"width": 1400, "height": 900})
            errors = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            page.route("http://pane-layout.test/", lambda route: route.fulfill(content_type="text/html", body=HTML))
            page.route("http://pane-layout.test/paneLayout.js", lambda route: route.fulfill(content_type="text/javascript", body=source))
            page.goto("http://pane-layout.test/")
            # Break an observer feedback loop before it can hang the test browser.
            # Production observers still run normally until this failure bound.
            page.evaluate("""() => {
                const NativeObserver = window.MutationObserver;
                window.paneMutationCount = 0;
                window.MutationObserver = class extends NativeObserver {
                    constructor(callback) { super((records, observer) => {
                        if (++window.paneMutationCount > 100) {
                            window.paneObserverLoop = true; observer.disconnect(); return;
                        }
                        callback(records, observer);
                    }); }
                };
            }""")
            page.evaluate("""async () => {
                window.panes = await import('/paneLayout.js');
                window.outer = panes.attach(document.querySelector('#designer'));
                window.inner = panes.attach(document.querySelector('#canvas'));
            }""")
            inspector = page.get_by_role("button", name="Resize node inspector")
            box = inspector.bounding_box()
            assert page.evaluate("""() => { const e=document.querySelector('[data-pane-resizer=inspector]'); const r=e.getBoundingClientRect(); return document.elementFromPoint(r.x+r.width/2,r.y+r.height/2) === e; }"""), "The splitter handle must receive pointer input across its hit area."
            x, y = box["x"] + box["width"] / 2, box["y"] + box["height"] / 2
            page.mouse.move(x, y)
            page.mouse.down()
            page.mouse.move(x - 64, y, steps=8)
            page.mouse.up()
            page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")
            assert not page.evaluate("window.paneObserverLoop || false"), "Nested pane observers entered an unbounded mutation loop"
            expect(inspector).to_have_attribute("aria-valuenow", "368")
            assert page.locator("aside").bounding_box()["width"] == 368
            assert page.evaluate("JSON.parse(localStorage.getItem('visual-agent-workflow.layout.v1')).layout.inspector") == 368
            inspector.focus()
            inspector.press("ArrowLeft")
            expect(inspector).to_have_attribute("aria-valuenow", "352")
            # Updating a different pane must preserve the inspector preference.
            bottom = page.get_by_role("button", name="Resize details")
            bottom.focus()
            bottom.press("ArrowUp")
            expect(bottom).to_have_attribute("aria-valuenow", "208")
            expect(inspector).to_have_attribute("aria-valuenow", "352")
            assert page.evaluate("JSON.parse(localStorage.getItem('visual-agent-workflow.layout.v1')).layout.inspector") == 352
            # Blazor can replace a handle with its declarative default value.
            page.evaluate("""() => {
                const handle = document.querySelector('[data-pane-resizer=inspector]');
                const replacement = handle.cloneNode(true);
                replacement.setAttribute('aria-valuenow', '304');
                handle.replaceWith(replacement);
            }""")
            expect(inspector).to_have_attribute("aria-valuenow", "352")
            page.evaluate("() => { outer.dispose(); inner.dispose(); inner = panes.attach(document.querySelector('#canvas')); outer = panes.attach(document.querySelector('#designer')); }")
            expect(inspector).to_have_attribute("aria-valuenow", "352")
            inspector.focus()
            inspector.press("ArrowRight")
            expect(inspector).to_have_attribute("aria-valuenow", "368")
            page.evaluate("() => window.dispatchEvent(new Event('vaw-layout-reset'))")
            expect(inspector).to_have_attribute("aria-valuenow", "304")
            expect(bottom).to_have_attribute("aria-valuenow", "192")
            assert not page.evaluate("window.paneObserverLoop || false")
            assert not errors, errors
            print("Pane resize regression passed: nested ownership, drag, keyboard, persistence, handle replacement, reattach, reset")
        finally:
            browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    check(parser.parse_args().browser_executable)
