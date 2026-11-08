"""Checks the real palette and lessons in a disposable background editor. No third-party packages."""
import json, pathlib, socket, sys, time, re
run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 30
while not (files := list((run/'live').glob('editor-*.json'))):
    if time.monotonic() > deadline: raise RuntimeError('background editor did not publish its endpoint')
    time.sleep(.1)
meta = json.loads(files[0].read_text())
sock = socket.create_connection(('127.0.0.1', meta['port']), timeout=90)
stream = sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'], client='discovery-check'))+'\n').encode()); stream.flush()
assert json.loads(stream.readline())['ok']
seq = 0
def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {}))+'\n').encode()); stream.flush()
    reply = json.loads(stream.readline())
    assert 'error' not in reply, reply
    return reply['result']
def step(op, **args): return call('ui.input', dict(steps=[dict(op=op, frames=8, **args)]))
def tree(): return call('ui.tree', dict(limit=3000))['controls']
def contains(text): return any(text in c.get('text','') for c in tree())
def shot(name): call('ui.shot',dict(name=name, dir=str(run/'shots')))
def palette(): return next(c for c in tree() if 'paletteResults' in c)
def highlight(): return next((c for c in tree() if c.get('highlightTarget')), None)
def search(text):
    step('key',key='Escape'); step('key',key='Ctrl+Shift+P'); step('type',text=text)
def card(): return [c for c in tree() if '/TutorialCard/' in c['path']]
def cancel(): step('click',at='text:Cancel'); assert not card()

call('ui.tree',dict(limit=1)) # The user's own editor rejects UI driving.
before = call('summary')['counts']
if '--navigation-only' in sys.argv:
    search('command: Simple Workspace'); step('key',key='Enter')
    search('control: Identity Name'); step('key',key='Enter')
    assert any(c.get('currentTab')=='Identity' for c in tree()) and highlight()
    search('command: Fire Selected'); step('key',key='Alt+Enter')
    assert any(c.get('action')=='gameplay.zone.fireSelected' for c in tree()), 'Reveal the collapsed completion toolbar'
    search('tutorial: Name your level'); step('key',key='Enter'); step('click',at='text:Next')
    step('key',key='Escape'); assert not card() and highlight() is None
    assert call('summary')['counts']==before
    shot('workspace-and-collapsed-controls')
    print('PASS Simple-workspace controls, collapsed mission toolbar and keyboard tutorial cancellation',flush=True)
    sys.exit(0)

search('Laptop'); shot('laptop-search')
assert contains('Locate the tile'), 'Laptop should select the computer placeable'
step('key',key='Enter')
assert any(c.get('currentTab')=='Placeables' for c in tree())
assert contains('Computer'), 'Computer must be visible after navigation'
shot('laptop-highlight')
h = highlight(); assert h, 'Found tile needs a spotlight'
x,y,w,hgt = h['highlightRect']
step('hover', at=[x+w/2,y+hgt/2]); assert highlight(), 'Keep the spotlight while hovering'
step('hover', at=[5,5]); assert highlight() is None, 'Dismiss only after mouse enters and leaves'
print('PASS laptop navigates to the Computer tile', flush=True)

search('how do I setup a ladder'); assert contains('Start tutorial')
step('key',key='Enter'); assert card() and contains('Step 1 of')
shot('ladder-tutorial')
step('click',at='text:−'); assert not contains('Step 1 of') and any(c.get('text')=='×' for c in card())
step('click',at='text:+'); assert contains('Step 1 of')
step('click',at='text:Next'); assert contains('Step 2 of')
step('click',at='text:Back'); assert contains('Step 1 of')
step('click',at='text:Next'); cancel()
search('Resume Last Tutorial'); step('key',key='Enter'); assert contains('Step 2 of')
cancel()
print('PASS tutorial next/back/cancel and saved resume', flush=True)

search('tutorial: laptop'); step('key',key='Enter'); assert card()
for _ in range(4): step('click',at='text:Next')
assert contains('Finish'); step('click',at='text:Finish'); assert not card()
search('tutorial: laptop'); assert contains('Completed'); step('key',key='Escape')
print('PASS finishing a tutorial and completion status', flush=True)

