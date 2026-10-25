"""Shared browser gestures for the Studio's view switcher and selected-node inspector."""
import re


def workbench(designer):
    tab = designer.page.get_by_role("button", name="Designer", exact=True)
    if tab.get_attribute("aria-pressed") != "true":
        tab.click()
    from playwright.sync_api import expect
    expect(designer).to_be_visible()
    return designer


def inspect_control(designer, label):
    designer = workbench(designer)
    ids = designer.locator("li[data-node-id]").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)")
    matches = [node_id for node_id in ids if re.search(r"(?:^| )" + re.escape(node_id) + r"(?:$|[ .])", label)]
    if matches:
        designer.get_by_label("Node to organize", exact=True).select_option(max(matches, key=len))
    if label.startswith("New node operation") or label.startswith(("Add node after ", "Add conditional after ", "Add condition after ")):
        summary=designer.get_by_text("Insertion options",exact=True)
        if not summary.evaluate("element=>element.parentElement.open"): summary.click()
    return designer


def show_runs(page):
    page.get_by_role("button", name="Run inspector", exact=True).click()


def new_workflow_fixture(designer):
    """Import the explicit three-node fixture used by legacy configuration tests.

    Creation itself is tested from the blank canvas in workbench_smoke.py.
    """
    import json
    from playwright.sync_api import expect
    designer = workbench(designer)
    designer.get_by_role("button", name="New workflow", exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.", exact=True).wait_for()
    definition = {
        "schemaVersion":"1.0", "workflowId":"fixture", "entryNodeId":"input",
        "nodes":{name:{"type":kind,"typeVersion":"1.0.0"} for name,kind in
                 (("input","core.input"),("step-1","core.text.uppercase"),("output","core.output"))},
        "connections":{
            "link-1":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"step-1","portId":"in"},"kind":"data"},
            "link-output":{"source":{"nodeId":"step-1","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}}}
    details=designer.get_by_text("Import / export definition",exact=True).locator("..")
    if not details.evaluate("element=>element.open"): details.locator("summary").click()
    designer.get_by_label("Workflow JSON",exact=True).fill(json.dumps(definition))
    designer.get_by_role("button",name="Import JSON into draft",exact=True).click()
    designer.get_by_text("Definition imported locally. Save and publish explicitly.",exact=True).wait_for()
    expect(designer.locator("li[data-node-id]")).to_have_count(3)
    details.locator("summary").click()
