"""Check process-owned pause/resume through APIs and real Studio controls.

Creates only disposable deterministic local workflows/runs. Pause is cooperative
between nodes; this does not test or claim crash-resumable checkpoints.
"""

from workbench_browser import workbench, inspect_control, show_runs
import argparse
import copy
from pathlib import Path
import re
import uuid
from playwright.sync_api import sync_playwright, expect

BASE = "http://127.0.0.1:5099"


def check(browser, screenshots):
    page = browser.new_page(viewport={"width":1440,"height":1100})
    errors = []
    page.on("pageerror",lambda error: errors.append(str(error)))

    def get(path):
        response=page.request.get(BASE+path)
        assert response.ok,response.text()
        return response.json()

    def post(path,body=None,status=202):
        response=page.request.post(BASE+path,data=body)
        assert response.status==status,(path,response.status,response.text())
        return response.json() if response.text() else None

    definition=get("/api/templates/default")
    paused=post("/api/runs",{"definition":definition,"input":"api pause","startPaused":True})
    first_id=paused["header"]["runId"]
    assert paused["status"]=="paused"
    assert not any(event["kind"]=="node.started" for event in paused["events"])
    post(f"/api/runs/{first_id}/pause")
    post(f"/api/runs/{first_id}/pause")
    assert sum(e["kind"]=="run.pause-requested" for e in get(f"/api/runs/{first_id}")["events"])==1

    def ready():
        page.goto(BASE)
        show_runs(page)
        expect(page.get_by_role("button",name="Start independent run",exact=True)).to_be_enabled()

    def select(run_id):
        page.locator(".vaw-history button").filter(has_text=run_id[:8]).click()
        expect(page.get_by_role("button",name="Resume selected run",exact=True)).to_be_enabled()

    ready(); select(first_id)
    viewer=page.get_by_role("region",name="Workflow run viewer",exact=True)
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: paused")
    expect(viewer.get_by_role("button",name="input core.input Paused before execution",exact=True)).to_be_visible()

    # Another run must complete even while this continuation stays paused.
    other=post("/api/runs",{"definition":definition,"input":"other run"})
    other_id=other["header"]["runId"]
    page.wait_for_function("""async id => {
        const response=await fetch('/api/runs/'+id); return (await response.json()).status==='completed';
    }""",arg=other_id)
    assert get(f"/api/runs/{first_id}")["status"]=="paused"
    # A browser reload loses its UI instance, not the backend pause/continuation.
    ready(); select(first_id)
    page.get_by_role("button",name="Resume selected run",exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("ESUAP IPA")
    finished=get(f"/api/runs/{first_id}")
    assert finished["header"]["revisionId"]==paused["header"]["revisionId"]
    assert sum(e["kind"]=="node.started" for e in finished["events"])==4
    before=len(finished["events"])
    post(f"/api/runs/{first_id}/resume",status=409)
    post(f"/api/runs/{first_id}/pause",status=409)
    assert len(get(f"/api/runs/{first_id}")["events"])==before
    post(f"/api/runs/{uuid.uuid4()}/pause",status=404)
    post(f"/api/runs/{uuid.uuid4()}/resume",status=404)
    rejected=page.request.post(BASE+f"/api/runs/{first_id}/pause",headers={"Origin":"https://untrusted.example"})
    assert rejected.status==403

    # Start-paused UI makes a fast workflow controllable without racing its execution.
    page.get_by_label("Input",exact=True).fill("ui pause")
    page.get_by_label("Start paused",exact=True).check()
    page.get_by_role("button",name="Start independent run",exact=True).click()
    expect(page.get_by_role("button",name="Resume selected run",exact=True)).to_be_enabled()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: paused")
    ui_id=re.search(r"Run ([0-9a-f-]{36})",viewer.locator(".vaw-run-header").inner_text())[1]
    assert not any(e["kind"]=="node.started" for e in get(f"/api/runs/{ui_id}")["events"])
    ready(); select(ui_id)
    page.get_by_role("button",name="Resume selected run",exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("ESUAP IU")

    # Cancellation is available while paused and must not require a resume.
    page.get_by_label("Start paused",exact=True).check()
    page.get_by_role("button",name="Start independent run",exact=True).click()
    expect(page.get_by_role("button",name="Resume selected run",exact=True)).to_be_enabled()
    cancel_id=re.search(r"Run ([0-9a-f-]{36})",viewer.locator(".vaw-run-header").inner_text())[1]
    page.get_by_role("button",name="Cancel selected run",exact=True).click()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: cancelled")
    cancelled=get(f"/api/runs/{cancel_id}")
    assert not any(e["kind"]=="node.started" for e in cancelled["events"])

    # A published paused run retains its exact revision even after activation changes.
    published_definition=copy.deepcopy(definition)
    workflow_id="pause-published-"+uuid.uuid4().hex[:12]
    published_definition["workflowId"]=workflow_id
    path="/api/workflows/"+workflow_id
    response=page.request.put(BASE+path+"/draft",data={"definition":published_definition,"expectedGeneration":0})
    assert response.ok,response.text()
    v1=post(path+"/publish",{"expectedGeneration":response.json()["generation"]},status=200)
    revision_id=v1["revision"]["revisionId"]
    pinned=post(f"/api/revisions/{revision_id}/runs",{"input":"abc","startPaused":True})
    changed=copy.deepcopy(published_definition);changed["nodes"]["step-1"]["type"]="core.text.reverse"
    generation=get(path)["generation"]
    response=page.request.put(BASE+path+"/draft",data={"definition":changed,"expectedGeneration":generation})
    assert response.ok,response.text()
    post(path+"/publish",{"expectedGeneration":response.json()["generation"]},status=200)
    pinned_id=pinned["header"]["runId"]
    post(f"/api/runs/{pinned_id}/resume")
    page.wait_for_function("""async id => (await (await fetch('/api/runs/'+id)).json()).status==='completed'""",arg=pinned_id)
    done=get(f"/api/runs/{pinned_id}")
    assert done["header"]["revisionId"]==revision_id
    assert done["events"][-1]["data"]=="CBA"

    ready()
    designer=page.get_by_role("region",name="Workflow designer",exact=True)
    expect(workbench(designer).get_by_role("button",name="Save draft",exact=True)).to_be_enabled()
    workbench(designer).get_by_label("Saved workflow",exact=True).select_option(workflow_id)
    workbench(designer).get_by_label("Published version",exact=True).select_option(revision_id)
    workbench(designer).get_by_label("Start published run paused",exact=True).check()
    workbench(designer).get_by_label("Published run input",exact=True).fill("published pause")
    workbench(designer).get_by_role("button",name="Run selected version",exact=True).click()
    expect(page.get_by_role("button",name="Resume selected run",exact=True)).to_be_enabled()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: paused")
    published_ui_id=re.search(r"Run ([0-9a-f-]{36})",viewer.locator(".vaw-run-header").inner_text())[1]
    recorded=get(f"/api/runs/{published_ui_id}")
    assert recorded["header"]["revisionId"]==revision_id
    if screenshots:
        viewer.screenshot(path=str(screenshots/"paused-run.png"))
    page.get_by_role("button",name="Resume selected run",exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("ESUAP DEHSILBUP")
    recorded=get(f"/api/runs/{published_ui_id}")
    pause_sequence=next(e["sequence"] for e in recorded["events"] if e["kind"]=="run.paused")
    slider=viewer.get_by_label("Recorded position",exact=True)
    slider.evaluate("""(input,position)=>{input.value=position;input.dispatchEvent(new Event('input',{bubbles:true}));}""",pause_sequence)
    expect(viewer.get_by_text("State at recorded position: paused",exact=True)).to_be_visible()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: completed")
    assert get(f"/api/runs/{published_ui_id}")==recorded,"Playback changed execution evidence"
    assert not errors,errors
    print("Pause/resume API, paused start, independent runs, browser reload, UI resume/cancel, pinned publication, terminal/unknown/origin rejection, and recorded pause playback passed.")
    page.close()


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--screenshots",type=Path)
    args=parser.parse_args()
    if args.screenshots:args.screenshots.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as p:
        options={"headless":True}
        if args.browser_executable:options["executable_path"]=args.browser_executable
        browser=p.chromium.launch(**options)
        try:check(browser,args.screenshots)
        finally:browser.close()
