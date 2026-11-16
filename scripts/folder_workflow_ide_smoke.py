"""Verify the approved workflow document layout and execution in a folder project."""
from workbench_browser import current_project

import argparse
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright
from folder_execution_smoke import ROOT, write_project


def verify(base_url, browser_executable):
    package = json.loads((ROOT / 'samples/WorkflowService/artifacts/pure.package.json').read_text())
    definition = package['definition']
    definition['schemaVersion'] = '2.0'
    definition['executionConnections'] = {
        'activate-output': {
            'source': {'nodeId': 'input', 'portId': 'then'},
            'target': {'nodeId': 'output', 'portId': 'execute'},
            'kind': 'execution',
        },
    }
    with tempfile.TemporaryDirectory(prefix='vaw-folder-document-') as temporary:
        project = Path(temporary) / 'project'
        project.mkdir()
        write_project(project, package, reverse=True)
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
                open_workflow = page.get_by_role('button', name='Open workflow service-pure.workflow', exact=True)
                if open_workflow.is_enabled():
                    open_workflow.click()
                expect(current_project(page)).to_have_text('Folder execution smoke project')
                document = page.locator('.vaw-folder-workflow-document')
                expect(document).to_be_visible()
                expect(page.get_by_role('navigation', name='Open project files')).to_be_visible()
                source_save = document.get_by_role('button', name='Save source file', exact=True)
                if source_save.is_enabled():
                    source_save.click()
                    expect(source_save).to_be_disabled()
                canvas = document.locator('.vaw-canvas-viewport')
                expect(canvas).to_be_visible()
                assert canvas.bounding_box()['height'] >= 300, canvas.bounding_box()
                expect(document.locator('.vaw-parameter-editor')).to_have_count(0)
                document.get_by_role('button', name='Run workflow', exact=True).click()
                run_dialog = page.locator('.vaw-run-dialog')
                expect(run_dialog).to_be_visible()
                expect(run_dialog).to_contain_text('saved workflow')
                page.keyboard.press('Escape')
                expect(run_dialog).to_be_hidden()
                document.get_by_role('button', name='Run workflow', exact=True).click()
                expect(run_dialog).to_be_visible()
                run_dialog.get_by_label('Workflow input', exact=True).fill('Hello folder!')
                run_dialog.get_by_role('button', name='Run workflow', exact=True).click()
                expect(document.locator('.vaw-viewer')).to_be_visible()
                document.get_by_role('button', name='Back to workflow', exact=True).click()
                expect(document.locator('.vaw-ide-run-table')).to_contain_text('completed', timeout=20000)
                output_tab = document.get_by_role('tab', name='Output', exact=True)
                output_tab.click()
                expect(output_tab).to_have_attribute('aria-selected', 'true')
                output = document.get_by_role('tabpanel', name='Workflow output', exact=True)
                expect(output).to_be_visible()
                expect(output.locator('.vaw-workflow-result')).to_have_text('!REDLOF OLLEH')
                output.get_by_role('button', name='Inspect execution', exact=True).click()
                expect(document.locator('.vaw-viewer')).to_be_visible()
                document.get_by_role('button', name='Back to workflow', exact=True).click()
                expect(canvas).to_be_visible()
                canvas.locator('[data-drag-node="step-1"]').click()
                inspector = document.get_by_label('Selected node properties', exact=True)
                inspector.get_by_label('Node label', exact=True).fill('Normalize request')
                inspector.get_by_label('Node label', exact=True).press('Tab')
                save = document.get_by_role('button', name='Save source file', exact=True)
                expect(save).to_be_enabled()
                expect(document.get_by_role('button', name='Run workflow', exact=True)).to_be_disabled()
                save.click()
                expect(save).to_be_disabled()
                expect(page.locator('.vaw-file-tab')).to_have_count(1)
                expect(document.get_by_role('button', name='Run workflow', exact=True)).to_be_enabled()
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                settings_panel = document.get_by_role('region', name='Workflow settings panel', exact=True)
                variables = settings_panel.locator('details.vaw-parameter-editor').filter(has_text='Workflow variables')
                expect(document.locator('.vaw-parameter-editor')).to_have_count(2)
                expect(variables).to_have_count(1)
                variables.locator(':scope > summary').click()
                expect(variables.get_by_label('New variable type', exact=True)).to_have_value('string')
                variables.get_by_label('Variable ID', exact=True).fill('browser-items')
                variables.get_by_label('Display name', exact=True).fill('Browser items')
                variables.get_by_label('New variable type', exact=True).select_option('array<string>')
                array_editor = variables.locator('.vaw-controls textarea')
                expect(array_editor).to_be_visible()
                array_default = variables.get_by_label('Default value', exact=True)
                expect(array_default).to_be_visible()
                array_default.fill('["alpha", "beta"]')
                variables.get_by_role('button', name='Add variable', exact=True).click()
                stored_default = variables.get_by_label('Default JSON array for Browser items', exact=True)
                expect(stored_default).to_have_value('["alpha", "beta"]')
                new_variable_controls = variables.locator('.vaw-controls')
                new_variable_controls.get_by_label('Variable ID', exact=True).fill('browser-json-items')
                new_variable_controls.get_by_label('Display name', exact=True).fill('Browser JSON items')
                new_variable_controls.get_by_label('New variable type', exact=True).select_option('array<json>')
                json_default = new_variable_controls.locator('textarea')
                expect(json_default).to_be_visible()
                json_default.fill('[null, {"seed": 1.00}]')
                variables.get_by_role('button', name='Add variable', exact=True).click()
                stored_json_default = variables.get_by_label('Default JSON array for Browser JSON items', exact=True)
                expect(stored_json_default).to_have_value('[null, {"seed": 1.00}]')
                json_variable = variables.locator('fieldset').filter(has_text='Browser JSON items')
                expect(json_variable).to_contain_text('immutable array transformations can feed Set')
                expect(json_variable).to_contain_text('65,536-byte run evidence budget')
                json_variable.scroll_into_view_if_needed()
                page.screenshot(path='/tmp/vaw-json-array-variable-settings.png')
                save.click()
                expect(save).to_be_disabled()
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                expect(document.locator('.vaw-parameter-editor')).to_have_count(0)
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                settings_panel = document.get_by_role('region', name='Workflow settings panel', exact=True)
                variables = settings_panel.locator('details.vaw-parameter-editor').filter(has_text='Workflow variables')
                variables.locator(':scope > summary').click()
                stored_default = variables.get_by_label('Default JSON array for Browser items', exact=True)
                expect(stored_default).to_have_value('["alpha", "beta"]')
                stored_json_default = variables.get_by_label('Default JSON array for Browser JSON items', exact=True)
                expect(stored_json_default).to_have_value('[null, {"seed": 1.00}]')
                page.screenshot(path='/tmp/vaw-workflow-variable-settings.png')
                stored_default.fill('["alpha", 1]')
                stored_default.press('Tab')
                expect(save).to_be_enabled()
                expect(document.get_by_role('button', name='Run workflow', exact=True)).to_be_disabled()
                save.click()
                expect(save).to_be_disabled()
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                settings_panel = document.get_by_role('region', name='Workflow settings panel', exact=True)
                variables = settings_panel.locator('details.vaw-parameter-editor').filter(has_text='Workflow variables')
                variables.locator(':scope > summary').click()
                expect(variables.get_by_label('Default JSON array for Browser items', exact=True)).to_have_value('["alpha", 1]')
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                document.locator('button[role="tab"]').filter(has_text='Problems').click()
                issues = document.get_by_role('tabpanel', name='Workflow issues', exact=True)
                expect(issues).to_contain_text('variables.browser-items')
                expect(issues).to_contain_text('default matching that type')
                expect(page.locator('.vaw-run-dialog')).to_have_count(0)
                document.get_by_role('tab', name='Runs', exact=True).click()
                page.screenshot(path='/tmp/vaw-folder-workflow-ide.png')
                page.set_viewport_size({'width': 390, 'height': 844})
                assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
                assert not errors, errors
            except Exception:
                page.screenshot(path='/tmp/vaw-folder-workflow-failure.png')
                raise
            finally:
                browser.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default=os.environ.get('VAW_STUDIO_BASE_URL', 'http://127.0.0.1:5099'))
    parser.add_argument('--browser-executable')
    arguments = parser.parse_args()
    verify(arguments.base_url.rstrip('/'), arguments.browser_executable)
    print('Folder workflow document layout, save, run, output, inspection, and responsive checks passed.')
