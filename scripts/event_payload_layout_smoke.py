"""Verify node-event playback keeps payloads readable inside a scrollable table."""
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright


def check(executable):
    root = Path(__file__).resolve().parents[1]
    css = (root / "src/VisualAgentWorkflow.Blazor/wwwroot/workflow.css").read_text()
    html = """<!doctype html><style>""" + css + """</style><main class="vaw-viewer">
<dialog class="vaw-node-events-dialog" open><header><h2>Proponent events</h2></header>
<div class="vaw-table-scroll vaw-node-events-table"><table><caption>Events for Proponent</caption>
<colgroup><col class="vaw-event-sequence-column"><col class="vaw-event-time-column"><col class="vaw-event-kind-column"><col class="vaw-node-events-scope-column"><col class="vaw-node-events-data-column"><col class="vaw-event-identity-column"><col class="vaw-event-identity-column"></colgroup>
<thead><tr><th>Sequence</th><th>Time (local)</th><th>Event</th><th>Scope</th><th>Data</th><th>Invocation</th><th>Connection</th></tr></thead>
<tbody><tr><td>19</td><td>15:34:44.599</td><td>execution.activated</td><td>root</td><td><pre>{"source":"research","text":"A long recorded payload must remain readable and must not force the Data column to collapse into a narrow strip."}</pre></td><td><code>0950e699-d09d-4420-b92a-1083aaaf5eb6</code></td><td><code>connection-identity</code></td></tr></tbody>
</table></div></dialog></main>"""
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, **({"executable_path": executable} if executable else {}))
        try:
            page = browser.new_page(viewport={"width": 1280, "height": 900})
            page.set_content(html)
            table = page.locator(".vaw-node-events-dialog table")
            assert table.evaluate("element => element.scrollWidth >= 1400")
            assert table.locator("col.vaw-node-events-data-column").evaluate("element => parseFloat(getComputedStyle(element).width)") >= 300
            assert table.get_by_role("columnheader", name="Data", exact=True).evaluate("element => getComputedStyle(element).whiteSpace") == "nowrap"
            assert table.locator("tbody td code").first.evaluate("element => getComputedStyle(element).textOverflow") == "ellipsis"
            assert page.locator(".vaw-node-events-table").evaluate("element => element.scrollLeft") == 0
            assert table.locator("col.vaw-node-events-data-column").evaluate("element => { const col=element.getBoundingClientRect(); const scroller=element.closest('.vaw-node-events-table').getBoundingClientRect(); return col.left >= scroller.left && col.right <= scroller.right; }")
            page.screenshot(path="/tmp/vaw-event-payload-layout.png")
            print("Node-event payload layout passed: fixed readable data column, wrapped payload, horizontal table scrolling")
        finally:
            browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    check(parser.parse_args().browser_executable)
