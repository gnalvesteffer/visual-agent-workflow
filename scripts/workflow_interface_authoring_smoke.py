"""Verify folder-project interface authoring and exact operation selection in the graph editor."""
import argparse
import os
import re
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright
from folder_execution_smoke import ROOT, write_project
from workbench_browser import current_project


def verify(base_url, browser_executable):
    import json
    package = json.loads((ROOT / 'samples/WorkflowService/artifacts/pure.package.json').read_text())
    with tempfile.TemporaryDirectory(prefix='vaw-interface-authoring-') as temporary:
        project = Path(temporary) / 'project'
        project.mkdir()
        write_project(project, package, reverse=True)
        write_json_workflow(project)
        with sync_playwright() as playwright:
            options = {'headless': True}
            if browser_executable:
                options['executable_path'] = browser_executable
            browser = playwright.chromium.launch(**options)
            page = browser.new_page(viewport={'width': 1600, 'height': 1000})
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            try:
                page.goto(base_url + '/source-project', wait_until='networkidle')
                folder_form = page.locator('.vaw-open-project-details')
                if folder_form.count() and not folder_form.evaluate('e => e.open'):
                    folder_form.locator(':scope > summary').click()
                page.get_by_label('Absolute folder path', exact=True).fill(str(project))
                page.get_by_role('button', name='Open folder', exact=True).click()
                workflow_file = page.get_by_role('button', name='Open workflow service-pure.workflow', exact=True)
                if workflow_file.get_attribute('aria-current') != 'page':
                    workflow_file.click()
                expect(workflow_file).to_have_attribute('aria-current', 'page')
                expect(current_project(page)).to_have_text('Folder execution smoke project')
                editor = page.locator('.vaw-folder-workflow-document')
                expect(page.get_by_text('This legacy workflow was normalized into the unified graph draft for editing.', exact=False)).to_be_visible()
                save_workflow = editor.get_by_role('button', name='Save source file', exact=True)
                expect(save_workflow).to_be_enabled()
                save_workflow.click()
                expect(save_workflow).to_be_disabled()

                assets = page.locator('details[aria-label="Workflow interface assets"]')
                expect(assets).to_be_visible()
                assets.locator(':scope > summary').click()
                assets.get_by_label('Stable interface ID', exact=True).fill('smoke.text')
                assets.get_by_label('Display name', exact=True).fill('Smoke text')
                assets.get_by_label('Operation boundary shape', exact=True).select_option('1.1')
                assets.get_by_label('Field ID', exact=True).first.fill('query')
                assets.get_by_label('Name', exact=True).nth(1).fill('Query')
                assets.get_by_label('Field ID', exact=True).nth(1).fill('query')
                assets.get_by_label('Name', exact=True).nth(2).fill('Query')
                assets.get_by_role('button', name='Save immutable interface revision', exact=True).click()
                interface_selection = assets.get_by_label('Interface revision for implementation', exact=True)
                expect(interface_selection).not_to_have_value('', timeout=10000)

                assets.get_by_label('Implementation ID', exact=True).fill('smoke.text.local')
                workflow_binding = assets.get_by_label('Workflow for operation execute', exact=True)
                workflow_binding.select_option(value='service-json')
                assets.get_by_role('button', name='Validate and save implementation revision', exact=True).click()
                assets.locator(':scope > summary').click()
                editor.get_by_role('button', name='Add node', exact=True).click()
                library = page.locator('.vaw-library-panel .vaw-node-library')
                library.get_by_role('button', name='Add Interface call', exact=True).click()
                node = editor.locator('li[data-node-type="core.workflow-interface-call"]')
                expect(node).to_have_count(1)
                inspector = editor.get_by_label('Selected node properties', exact=True)
                selector = inspector.get_by_label(re.compile('Pinned workflow interface operation'))
                selector.select_option(label='Smoke text · Execute · smoke.text.local v1')
                expect(node.locator('[data-socket="query"]')).to_have_count(2)
                expect(inspector.get_by_text('Smoke text', exact=True)).to_be_visible()
                expect(inspector).to_contain_text('smoke.text.local v1')
                expect(inspector).to_contain_text('service-json v1')
                expect(page.locator('.vaw-file-tab')).to_have_count(2)
                assert not errors, errors
                page.screenshot(path='/tmp/vaw-workflow-interface-authoring.png')
            except Exception:
                page.screenshot(path='/tmp/vaw-workflow-interface-authoring-failure.png')
                raise
            finally:
                browser.close()


def write_json_workflow(project):
    import hashlib
    import json
    from pathlib import Path

    workflow = {
        'schemaVersion': '2.0',
        'workflowId': 'service-json',
        'entryNodeId': 'input',
        'nodes': {
            'input': {'type': 'core.input', 'typeVersion': '1.0.0', 'inputDataType': 'json'},
            'output': {'type': 'core.output', 'typeVersion': '1.0.0', 'outputDataType': 'json'},
        },
        'connections': {
            'data': {'source': {'nodeId': 'input', 'portId': 'out'}, 'target': {'nodeId': 'output', 'portId': 'in'}, 'kind': 'data'},
        },
        'executionConnections': {
            'execute': {'source': {'nodeId': 'input', 'portId': 'then'}, 'target': {'nodeId': 'output', 'portId': 'execute'}, 'kind': 'execution'},
        },
    }
    asset = {'formatVersion': '1.0', 'definition': workflow,
             'layout': {'schemaVersion': '1.0', 'nodes': {
                 'input': {'position': {'x': 80, 'y': 100}, 'label': '', 'comment': ''},
                 'output': {'position': {'x': 440, 'y': 100}, 'label': '', 'comment': ''},
             }, 'wires': {}, 'sections': {}}}
    content = (json.dumps(asset, indent=2) + '\n').encode()
    relative_path = 'workflows/service-json.workflow'
    path = Path(project) / relative_path
    path.write_bytes(content)
    manifest_path = Path(project) / 'vaw.project.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    manifest['files'].append({'kind': 'workflow', 'id': 'service-json', 'version': 'draft',
                              'path': relative_path, 'sha256': hashlib.sha256(content).hexdigest()})
    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default=os.environ.get('VAW_STUDIO_BASE_URL', 'http://127.0.0.1:5099'))
    parser.add_argument('--browser-executable')
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip('/'), arguments.browser_executable)
    print('Folder interface authoring, immutable binding, graph call selection, and typed pin checks passed.')
