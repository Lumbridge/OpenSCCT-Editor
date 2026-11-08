"""Exercise doors, prefab browsing and lesson destinations in a disposable --background editor."""
import json, pathlib, re, socket, sys, time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 30
while not (files := list((run / 'live').glob('editor-*.json'))):
    if time.monotonic() > deadline: raise RuntimeError('No disposable editor endpoint')
    time.sleep(.1)
meta = json.loads(files[0].read_text())
sock = socket.create_connection(('127.0.0.1', meta['port']), timeout=90)
stream = sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'], client='door-ui-check'))+'\n').encode()); stream.flush()
assert json.loads(stream.readline())['ok']
seq = 0
def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {}))+'\n').encode()); stream.flush()
    reply = json.loads(stream.readline()); assert 'error' not in reply, reply
    return reply['result']
def step(op, **args): return call('ui.input', dict(steps=[dict(op=op, frames=8, **args)]))
def tree(): return call('ui.tree', dict(limit=4000))['controls']
def search(text):
    step('key', key='Ctrl+Shift+P'); step('type', text=text)
def navigate(query): search(query); step('key', key='Enter')
def shot(name): return call('ui.shot', dict(name=name, dir=str(run/'shots')))
def doors(): return call('query',dict(q='find', **{'class':'Engine.Mover'}, fields=['id','door','loc','bounds']))['items']
def number(label, value, scope):
    controls = tree(); caption = next(c for c in controls if c.get('text')==label and scope in c['path'])
    for _ in range(20):
        scroll=next(c for c in controls if c['type']=='ScrollContainer' and scope in c['path'])
        if caption['rect'][1] < scroll['rect'][1]+scroll['rect'][3]-30: break
        step('scroll',at=scroll['path'],clicks=-3)
        controls=tree(); caption=next(c for c in controls if c.get('text')==label and scope in c['path'])
    parent = caption['path'].rsplit('/',1)[0]
    spin = next(c for c in controls if c['type']=='SpinBox' and c['path'].startswith(parent+'/'))
    step('click', at=spin['path'], offset=[.35,.5]); step('key',key='Ctrl+A'); step('type',text=str(value)); step('key',key='Enter')

call('ui.tree',dict(limit=1)) # A normal user editor rejects UI driving.
for _ in range(100):
    if not call('editor')['dirty']: break
    call('undo')
call('open',dict(path=call('editor')['folder']))
navigate('commands: Full Workspace')
navigate('panels: Doors')
step('scroll',at=next(c['path'] for c in tree() if c['type']=='ScrollContainer' and '/Doors/Doors/' in c['path']),clicks=30)
assert not shot('doors-palette')['lint'], 'Door palette must fit the default dock'
step('click',at='text:Industrial')
number('Width',160,'/Doors/Doors/')
before = len(doors())
step('click',at='text:Place Door'); step('move',at='view:Perspective',offset=[.55,.62]); shot('door-preview')
step('click',at='view:Perspective',offset=[.55,.62])
placed = doors(); assert len(placed)==before+1, 'Click placement creates one actor'
new = next(d for d in placed if d['id']!='Door0')
assert new['door']['style']=='industrial' and new['door']['width']==160, new
assert new['door']['cutDepth']>0, 'Wall placement owns a cut'
call('undo'); assert len(doors())==before
call('redo'); assert len(doors())==before+1
step('escape'); navigate('commands: Move')
call('apply',dict(ops=[dict(op='select',id=new['id'])]))
navigate('panels: Inspector')
number('Width',192,'/Inspector/Inspector/')
assert next(d for d in doors() if d['id']==new['id'])['door']['width']==192, 'Inspector changes authored dimensions'
call('undo'); assert next(d for d in doors() if d['id']==new['id'])['door']['width']==160
call('redo'); shot('door-edited')

navigate('panels: Doors')
step('scroll',at=next(c['path'] for c in tree() if c['type']=='ScrollContainer' and '/Doors/Doors/' in c['path']),clicks=30)
before = len(doors())
step('drag',at='text:Glazed',to='view:Perspective',toOffset=[.7,.65])
assert len(doors())==before+1, 'Drag placement creates exactly one door'
assert any(d['door']['style']=='glazed' for d in doors()), 'Drag chooses its actual style'
call('undo'); assert len(doors())==before
print('PASS door click/drag placement, settings, wall cut, Inspector and undo/redo',flush=True)

navigate('panels: Prefabs')
assert not [l for l in shot('prefab-browser')['lint'] if '/Prefabs/' in l['path']], 'Prefab browser must fit'
controls=tree(); searchbox=next(c for c in controls if c['type']=='LineEdit' and '/Prefabs/Prefabs/' in c['path'])
step('click',at=searchbox['path']); step('type',text='shipping crate')
controls=tree(); grid=next(c for c in controls if c['type']=='ItemList' and '/Prefabs/Prefabs/' in c['path'])
step('click',at=grid['path'],offset=[.2,.08])
assert any(c.get('text')=='Shipping crate' for c in tree()),'Search selects the matching prefab'
star = next(c for c in tree() if 'Add favourite' in c.get('text',''))
step('click',at=star['path']); assert any('Remove favourite' in c.get('text','') for c in tree())
step('click',at='text:Favourites')
assert any(re.search(r'1 of \d+ prefabs',c.get('text','')) for c in tree()),'Favourites filter retains the starred piece'
step('click',at=searchbox['path']);step('key',key='Ctrl+A');step('type',text='zzzznothing')
assert any('No prefabs match' in c.get('text','') for c in tree()),'Empty search explains recovery'
step('key',key='Ctrl+A');step('key',key='Backspace');step('click',at='text:Favourites')
shot('prefab-favourites')
print('PASS prefab search, favourite toggle, filtering and no-result guidance',flush=True)

before=call('summary')['counts']; destinations=0
for title, lesson in [('Create and edit doors','doors'),('Place a door with a switch','door-switch'),('Browse and customise prefabs','prefabs'),('Set up moving doors and platforms','movers'),('Configure door hacking and team access','door-access')]:
    navigate('tutorial: '+title)
    n=int(next(re.search(r'Step 1 of (\d+)',c.get('text','')) for c in tree() if re.search(r'Step 1 of (\d+)',c.get('text',''))).group(1))
    for index in range(n):
        controls=tree(); assert any(c.get('tutorial')==lesson and c.get('tutorialStep')==index for c in controls)
        assert any(c.get('highlightTarget') for c in controls),(lesson,index)
        if index+1<n:step('click',at='text:Next')
        destinations+=1
    shot('tutorial-'+lesson);step('click',at='text:Cancel')
assert call('summary')['counts']==before,'Tutorials must not edit the map'
print(f'PASS {destinations} door/prefab/mover tutorial destinations; map unchanged',flush=True)
