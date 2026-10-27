"""Verify the searchable IDE project switcher through visible controls."""
from workbench_browser import current_project

import argparse
import contextlib
import json
import re
import tempfile
from pathlib import Path
from playwright.sync_api import expect, sync_playwright


def verify(base, executable):
    with tempfile.TemporaryDirectory(prefix='vaw-picker-') as temporary, sync_playwright() as playwright:
        paths = [Path(temporary) / 'research-team' / name for name in ('customer-support', 'billing-review')]
        for index, path in enumerate(paths):
            path.mkdir(parents=True)
            (path / 'vaw.project.json').write_text(json.dumps({'formatVersion': '1.0', 'projectId': f'picker-{index}', 'projectName': 'Shared project', 'files': []}))
        options = {'headless': True}
        if executable: options['executable_path'] = executable
        browser = playwright.chromium.launch(**options)
        page = browser.new_page(viewport={'width': 1440, 'height': 1000})
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        trigger = current_project(page)
        popup = page.get_by_role('dialog', name='Switch project', exact=True)
        search = popup.get_by_role('combobox', name='Search projects', exact=True)
        def open_picker():
            trigger.click()
            expect(search).to_be_focused()
            expect(trigger).to_have_attribute('aria-expanded', 'true')
        try:
            urls = []
            for path in paths:
                page.goto(base + '/source-project', wait_until='networkidle')
                form = page.locator('.vaw-open-project-details')
                if form.count() and not form.evaluate('e => e.open'): form.locator(':scope > summary').click()
                page.get_by_label('Absolute folder path', exact=True).fill(str(path))
                page.get_by_role('button', name='Open folder', exact=True).click()
                expect(trigger).to_have_text('Shared project')
                expect(trigger).to_have_attribute('title', str(path))
                expect(page).to_have_url(re.compile('/source-project/folder/'))
                urls.append(page.url)
            trigger.focus()
            page.keyboard.press('Space')
            expect(search).to_be_focused()
            expect(trigger).to_have_attribute('aria-expanded', 'true')
            search.fill('Shared project')
            expect(popup.get_by_role('option')).to_have_count(2)
            for path in paths: expect(popup.get_by_role('option').filter(has_text=str(path))).to_have_count(1)
            search.fill('no-project-matches-this')
            expect(popup.get_by_role('option')).to_have_count(0)
            expect(popup.get_by_role('status')).to_contain_text('No matching projects')
            assert page.url == urls[1], 'Filtering must not navigate'
            page.keyboard.press('Escape')
            expect(popup).not_to_be_visible()
            expect(trigger).to_be_focused()
            open_picker()
            expect(search).to_have_value('')
            search.fill('customer-support')
            expect(popup.get_by_role('option')).to_have_count(1)
            page.keyboard.press('ArrowDown')
            page.keyboard.press('ArrowUp')
            assert page.url == urls[1], 'Highlighting must not navigate'
            page.keyboard.press('Enter')
            expect(page).to_have_url(urls[0])
            expect(popup).not_to_be_visible()
            # Existing folder switching retains unsaved buffers in this IDE session.
            page.get_by_role('button', name='Create project item', exact=True).click()
            page.get_by_role('menuitem', name='New file', exact=False).click()
            page.get_by_role('menuitem', name='Workflow .workflow', exact=True).click()
            page.get_by_label('Node label', exact=True).fill('Retained picker draft')
            page.get_by_label('Node label', exact=True).press('Tab')
            for path, url in ((paths[1], urls[1]), (paths[0], urls[0])):
                open_picker()
                search.fill(path.name)
                expect(popup.get_by_role('option')).to_have_count(1)
                option = popup.get_by_role('option').filter(has_text=str(path))
                option.click()
                expect(page).to_have_url(url)
            expect(page.get_by_label('Node label', exact=True)).to_have_value('Retained picker draft')
            page.get_by_role('button', name='Save source file', exact=True).click()
            expect(page.get_by_role('button', name='Save source file', exact=True)).to_be_disabled()
            open_picker()
            search.fill('customer-support')
            expect(popup.get_by_role('option')).to_have_count(1)
            popup.get_by_role('option').click()  # Current project is a no-op.
            expect(page.get_by_label('Node label', exact=True)).to_have_value('Retained picker draft')
            open_picker()
            search.press('Tab')
            expect(popup.get_by_role('link', name='Open folder…', exact=True)).to_be_focused()
            page.keyboard.press('Tab')
            expect(popup.get_by_role('link', name='Manage projects…', exact=True)).to_be_focused()
            page.keyboard.press('Tab')
            expect(popup).not_to_be_visible()
            open_picker()
            page.locator('.vaw-ide-product-name').click()
            expect(popup).not_to_be_visible()
            open_picker()
            search.fill('Default Project')
            expect(popup.get_by_role('option').first).to_contain_text('Catalog project')
            page.keyboard.press('Enter')
            expect(page).to_have_url(base + '/projects/default')
            expect(trigger).to_have_text('Default Project')
            open_picker()
            popup.get_by_role('link', name='Manage projects…', exact=True).click()
            expect(page.get_by_role('heading', name='Projects', exact=True)).to_be_visible()
            open_picker()
            popup.get_by_role('link', name='Open folder…', exact=True).click()
            expect(page).to_have_url(base + '/source-project')
            for size in ({'width': 390, 'height': 844}, {'width': 1440, 'height': 1000}):
                page.set_viewport_size(size)
                open_picker()
                expect(search).to_be_focused()
                box = popup.bounding_box()
                assert box['x'] >= 0 and box['x'] + box['width'] <= size['width'] + 1, box
                assert box['y'] + box['height'] <= size['height'] + 1, box
                if size['width'] == 390:
                    page.screenshot(path='/tmp/vaw-project-picker-mobile.png')
                else:
                    page.screenshot(path='/tmp/vaw-project-picker-desktop.png')
                page.keyboard.press('Escape')
            page.evaluate("document.documentElement.style.fontSize='200%'")
            page.emulate_media(forced_colors='active')
            open_picker()
            expect(search).to_be_focused()
            box = popup.bounding_box()
            assert box['y'] + box['height'] <= 1001, box
            page.keyboard.press('Escape')
            assert not errors, errors
        except Exception:
            with contextlib.suppress(Exception): page.screenshot(path='/tmp/vaw-project-picker-failure.png')
            raise
        finally:
            browser.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default='http://127.0.0.1:5099')
    parser.add_argument('--browser-executable')
    args = parser.parse_args()
    verify(args.base_url.rstrip('/'), args.browser_executable)
    print('Project picker search, path disambiguation, keyboard, dismissal, folder/catalog switching, dirty buffers and mobile checks passed.')
