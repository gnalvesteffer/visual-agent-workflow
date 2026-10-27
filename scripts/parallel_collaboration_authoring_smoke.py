"""Exercise 1.33 rendezvous creation, grants, invalid drafts, reload, and publish gating."""

import json
import os
import uuid

from playwright.sync_api import expect, sync_playwright

BASE = os.environ.get("VAW_STUDIO_BASE_URL", "http://127.0.0.1:5099")


def base_definition(workflow_id):
    branches = [
        {"id": "branch_1", "label": "Research", "anchorNodeId": "anchor_1"},
        {"id": "branch_2", "label": "Review", "anchorNodeId": "anchor_2"},
    ]
    return {
        "schemaVersion": "1.32", "workflowId": workflow_id,
        "displayName": "Collaboration authoring smoke", "entryNodeId": "input",
        "nodes": {
            "input": {"type": "core.input", "typeVersion": "1.0.0"},
            "fork": {"type": "core.parallel.fork", "typeVersion": "1.0.0", "parallelFork": {"branches": branches}},
            "anchor_1": {"type": "core.parallel.branch", "typeVersion": "1.0.0", "parallelBranch": {"forkNodeId": "fork", "branchId": "branch_1"}},
            "anchor_2": {"type": "core.parallel.branch", "typeVersion": "1.0.0", "parallelBranch": {"forkNodeId": "fork", "branchId": "branch_2"}},
            "join": {"type": "core.parallel.join", "typeVersion": "1.0.0", "parallelJoin": {"forkNodeId": "fork", "branchIds": ["branch_1", "branch_2"]}},
            "output": {"type": "core.output", "typeVersion": "1.0.0", "outputDataType": "json"},
        },
        "connections": {"result": {"source": {"nodeId": "join", "portId": "out"}, "target": {"nodeId": "output", "portId": "in"}, "kind": "data"}},
        "executionConnections": {
            "start": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "fork", "portId": "execute"}, "kind": "execution"},
            "fork-1": {"source": {"nodeId": "fork", "portId": "branch.branch_1"}, "target": {"nodeId": "anchor_1", "portId": "execute"}, "kind": "execution"},
            "fork-2": {"source": {"nodeId": "fork", "portId": "branch.branch_2"}, "target": {"nodeId": "anchor_2", "portId": "execute"}, "kind": "execution"},
            "branch-1": {"source": {"nodeId": "anchor_1", "portId": "then"}, "target": {"nodeId": "join", "portId": "branch.branch_1"}, "kind": "execution"},
            "branch-2": {"source": {"nodeId": "anchor_2", "portId": "then"}, "target": {"nodeId": "join", "portId": "branch.branch_2"}, "kind": "execution"},
            "finish": {"source": {"nodeId": "join", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
        },
    }