search('tutorial: glass'); step('key',key='Ctrl+D'); step('click',at='text:Favourites')
assert contains('Start tutorial'); shot('favourites')
step('key',key='Enter'); assert contains('Use the glass'); cancel()
search('Delete'); step('key',key='Alt+Enter')
assert call('summary')['counts']==before, 'Show me must not run destructive commands'
print('PASS favourites and safe command location', flush=True)

search('Name'); step('click',at='text:Panels'); step('type',text='Identity'); step('key',key='Enter')
assert any(c.get('currentTab')=='Identity' for c in tree())
search('@'); shot('objects'); step('key',key='Escape')
step('key',key='F1'); shot('shortcuts'); step('key',key='Escape')
search('zzzzdoesnotexist'); assert contains('0 results')
shot('no-results'); step('key',key='Escape')
assert call('summary')['counts']==before, 'Lessons and navigation must leave map objects intact'
print('PASS category navigation, objects, shortcuts, no-result guidance; map unchanged', flush=True)

search('assets: laptop'); assert palette()['paletteResults'][0]['kind']=='Asset'
step('key',key='Enter'); assert highlight(), 'Asset result needs a spotlight'
assert any(c.get('currentTab')=='Assets' for c in tree()); shot('asset-highlight')
search('class: Engine.Actor'); assert palette()['paletteResults'], 'Include non-placeable base classes'
step('key',key='Enter'); assert highlight(); shot('class-highlight')
assert any(c.get('text')=='Place' and c.get('disabled') for c in tree()), 'Base classes cannot be placed'
search('preset: Square 512'); assert any(r['kind']=='Preset' for r in palette()['paletteResults'])
step('key',key='Enter'); assert highlight(); shot('geometry-highlight')
search('prefab: objective computer'); assert any(r['kind']=='Prefab' for r in palette()['paletteResults'])
step('key',key='Enter'); assert highlight(); shot('prefab-highlight')
search('control: Name'); assert any(r['kind']=='Control' for r in palette()['paletteResults'])
step('key',key='Enter'); assert highlight()
search('command: Delete'); step('key',key='Alt+Enter')
assert call('ui.tree',dict(limit=1))['popups'], 'Commands without toolbar buttons reveal their actual menu'
step('key',key='Escape')
print('PASS exact assets, base classes, geometry presets, prefabs, controls and menu-command navigation', flush=True)

search('Textures'); assert palette()['paletteSelected']=='assets-tab:Texture'
step('key',key='Enter'); assert any(c.get('currentTab')=='Assets' for c in tree()) and highlight()
search('Loading screen'); assert palette()['paletteSelected']=='identity-field:loading'
step('key',key='Enter'); assert any(c.get('currentTab')=='Identity' for c in tree()) and highlight()
search('tool: Ladder'); step('key',key='Enter')
assert any(c.get('action')=='tools.ge' and c.get('pressed') for c in tree()) and highlight()
print('PASS direct textures, loading-screen and ladder destinations', flush=True)

search('tutorial'); lessons = [r for r in palette()['paletteResults'] if r['kind']=='Tutorial']
assert len(lessons)>=30
step('key',key='Escape')
steps = 0
for lesson in lessons:
    search(lesson['title']); assert palette()['paletteSelected']==lesson['key'], lesson
    step('key',key='Enter')
    n = int(next(re.search(r'Step 1 of (\d+)', c.get('text','')) for c in card() if re.search(r'Step 1 of (\d+)', c.get('text',''))).group(1))
    for index in range(n):
        assert any(c.get('tutorialStep')==index and c.get('tutorial')==lesson['key'][2:] for c in tree()), (lesson,index)
        assert highlight(), ('Missing target',lesson,index)
        steps += 1
        if index+1<n: step('click',at='text:Next')
    cancel()
    if steps%20<5: print(f'Checked {steps} tutorial destinations',flush=True)
assert call('summary')['counts']==before
print(f'PASS all {len(lessons)} tutorials and {steps} step destinations; map objects unchanged', flush=True)
