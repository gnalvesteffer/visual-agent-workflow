"""Shared browser gestures for the Studio's view switcher and selected-node inspector."""
import re


def workbench(designer):
    page = designer.page
    back = page.get_by_role("button", name="Back to workflow", exact=True)
    if back.is_visible():
        back.click()
    else:
        tab = page.get_by_role("button", name="Designer", exact=True)
        if tab.is_visible() and tab.get_attribute("aria-pressed") != "true":
            tab.click()
    from playwright.sync_api import expect
    expect(designer).to_be_visible()
    return designer


def inspect_control(designer, label):
    designer = workbench(designer)
    ids = designer.locator("li[data-node-id]").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)")
    matches = [node_id for node_id in ids if re.search(r"(?:^| )" + re.escape(node_id) + r"(?:$|[ .])", label)]
    if matches:
        node_id = max(matches, key=len)
        designer.locator(f'li[data-node-id="{node_id}"] [data-drag-node]').click()
    if label.startswith("New node operation") or label.startswith(("Add node after ", "Add conditional after ", "Add condition after ")):
        summary=designer.get_by_text("Insertion options",exact=True)
        if not summary.evaluate("element=>element.parentElement.open"): summary.click()
    return designer


def show_runs(page):
    from playwright.sync_api import expect
    legacy = page.get_by_role("button", name="Run inspector", exact=True)
    if legacy.is_visible():
        legacy.click()
    else:
        page.get_by_role("button", name="Configure test input", exact=True).click()
        page.get_by_role("button", name="Open run inspector", exact=True).click()
    expect(page.get_by_role("region", name="Workflow run viewer", exact=True)).to_be_visible()


def new_workflow_fixture(designer):
    """Import the explicit three-node fixture used by legacy configuration tests.

    Creation itself is tested from the blank canvas in workbench_smoke.py.
    """
    import json
    from playwright.sync_api import expect
    designer = workbench(designer)
    settings = designer.get_by_role("button", name="Workflow settings", exact=True)
    if settings.is_visible() and settings.get_attribute("aria-expanded") != "true":
        settings.click()
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text(re.compile("^New workflow created")).wait_for(state="attached")
    definition = {
        "schemaVersion":"1.0", "workflowId":"fixture", "entryNodeId":"input",
        "nodes":{name:{"type":kind,"typeVersion":"1.0.0"} for name,kind in
                 (("input","core.input"),("step-1","core.text.uppercase"),("output","core.output"))},
        "connections":{
            "link-1":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"step-1","portId":"in"},"kind":"data"},
            "link-output":{"source":{"nodeId":"step-1","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}}}
    secondary = designer.locator("details.vaw-secondary-workflow-tools")
    if secondary.count() and not secondary.evaluate("element => element.open"):
        secondary.locator(":scope > summary").click()
    details=designer.get_by_text("Import / export definition",exact=True).locator("..")
    if not details.evaluate("element=>element.open"): details.locator("summary").click()
    designer.get_by_label("Workflow JSON",exact=True).fill(json.dumps(definition))
    designer.get_by_role("button",name="Import JSON into draft",exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.",exact=True).wait_for(state="attached")
    expect(designer.locator("li[data-node-id]")).to_have_count(3)
    details.locator("summary").click()
    if settings.is_visible() and settings.get_attribute("aria-expanded") == "true":
        settings.click()


def current_project(page):
    """The named titlebar button exposes its current project to assistive technology."""
    return page.get_by_role("button", name=re.compile(r"^Current project:"))
