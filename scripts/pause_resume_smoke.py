"""Check process-owned pause/resume and sequential debugger controls.

Creates only disposable deterministic local workflows/runs. Pause is cooperative
between nodes; this does not test or claim crash-resumable checkpoints.
"""

import argparse
import copy
from pathlib import Path
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
        expect(page.locator(".vaw-ide-run-table")).to_be_visible()

    def select(run_id):
        page.get_by_role("button",name=f"Inspect run {run_id}",exact=True).click()
        expect(page.get_by_role("region",name="Workflow run viewer",exact=True)).to_be_visible()

    ready(); select(first_id)
    viewer=page.get_by_role("region",name="Workflow run viewer",exact=True)
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: paused")
    expect(viewer.get_by_role("button",name="input core.input Paused before execution",exact=True)).to_be_visible()
    viewer.get_by_role("button",name="Set breakpoint on output",exact=True).click()
    viewer.get_by_role("button",name="Continue",exact=True).click()
    page.wait_for_function("""async id => {
        const run=await (await fetch('/api/runs/'+id)).json();
        return run.status==='paused' && run.events.some(event=>event.kind==='debug.breakpoint.hit' && event.nodeId==='output');
    }""",arg=first_id)
    assert not any(e["kind"]=="node.started" and e["nodeId"]=="output" for e in get(f"/api/runs/{first_id}")["events"])
    viewer.get_by_role("button",name="Step node",exact=True).click()
    page.wait_for_function("""async id => {
        const run=await (await fetch('/api/runs/'+id)).json();
        return run.status==='paused' && run.events.some(event=>event.kind==='node.completed' && event.nodeId==='output');
    }""",arg=first_id)
    assert not any(e["kind"]=="run.completed" for e in get(f"/api/runs/{first_id}")["events"])

    # Another run must complete even while this continuation stays paused.
    other=post("/api/runs",{"definition":definition,"input":"other run"})
    other_id=other["header"]["runId"]
    page.wait_for_function("""async id => {
        const response=await fetch('/api/runs/'+id); return (await response.json()).status==='completed';
    }""",arg=other_id)
    assert get(f"/api/runs/{first_id}")["status"]=="paused"
    # A browser reload loses its UI instance, not the backend pause/continuation.
    ready(); select(first_id)
    page.get_by_role("button",name="Continue",exact=True).click()
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("ESUAP IPA")
    finished=get(f"/api/runs/{first_id}")
    assert finished["header"]["revisionId"]==paused["header"]["revisionId"]
    admitted_nodes={e["nodeId"] for e in finished["events"] if e["kind"]=="node.started"}
    assert admitted_nodes=={"input","output"},admitted_nodes
    before=len(finished["events"])
    post(f"/api/runs/{first_id}/resume",status=409)
    post(f"/api/runs/{first_id}/pause",status=409)
    assert len(get(f"/api/runs/{first_id}")["events"])==before
    post(f"/api/runs/{uuid.uuid4()}/pause",status=404)
    post(f"/api/runs/{uuid.uuid4()}/resume",status=404)
    rejected=page.request.post(BASE+f"/api/runs/{first_id}/pause",headers={"Origin":"https://untrusted.example"})
    assert rejected.status==403

    # Cancellation is available while paused and does not require a resume.
    cancel=post("/api/runs",{"definition":definition,"input":"cancel","startPaused":True})
    cancel_id=cancel["header"]["runId"]
    ready(); select(cancel_id)
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

    ready(); select(pinned_id)
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: completed")
    recorded=get(f"/api/runs/{pinned_id}")
    assert recorded["header"]["revisionId"]==revision_id
    if screenshots:
        viewer.screenshot(path=str(screenshots/"debugger-playback.png"))
    viewer.locator('li[data-node-id="output"] button.vaw-node-drag').click()
    expect(viewer.get_by_role("region",name="Selected node payloads").locator("pre").last).to_have_text("CBA")
    pause_sequence=next(e["sequence"] for e in recorded["events"] if e["kind"]=="run.paused")
    slider=viewer.get_by_label("Recorded position",exact=True)
    slider.evaluate("""(input,position)=>{input.value=position;input.dispatchEvent(new Event('input',{bubbles:true}));}""",pause_sequence)
    expect(viewer.get_by_text("State at recorded position: paused",exact=True)).to_be_visible()
    expect(viewer.locator(".vaw-run-header")).to_contain_text("Latest run status: completed")
    assert get(f"/api/runs/{pinned_id}")==recorded,"Playback changed execution evidence"
    assert not errors,errors
    print("Pause/resume API, sequential UI breakpoints and stepping, browser reload, UI cancel/continue, pinned publication, terminal/origin rejection, and passive playback passed.")
    page.close()


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser-executable")
    parser.add_argument("--base-url",default=BASE)
    parser.add_argument("--screenshots",type=Path)
    args=parser.parse_args()
    BASE=args.base_url.rstrip("/")
    if args.screenshots:args.screenshots.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as p:
        options={"headless":True}
        if args.browser_executable:options["executable_path"]=args.browser_executable
        browser=p.chromium.launch(**options)
        try:check(browser,args.screenshots)
        finally:browser.close()