def check(browser):
    page = browser.new_page(viewport={"width": 1600, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.on("response", lambda response: errors.append(f"HTTP {response.status}: {response.url}")
            if response.status >= 400 and not response.url.endswith("/favicon.ico") else None)
    workflow_id = "parallel-collaboration-" + uuid.uuid4().hex
    authored = base_definition(workflow_id)
    response = page.request.put(BASE + f"/api/workflows/{workflow_id}/draft", data={"definition": authored, "expectedGeneration": 0})
    assert response.ok, response.text()

    page.goto(BASE, wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    expect(designer.get_by_label("Workflow name", exact=True)).to_have_value("Collaboration authoring smoke")
    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas.locator("li[data-node-id]")).to_have_count(len(authored["nodes"]))
    original_nodes = canvas.locator("li[data-node-id]").count()
    original_exec_edges = canvas.locator("path[data-kind='execution']").count()

    designer.locator(".vaw-node-library button[data-node-key='parallel-rendezvous']").click()
    rendezvous = canvas.locator("li[data-node-type='core.parallel.rendezvous']")
    try:
        expect(rendezvous).to_have_count(1)
    except AssertionError as error:
        raise AssertionError(f"Unconnected 1.33 rendezvous creation failed: {designer.locator('[role=alert]').all_text_contents()}") from error
    expect(canvas.locator("li[data-node-id]")).to_have_count(original_nodes + 1)
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(original_exec_edges)
    rendezvous_id = rendezvous.get_attribute("data-node-id")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    saved = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert saved["schemaVersion"] == "1.33"
    assert saved["nodes"][rendezvous_id]["type"] == "core.parallel.rendezvous"
    assert "parallelRendezvous" not in saved["nodes"][rendezvous_id]
    assert saved["executionConnections"] == authored["executionConnections"]
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_contain_text("rendezvous")

    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(0)
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(1)
    rendezvous = canvas.locator(f"li[data-node-id='{rendezvous_id}']")
    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)

    designer.get_by_label(f"Shared channels for {rendezvous_id}", exact=True).fill("research\nreview")
    designer.get_by_label(f"Collaboration timeout for {rendezvous_id}", exact=True).fill("90")
    designer.get_by_role("button", name="Apply region and branch pins", exact=True).click()
    expect(rendezvous.locator("[data-socket-kind='execution'][data-socket='branch.branch_1']")).to_have_count(2)
    expect(rendezvous.locator("[data-socket-kind='execution'][data-socket='branch.branch_2']")).to_have_count(2)

    # Agent creation uses the same explicit library action and must also stay unwired.
    before_agent_nodes = canvas.locator("li[data-node-id]").count()
    before_agent_edges = canvas.locator("path[data-kind='execution']").count()
    designer.locator(".vaw-node-library button[data-node-key='agent:demo.scripted']").click()
    first_agent = canvas.locator("li[data-node-type='core.agent']").last.get_attribute("data-node-id")
    expect(canvas.locator("li[data-node-id]")).to_have_count(before_agent_nodes + 1)
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(before_agent_edges)
    designer.locator(".vaw-node-library button[data-node-key='agent:demo.scripted']").click()
    second_agent = canvas.locator("li[data-node-type='core.agent']").last.get_attribute("data-node-id")
    expect(canvas.locator("li[data-node-id]")).to_have_count(before_agent_nodes + 2)
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(before_agent_edges)
    agent_ids = canvas.locator("li[data-node-type='core.agent']").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)")
    second_agent = next(agent_id for agent_id in agent_ids if agent_id != first_agent)

    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)
    designer.get_by_role("button", name="Add Agent grant for branch branch_1", exact=True).click()
    designer.get_by_label("Granted agent for branch_1 grant 0", exact=True).select_option(first_agent)
    designer.get_by_label("Grant phase for branch_1 grant 0", exact=True).select_option("publish")
    designer.get_by_label("Grant channels for branch_1 grant 0", exact=True).fill("research")
    designer.get_by_role("button", name="Add Agent grant for branch branch_2", exact=True).click()
    designer.get_by_label("Granted agent for branch_2 grant 0", exact=True).select_option(second_agent)
    designer.get_by_label("Grant phase for branch_2 grant 0", exact=True).select_option("read")
    designer.get_by_label("Grant channels for branch_2 grant 0", exact=True).fill("review")

    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    stored = page.request.get(BASE + f"/api/workflows/{workflow_id}/design").json()["draft"]["definition"]
    assert stored["parallelCollaborationRegions"][rendezvous_id] == {
        "forkNodeId": "fork", "rendezvousNodeId": rendezvous_id, "joinNodeId": "join",
        "channelIds": ["research", "review"], "timeoutSeconds": 90,
        "branches": [
            {"branchId": "branch_1", "agents": [{"agentNodeId": first_agent, "phase": "publish", "channels": ["research"]}]},
            {"branchId": "branch_2", "agents": [{"agentNodeId": second_agent, "phase": "read", "channels": ["review"]}]},
        ],
    }, stored["parallelCollaborationRegions"]

    # Reopen verifies authored metadata survives the save boundary.
    page.reload(wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(workflow_id)
    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)
    expect(designer.get_by_label(f"Shared channels for {rendezvous_id}", exact=True)).to_have_value("research\nreview")
    expect(designer.get_by_label(f"Collaboration timeout for {rendezvous_id}", exact=True)).to_have_value("90")
    expect(designer.get_by_label("Granted agent for branch_1 grant 0", exact=True)).to_have_value(first_agent)
    expect(designer.get_by_label("Grant phase for branch_2 grant 0", exact=True)).to_have_value("read")
    expect(designer.get_by_label("Grant role for branch_1 grant 0", exact=True)).to_have_count(0)
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_be_visible()

    # The UI has kept execution wiring untouched; the user explicitly authors the full routes.
    final_definition = json.loads(json.dumps(stored))
    final_definition["executionConnections"] = {
        "start": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "fork", "portId": "execute"}, "kind": "execution"},
        "fork-1": {"source": {"nodeId": "fork", "portId": "branch.branch_1"}, "target": {"nodeId": "anchor_1", "portId": "execute"}, "kind": "execution"},
        "fork-2": {"source": {"nodeId": "fork", "portId": "branch.branch_2"}, "target": {"nodeId": "anchor_2", "portId": "execute"}, "kind": "execution"},
        "pre-1": {"source": {"nodeId": "anchor_1", "portId": "then"}, "target": {"nodeId": first_agent, "portId": "execute"}, "kind": "execution"},
        "pre-rendezvous": {"source": {"nodeId": first_agent, "portId": "out"}, "target": {"nodeId": rendezvous_id, "portId": "branch.branch_1"}, "kind": "execution"},
        "rendezvous-1": {"source": {"nodeId": rendezvous_id, "portId": "branch.branch_1"}, "target": {"nodeId": "join", "portId": "branch.branch_1"}, "kind": "execution"},
        "pre-2": {"source": {"nodeId": "anchor_2", "portId": "then"}, "target": {"nodeId": rendezvous_id, "portId": "branch.branch_2"}, "kind": "execution"},
        "rendezvous-2": {"source": {"nodeId": rendezvous_id, "portId": "branch.branch_2"}, "target": {"nodeId": second_agent, "portId": "execute"}, "kind": "execution"},
        "post-2": {"source": {"nodeId": second_agent, "portId": "out"}, "target": {"nodeId": "join", "portId": "branch.branch_2"}, "kind": "execution"},
        "finish": {"source": {"nodeId": "join", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
    }
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(final_definition))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    try:
        designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True).wait_for(timeout=10000)
    except Exception as error:
        raise AssertionError(f"Final route import failed: {designer.locator('[role=alert]').all_text_contents()}") from error
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_have_count(0)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    expect(designer).to_contain_text("Published and activated version 1.")

    # Keep the schema-1.33 workflow above as an unchanged compatibility proof. Clone its
    # valid definition, then upgrade that copy through the actual Studio controls.
    phased_id = workflow_id + "-phased"
    phased_definition = json.loads(json.dumps(final_definition))
    phased_definition["workflowId"] = phased_id
    phased_definition["displayName"] = "Two-phase collaboration authoring smoke"
    response = page.request.put(BASE + f"/api/workflows/{phased_id}/draft", data={"definition": phased_definition, "expectedGeneration": 0})
    assert response.ok, response.text()
    page.reload(wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(phased_id)
    expect(designer.get_by_label("Saved workflow", exact=True)).to_have_value(phased_id)
    expect(designer.get_by_label("Workflow name", exact=True)).to_have_value("Two-phase collaboration authoring smoke")
    canvas = designer.locator(".vaw-graph-editor")
    expect(canvas.locator(f"li[data-node-id='{rendezvous_id}']")).to_have_count(1)
    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)
    expect(designer.get_by_label("Node to organize", exact=True)).to_have_value(rendezvous_id)
    before_upgrade_edges = canvas.locator("path[data-kind='execution']").count()
    upgrade_button = designer.get_by_role("button", name="Upgrade to two phases", exact=True)
    if upgrade_button.count() != 1:
        current = page.request.get(BASE + f"/api/workflows/{phased_id}/design").json()["draft"]["definition"]
        raise AssertionError(f"Upgrade action missing: schema={current['schemaVersion']}, regions={current.get('parallelCollaborationRegions')}, selected={designer.get_by_label('Node to organize', exact=True).input_value()}, alerts={designer.locator('[role=alert]').all_text_contents()}")
    upgrade_button.click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(2)
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(before_upgrade_edges)
    phased_rendezvous_ids = canvas.locator("li[data-node-type='core.parallel.rendezvous']").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)")
    second_rendezvous_id = next(node_id for node_id in phased_rendezvous_ids if node_id != rendezvous_id)
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(1)
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(2)

    # The two new Agents remain unwired until the author connects each execution pin.
    before_agent_nodes = canvas.locator("li[data-node-id]").count()
    before_agent_edges = canvas.locator("path[data-kind='execution']").count()
    agent_ids_before = set(canvas.locator("li[data-node-type='core.agent']").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)"))
    designer.locator(".vaw-node-library button[data-node-key='agent:demo.scripted']").click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(before_agent_nodes + 1)
    agent_ids_after_first = set(canvas.locator("li[data-node-type='core.agent']").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)"))
    middle_agent_1 = (agent_ids_after_first - agent_ids_before).pop()
    designer.locator(".vaw-node-library button[data-node-key='agent:demo.scripted']").click()
    expect(canvas.locator("li[data-node-id]")).to_have_count(before_agent_nodes + 2)
    agent_ids_after_second = set(canvas.locator("li[data-node-type='core.agent']").evaluate_all("nodes => nodes.map(node => node.dataset.nodeId)"))
    middle_agent_2 = (agent_ids_after_second - agent_ids_after_first).pop()
    expect(canvas.locator("li[data-node-id]")).to_have_count(before_agent_nodes + 2)
    expect(canvas.locator("path[data-kind='execution']")).to_have_count(before_agent_edges)

    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)
    designer.get_by_role("button", name="Add Agent grant for branch branch_1", exact=True).click()
    designer.get_by_label("Granted agent for branch_1 grant 1", exact=True).select_option(middle_agent_2)
    designer.get_by_label("Grant phase for branch_1 grant 1", exact=True).select_option("phase2")
    designer.get_by_label("Grant role for branch_1 grant 1", exact=True).select_option("publish")
    designer.get_by_label("Grant channels for branch_1 grant 1", exact=True).fill("review")
    designer.get_by_role("button", name="Add Agent grant for branch branch_2", exact=True).click()
    designer.get_by_label("Granted agent for branch_2 grant 1", exact=True).select_option(middle_agent_1)
    designer.get_by_label("Grant phase for branch_2 grant 1", exact=True).select_option("phase1")
    designer.get_by_label("Grant role for branch_2 grant 1", exact=True).select_option("read")
    designer.get_by_label("Grant channels for branch_2 grant 1", exact=True).fill("research")
    designer.get_by_label("Grant phase for branch_2 grant 0", exact=True).select_option("phase2")
    designer.get_by_label("Grant role for branch_2 grant 0", exact=True).select_option("read")
    designer.get_by_label("Grant channels for branch_2 grant 0", exact=True).fill("review")
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    saved_phased = page.request.get(BASE + f"/api/workflows/{phased_id}/design").json()["draft"]["definition"]
    assert saved_phased["schemaVersion"] == "1.35"
    phase_region = saved_phased["parallelCollaborationRegions"][rendezvous_id]
    assert phase_region["secondRendezvousNodeId"] == second_rendezvous_id
    assert phase_region["branches"][0]["agents"] == [
        {"agentNodeId": first_agent, "phase": "phase1", "channels": ["research"], "role": "publish"},
        {"agentNodeId": middle_agent_2, "phase": "phase2", "channels": ["review"], "role": "publish"},
    ]
    assert phase_region["branches"][1]["agents"] == [
        {"agentNodeId": second_agent, "phase": "phase2", "channels": ["review"], "role": "read"},
        {"agentNodeId": middle_agent_1, "phase": "phase1", "channels": ["research"], "role": "read"},
    ]
    page.reload(wait_until="networkidle")
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    reloaded_phased = page.request.get(BASE + f"/api/workflows/{phased_id}/design").json()["draft"]["definition"]
    assert reloaded_phased["schemaVersion"] == "1.35"
    assert reloaded_phased["parallelCollaborationRegions"][rendezvous_id] == phase_region
    assert [grant for branch in reloaded_phased["parallelCollaborationRegions"][rendezvous_id]["branches"] for grant in branch["agents"]] == [
        {"agentNodeId": first_agent, "phase": "phase1", "channels": ["research"], "role": "publish"},
        {"agentNodeId": middle_agent_2, "phase": "phase2", "channels": ["review"], "role": "publish"},
        {"agentNodeId": second_agent, "phase": "phase2", "channels": ["review"], "role": "read"},
        {"agentNodeId": middle_agent_1, "phase": "phase1", "channels": ["research"], "role": "read"},
    ]
    designer.get_by_label("Saved workflow", exact=True).select_option(phased_id)
    expect(designer.get_by_label("Workflow name", exact=True)).to_have_value("Two-phase collaboration authoring smoke")
    designer.get_by_label("Node to organize", exact=True).select_option(rendezvous_id)
    expect(designer.get_by_label("Node to organize", exact=True)).to_have_value(rendezvous_id)
    expect(designer.get_by_label(f"Second rendezvous for {rendezvous_id}", exact=True)).to_have_value(second_rendezvous_id)
    expect(designer.get_by_label("Grant phase for branch_1 grant 0", exact=True)).to_have_value("phase1")
    expect(designer.get_by_label("Grant role for branch_1 grant 0", exact=True)).to_have_value("publish")
    expect(designer.get_by_label("Grant channels for branch_1 grant 0", exact=True)).to_have_value("research")
    expect(designer.get_by_label("Grant phase for branch_1 grant 1", exact=True)).to_have_value("phase2")
    expect(designer.get_by_label("Grant role for branch_1 grant 1", exact=True)).to_have_value("publish")
    expect(designer.get_by_label("Grant channels for branch_1 grant 1", exact=True)).to_have_value("review")
    expect(designer.get_by_label("Grant phase for branch_2 grant 0", exact=True)).to_have_value("phase2")
    expect(designer.get_by_label("Grant role for branch_2 grant 0", exact=True)).to_have_value("read")
    expect(designer.get_by_label("Grant channels for branch_2 grant 0", exact=True)).to_have_value("review")
    expect(designer.get_by_label("Grant phase for branch_2 grant 1", exact=True)).to_have_value("phase1")
    expect(designer.get_by_label("Grant role for branch_2 grant 1", exact=True)).to_have_value("read")
    expect(designer.get_by_label("Grant channels for branch_2 grant 1", exact=True)).to_have_value("research")
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_be_visible()
    expect(designer.get_by_role("button", name="Publish & activate", exact=True)).to_be_disabled()
    expect(designer.get_by_role("button", name="Run selected version", exact=True)).to_be_disabled()

    # Importing the explicit execution routes is the author action that completes the graph.
    final_phased = json.loads(json.dumps(saved_phased))
    final_phased["executionConnections"] = {
        "start": {"source": {"nodeId": "input", "portId": "then"}, "target": {"nodeId": "fork", "portId": "execute"}, "kind": "execution"},
        "fork-1": {"source": {"nodeId": "fork", "portId": "branch.branch_1"}, "target": {"nodeId": "anchor_1", "portId": "execute"}, "kind": "execution"},
        "fork-2": {"source": {"nodeId": "fork", "portId": "branch.branch_2"}, "target": {"nodeId": "anchor_2", "portId": "execute"}, "kind": "execution"},
        "branch1-agent": {"source": {"nodeId": "anchor_1", "portId": "then"}, "target": {"nodeId": first_agent, "portId": "execute"}, "kind": "execution"},
        "agent-rendezvous1": {"source": {"nodeId": first_agent, "portId": "out"}, "target": {"nodeId": rendezvous_id, "portId": "branch.branch_1"}, "kind": "execution"},
        "rendezvous1-middle2": {"source": {"nodeId": rendezvous_id, "portId": "branch.branch_1"}, "target": {"nodeId": middle_agent_2, "portId": "execute"}, "kind": "execution"},
        "middle2-rendezvous2": {"source": {"nodeId": middle_agent_2, "portId": "out"}, "target": {"nodeId": second_rendezvous_id, "portId": "branch.branch_1"}, "kind": "execution"},
        "rendezvous2-join1": {"source": {"nodeId": second_rendezvous_id, "portId": "branch.branch_1"}, "target": {"nodeId": "join", "portId": "branch.branch_1"}, "kind": "execution"},
        "anchor2-rendezvous1": {"source": {"nodeId": "anchor_2", "portId": "then"}, "target": {"nodeId": rendezvous_id, "portId": "branch.branch_2"}, "kind": "execution"},
        "rendezvous1-branch2-middle1": {"source": {"nodeId": rendezvous_id, "portId": "branch.branch_2"}, "target": {"nodeId": middle_agent_1, "portId": "execute"}, "kind": "execution"},
        "middle1-rendezvous2": {"source": {"nodeId": middle_agent_1, "portId": "out"}, "target": {"nodeId": second_rendezvous_id, "portId": "branch.branch_2"}, "kind": "execution"},
        "rendezvous2-agent2": {"source": {"nodeId": second_rendezvous_id, "portId": "branch.branch_2"}, "target": {"nodeId": second_agent, "portId": "execute"}, "kind": "execution"},
        "agent2-join2": {"source": {"nodeId": second_agent, "portId": "out"}, "target": {"nodeId": "join", "portId": "branch.branch_2"}, "kind": "execution"},
        "finish": {"source": {"nodeId": "join", "portId": "then"}, "target": {"nodeId": "output", "portId": "execute"}, "kind": "execution"},
    }
    with open("/tmp/vaw-phased-import.json", "w", encoding="utf-8") as definition_file:
        json.dump(final_phased, definition_file)
    details = designer.get_by_text("Import / export definition", exact=True).locator("..")
    if not details.evaluate("element => element.open"):
        details.locator("summary").click()
    designer.get_by_label("Workflow JSON", exact=True).fill(json.dumps(final_phased))
    designer.get_by_role("button", name="Import JSON into draft", exact=True).click()
    expect(designer.get_by_text("Definition imported locally. Save and publish explicitly.", exact=True)).to_be_visible()
    expect(designer.get_by_role("alert", name="Workflow validation issues")).to_have_count(0)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    designer.get_by_role("button", name="Publish & activate", exact=True).click()
    expect(designer).to_contain_text("Published and activated version 1.")
    designer.get_by_role("button", name="Run selected version", exact=True).click()
    playback = page.get_by_role("region", name="Parallel collaboration evidence")
    expect(playback).to_be_visible(timeout=20000)
    expect(playback).to_contain_text("Phase 1")
    expect(playback).to_contain_text("Phase 2")

    # Removing either barrier is one undoable region reset; it removes both barriers and grants.
    page.get_by_role("button", name="Designer", exact=True).click()
    designer = page.get_by_role("region", name="Workflow designer", exact=True)
    designer.get_by_label("Saved workflow", exact=True).select_option(phased_id)
    canvas = designer.locator(".vaw-graph-editor")
    canvas.locator(f"li[data-node-id='{second_rendezvous_id}']").click(button="right")
    designer.get_by_role("button", name="Remove collaboration region…", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(0)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    reset_draft = page.request.get(BASE + f"/api/workflows/{phased_id}/design").json()["draft"]["definition"]
    assert reset_draft["schemaVersion"] == "1.32"
    assert "parallelCollaborationRegions" not in reset_draft
    assert all(node["type"] != "core.parallel.rendezvous" for node in reset_draft["nodes"].values())
    assert {node_type for node_type in ("core.parallel.fork", "core.parallel.branch", "core.parallel.join")
            if any(node["type"] == node_type for node in reset_draft["nodes"].values())} == {
                "core.parallel.fork", "core.parallel.branch", "core.parallel.join"
            }
    remaining_ids = set(reset_draft["nodes"])
    assert all(edge["source"]["nodeId"] in remaining_ids and edge["target"]["nodeId"] in remaining_ids
               for edge in reset_draft["executionConnections"].values())
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(2)
    designer.get_by_role("button", name="Redo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(0)
    designer.get_by_role("button", name="Undo edit", exact=True).click()
    expect(canvas.locator("li[data-node-type='core.parallel.rendezvous']")).to_have_count(2)
    designer.get_by_role("button", name="Save draft", exact=True).click()
    expect(designer).to_contain_text("Draft saved.")
    restored_draft = page.request.get(BASE + f"/api/workflows/{phased_id}/design").json()["draft"]["definition"]
    assert restored_draft["schemaVersion"] == "1.35"
    assert restored_draft["parallelCollaborationRegions"][rendezvous_id]["secondRendezvousNodeId"] == second_rendezvous_id
    assert not errors, errors
    print("Schema 1.33 remains saveable and publishable; the 1.35 upgrade adds only an unwired second barrier, phase/role grants survive save/reload, invalid drafts stay gated, explicit topology publishes and plays back both phases, and region removal is undoable.")
    page.close()


if __name__ == "__main__":
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True, executable_path="/usr/bin/chromium")
        try:
            check(browser)
        finally:
            browser.close()
