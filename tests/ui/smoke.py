"""Rendering tests with explicit API fixtures; not an API integration test."""
import functools
import http.server
import json
import pathlib
import threading
from playwright.sync_api import sync_playwright

root=pathlib.Path(__file__).resolve().parents[2]
out=root/'artifacts'/'ui-tests'
out.mkdir(parents=True,exist_ok=True)
handler=functools.partial(http.server.SimpleHTTPRequestHandler,directory=str(root/'src/Jarvis.Web/dist'))
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
base='http://127.0.0.1:'+str(server.server_port)
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True)
    page=browser.new_page(viewport={'width':1440,'height':1100})
    errors=[]
    page.on('pageerror',lambda e:errors.append(str(e)))
    def setup(route):
        route.fulfill(content_type='application/json',body=json.dumps({'required':True}))
    page.route('**/api/v1/setup/status',setup)
    page.goto(base)
    page.get_by_role('heading',name='Ein Zuhause für deine KI.').wait_for()
    page.screenshot(path=str(out/'setup-desktop.png'),full_page=True)
    page.set_viewport_size({'width':390,'height':844})
    page.wait_for_timeout(350)
    page.screenshot(path=str(out/'setup-mobile.png'),full_page=True)
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'mobile horizontal overflow'
    page.unroute('**/api/v1/setup/status')
    def fixture(route):
        path=route.request.url.split('/api/v1')[-1]
        data=[]
        if path=='/setup/status':data={'required':False}
        elif path=='/auth/me':data={'user':{'username':'Testnutzer','id':'ui-fixture'},'csrf':'test-only'}
        elif path=='/settings':data={}
        route.fulfill(content_type='application/json',body=json.dumps(data))
    page.route('**/api/v1/**',fixture)
    page.route('**/health/ready',lambda r:r.fulfill(content_type='application/json',body='{"status":"ok"}'))
    page.route('**/hubs/**',lambda r:r.fulfill(status=503,body='UI fixture has no live hub'))
    page.set_viewport_size({'width':1440,'height':1100})
    page.reload()
    page.get_by_role('heading',name='Hallo, Testnutzer.').wait_for()
    page.screenshot(path=str(out/'dashboard-desktop.png'),full_page=True)
    page.get_by_role('button',name='Research',exact=True).click()
    page.get_by_role('heading',name='Wissen, das du nachprüfen kannst.').wait_for()
    page.screenshot(path=str(out/'research-desktop.png'),full_page=True)
    page.set_viewport_size({'width':390,'height':844})
    page.wait_for_timeout(350)
    page.screenshot(path=str(out/'research-mobile.png'),full_page=True)
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'research mobile overflow'
    assert not errors, errors
    browser.close()
server.shutdown()
print('PASS: setup/dashboard/research rendering, responsive widths, no uncaught UI errors (explicit API fixtures; not backend integration)')
