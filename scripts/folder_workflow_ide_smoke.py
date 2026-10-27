"""Verify the approved workflow document layout and execution in a folder project."""
import argparse
import json
import os
import tempfile
from pathlib import Path

from playwright.sync_api import expect, sync_playwright
from folder_execution_smoke import ROOT, write_project


def verify(base_url, browser_executable):
    package = json.loads((ROOT / 'samples/WorkflowService/artifacts/pure.package.json').read_text())
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
                page.get_by_label('Absolute folder path', exact=True).fill(str(project))
                page.get_by_role('button', name='Open folder', exact=True).click()
                page.get_by_role('button', name='Open workflow service-pure.workflow', exact=True).click()
                expect(page.get_by_label('Current project', exact=True).locator('option:checked')).to_have_text('Folder execution smoke project')
                document = page.locator('.vaw-folder-workflow-document')
                expect(document).to_be_visible()
                expect(page.get_by_role('navigation', name='Open project files')).to_be_visible()
                canvas = document.locator('.vaw-canvas-viewport')
                expect(canvas).to_be_visible()
                assert canvas.bounding_box()['height'] >= 300, canvas.bounding_box()
                expect(document.locator('.vaw-parameter-editor')).to_have_count(0)
                document.get_by_role('button', name='Test input', exact=True).click()
                document.get_by_label('Workflow input', exact=True).fill('Hello folder!')
                document.get_by_role('button', name='Run workflow', exact=True).click()
                expect(document.locator('.vaw-ide-run-table')).to_contain_text('completed', timeout=20000)
                document.get_by_role('tab', name='Output', exact=True).click()
                expect(document.locator('.vaw-workflow-result')).to_have_text('!REDLOF OLLEH')
                document.get_by_role('button', name='Inspect execution', exact=True).click()
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
                expect(document.locator('.vaw-parameter-editor')).to_be_visible()
                document.get_by_role('button', name='Workflow settings', exact=True).click()
                expect(document.locator('.vaw-parameter-editor')).to_have_count(0)
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
