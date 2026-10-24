"""Check reusable publications and portable packages through the actual Studio.

Uses only disposable local fixtures. Imports/copies do not invoke providers/tools;
only explicit run controls execute the deterministic local workflow.
"""
import argparse
import copy
import json
from pathlib import Path
import uuid
from playwright.sync_api import sync_playwright, expect

BASE = "http://127.0.0.1:5099"


def check(browser, screenshots):
    page = browser.new_page(viewport={"width":1600,"height":1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    suffix = uuid.uuid4().hex[:12]
    source_id, target_id = "reuse-source-"+suffix, "reuse-consumer-"+suffix

    def get(path):
        response = page.request.get(BASE+path)
        assert response.ok, response.text()
        return response.json()

    def mutate(path, body, method="post"):
        response = getattr(page.request,method)(BASE+path,data=body)
        assert response.ok, response.text()
        return response.json()

    template = get("/api/templates/default")
    source = copy.deepcopy(template); source["workflowId"] = source_id
    path = "/api/workflows/"+source_id
    mutate(path+"/draft",{"definition":source,"expectedGeneration":0},"put")
    design = get(path+"/design")
    layout = design["layout"]
    layout["nodes"]["step-1"]["label"] = "Normalize for reuse"
    layout["nodes"]["step-1"]["comment"] = "Keep this annotation with the published source."
    layout["sections"] = {"processing":{"label":"Reusable text processing","nodeIds":["step-1","step-2"]}}
    saved = mutate(path+"/design",{"definition":source,"layout":layout,"expectedGeneration":design["draft"]["generation"]},"put")
    v1 = mutate(path+"/publish",{"expectedGeneration":saved["draft"]["generation"]})
    updated = copy.deepcopy(source); updated["nodes"]["step-2"]["type"] = "core.text.uppercase"
    current = get(path)
    saved = mutate(path+"/draft",{"definition":updated,"expectedGeneration":current["generation"]},"put")
    mutate(path+"/publish",{"expectedGeneration":saved["generation"]})
    target = copy.deepcopy(template); target["workflowId"] = target_id
    mutate("/api/workflows/"+target_id+"/draft",{"definition":target,"expectedGeneration":0},"put")

    page.goto(BASE)
    designer = page.get_by_role("region",name="Workflow designer",exact=True)
    expect(designer.get_by_role("button",name="Save draft",exact=True)).to_be_enabled()
    designer.get_by_label("Saved workflow",exact=True).select_option(target_id)
    expect(designer.locator(".vaw-definition-status")).to_contain_text(target_id)

    def open_details(label):
        summary = designer.get_by_text(label,exact=True)
        if not summary.evaluate("element=>element.closest('details').open"):
            summary.click()

    def save():
        designer.get_by_role("button",name="Save draft",exact=True).click()
        designer.get_by_text("Draft saved.",exact=True).wait_for()

    def publish(version):
        designer.get_by_role("button",name="Publish & activate",exact=True).click()
        designer.get_by_text(f"Published and activated version {version}.",exact=True).wait_for()

    open_details("Reusable pipelines")
    designer.get_by_label("Reusable workflow",exact=True).select_option(source_id)
    expect(designer.get_by_label("Reusable version",exact=True).locator("option")).to_have_count(2)
    designer.get_by_label("Reusable version",exact=True).select_option(v1["revision"]["revisionId"])
    designer.get_by_label("Insert pipeline after",exact=True).select_option("input")
    designer.get_by_role("button",name="Insert published pipeline",exact=True).click()
    designer.get_by_role("status").filter(has_text="Inserted version 1").wait_for()
    nodes = designer.locator("ol[aria-label='Editable pipeline nodes'] > li")
    expect(nodes).to_have_count(6)
    designer.get_by_role("button",name="Undo edit",exact=True).click()
    expect(nodes).to_have_count(4)
    designer.get_by_role("button",name="Redo edit",exact=True).click()
    expect(nodes).to_have_count(6)
    save()
    consumer = get("/api/workflows/"+target_id+"/design")
    copies = {node_id:node for node_id,node in consumer["draft"]["definition"]["nodes"].items() if node.get("origin")}
    assert len(copies)==2
    assert all(node_id not in source["nodes"] for node_id in copies)
    assert all(node["origin"]["revisionId"]==v1["revision"]["revisionId"] and node["origin"]["version"]==1 for node in copies.values())
    assert any(node["comment"].startswith("Keep this annotation") for node in consumer["layout"]["nodes"].values())
    publish(1)
    designer.get_by_label("Published run input",exact=True).fill("abc")
    designer.get_by_role("button",name="Run selected version",exact=True).click()
    viewer = page.get_by_role("region",name="Workflow run viewer",exact=True)
    viewer.get_by_role("button",name="output core.output Completed",exact=True).click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("ABC")
    revision_id = get("/api/workflows/"+target_id+"/revisions")[0]["revision"]["revisionId"]
    package_api = get("/api/revisions/"+revision_id+"/package")
    assert package_api["definition"]==consumer["draft"]["definition"]
    assert package_api["layout"]==consumer["layout"]

    open_details("Import / export definition")
    designer.get_by_role("button",name="Copy workflow package into JSON editor",exact=True).click()
    editor = designer.get_by_label("Workflow JSON",exact=True)
    expect(editor).not_to_have_value("")
    package = json.loads(editor.input_value())
    assert package == package_api
    assert any(r["id"]=="core.text.reverse" for r in package["requirements"])
    designer.get_by_role("button",name="New workflow",exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.",exact=True).wait_for()
    imported_id = designer.get_by_label("Saved workflow",exact=True).input_value()
    open_details("Import / export definition")
    editor.fill(json.dumps(package) + "\n" + " " * (1024 * 1024 + 40000))
    designer.get_by_role("button",name="Import package into draft",exact=True).click()
    designer.get_by_role("status").filter(has_text="Package imported locally").wait_for()
    expect(nodes).to_have_count(6)
    assert not any(run["header"]["definition"]["workflowId"]==imported_id for run in get("/api/runs")), "Import executed the graph"
    save()
    imported = get("/api/workflows/"+imported_id+"/design")
    assert imported["draft"]["definition"]["workflowId"]==imported_id
    assert imported["layout"]==package["layout"]
    assert imported["draft"]["definition"]["nodes"]==package["definition"]["nodes"]
    tampered = copy.deepcopy(package)
    tampered["layout"]["nodes"]["input"]["comment"]="Mismatched checksum"
    editor.fill(json.dumps(tampered))
    designer.get_by_role("button",name="Import package into draft",exact=True).click()
    expect(designer.get_by_role("alert")).to_contain_text("Invalid workflow, layout, or JSON")
    assert get("/api/workflows/"+imported_id+"/design")==imported

    # Extract the copied labeled section without replacing or publishing the current draft.
    open_details("Reusable pipelines")
    section_id = next(iter(imported["layout"]["sections"]))
    reusable_id = "extracted-"+suffix
    designer.get_by_label("Section to save",exact=True).select_option(section_id)
    designer.get_by_label("Reusable pipeline ID",exact=True).fill(reusable_id)
    designer.get_by_role("button",name="Save section as reusable draft",exact=True).click()
    designer.get_by_role("status").filter(has_text=f"Reusable draft '{reusable_id}' created").wait_for()
    expect(designer.get_by_label("Saved workflow",exact=True)).to_have_value(imported_id)
    extracted = get("/api/workflows/"+reusable_id+"/design")
    assert len(extracted["draft"]["definition"]["nodes"])==4
    assert extracted["draft"]["activeRevisionId"] is None
    assert get("/api/workflows/"+imported_id+"/design")==imported
    if screenshots:
        designer.locator(".vaw-reuse-panel").screenshot(path=str(screenshots/"reusable-pipelines.png"))

    # A correctly checksummed package can declare unavailable tools, but cannot grant them.
    missing = copy.deepcopy(template); missing["workflowId"]="missing-tool-"+suffix; missing["schemaVersion"]="1.1"
    missing["nodes"]["step-1"]={"type":"core.mcp-tool","typeVersion":"1.0.0","tool":{"bindingId":"unregistered.tool","contractHash":"a"*64}}
    missing_path="/api/workflows/"+missing["workflowId"]
    mutate(missing_path+"/draft",{"definition":missing,"expectedGeneration":0},"put")
    missing_package=get(missing_path+"/package")
    designer.get_by_role("button",name="New workflow",exact=True).click()
    designer.get_by_text("New workflow created locally. Save its draft before publishing.",exact=True).wait_for()
    denied_id=designer.get_by_label("Saved workflow",exact=True).input_value()
    open_details("Import / export definition")
    editor.fill(json.dumps(missing_package))
    designer.get_by_role("button",name="Import package into draft",exact=True).click()
    designer.get_by_role("status").filter(has_text="Package imported locally").wait_for()
    expect(designer.locator(".vaw-requirements").filter(has_text="unregistered.tool")).to_contain_text("Matching host binding not listed")
    save(); publish(1)
    designer.get_by_role("button",name="Run selected version",exact=True).click()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: failed")
    denied_run=next(run for run in get("/api/runs") if run["header"]["definition"]["workflowId"]==denied_id)
    evidence=get("/api/runs/"+denied_run["header"]["runId"])
    assert any(event["kind"]=="tool.denied" for event in evidence["events"])
    assert not any(event["kind"] in ["tool.authorized","tool.completed"] for event in evidence["events"])
    assert not errors, errors
    print("Published-version insertion, fresh identities, undo/redo, exact output, large package transport/sharing/tamper rejection, section extraction, selector identity, and missing-tool denial passed.")
    page.close()


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots",type=Path)
    args=parser.parse_args()
    if args.screenshots: args.screenshots.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as p:
        options={"headless":True}
        if args.browser_executable: options["executable_path"]=args.browser_executable
        browser=p.chromium.launch(**options)
        try: check(browser,args.screenshots)
        finally: browser.close()
