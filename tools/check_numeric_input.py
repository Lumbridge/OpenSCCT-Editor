"""Type one character at a time, past the live-edit delay, in a disposable background Editor.

Usage: python -B tools/check_numeric_input.py <run-folder> <DoorPlacement.osmap>
Generate the fixture with tools/door-prefab-checks. The editor needs its own OSE_LIVE_DIR at <run-folder>/live.
An optional third argument is WindowTest.osmap from tools/window-checks, for authored window/stair scaling.
"""
import json, math, pathlib, socket, sys, time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic()+45
while not (files := list((run/'live').glob('editor-*.json'))):
    if time.monotonic() > deadline: raise RuntimeError('No disposable editor endpoint')
    time.sleep(.1)
meta = json.loads(files[0].read_text())
sock = socket.create_connection(('127.0.0.1',meta['port']),timeout=90)
stream = sock.makefile('rwb'); seq = 0
stream.write((json.dumps(dict(auth=meta['token'],client='numeric-input-check'))+'\n').encode());stream.flush()
assert json.loads(stream.readline())['ok']

def call(method,params=None):
    global seq
    seq += 1;stream.write((json.dumps(dict(id=seq,method=method,params=params or {}))+'\n').encode());stream.flush()
    r=json.loads(stream.readline());assert 'error' not in r,r;return r['result']
def step(op,frames=12,**kw):return call('ui.input',dict(steps=[dict(op=op,frames=frames,**kw)]))
def tree():return call('ui.tree',dict(limit=5000))
def nav(q):step('key',key='Ctrl+Shift+P');step('type',text=q);step('key',key='Enter')
def get(id='Door0',fields=None):return call('query',dict(q='get',id=id,fields=fields or ['loc','scale','door','props']))['items'][0]
def close(a,b):assert math.isclose(a,b,rel_tol=1e-8,abs_tol=1e-8),(a,b)
def field(label,scope='/Inspector/Inspector/',axis=0):
    controls=tree()['controls']
    caption=next(c for c in controls if c.get('text','').casefold()==label.casefold() and scope in c['path'])
    row=caption['path'].rsplit('/',1)[0]
    box=[c for c in controls if c['type']=='SpinBox' and c['path'].startswith(row+'/')][axis]
    parents=[c for c in controls if c['type']=='ScrollContainer' and box['path'].startswith(c['path']+'/')]
    for parent in reversed(parents):
        for _ in range(20):
            y,h=box['rect'][1],box['rect'][3];sy,sh=parent['rect'][1],parent['rect'][3]
            if sy<=y and y+h<=sy+sh:break
            step('scroll',at=parent['path'],clicks=3 if y<sy else -3)
            box=next(c for c in tree()['controls'] if c['path']==box['path'])
        else:raise AssertionError('Cannot reveal '+label)
    return box
def check_edit(box,text):
    t=tree();current=next((c for c in t['controls'] if c['path']==box['path']),None)
    assert current is not None,'Editing rebuilt the field'
    assert t.get('focus','').startswith(box['path']+'/'),'Typing lost keyboard focus'
    assert current.get('text')==text,('Typed text/caret was rewritten',text,current.get('text'))
def type_number(box,text,finish='Enter'):
    step('click',at=box['path'],offset=[.35,.5]);step('key',key='Ctrl+A');prefix=''
    for char in text:
        prefix+=char;step('type',text=char);check_edit(box,prefix)
    if finish:step('key',key=finish)

call('ui.tree',dict(limit=1)) # Normal interactive editors reject UI driving.
nav('commands: Full Workspace')
for _ in range(100):
    if not call('editor')['dirty']:break
    call('undo')
else:raise AssertionError('Could not revert the disposable test map')
call('open',dict(path=str(pathlib.Path(sys.argv[2]).resolve())))
call('apply',dict(ops=[dict(op='select',id='Door0')]))
nav('panels: Inspector')
before=get()['loc'][0]
box=field('Location');type_number(box,'123');close(get()['loc'][0],123)
call('undo');close(get()['loc'][0],before)
call('redo');close(get()['loc'][0],123)
print('PASS paused integer entry, focus retention and one-step undo/redo',flush=True)
for text in ['1.25','-12.5','0.125','.75','-0.25']:
    type_number(field('Location'),text);close(get()['loc'][0],float(text))
print('PASS decimals, negative values, leading decimal points and sub-unit values',flush=True)

box=field('Location');type_number(box,'123.45',finish=None)
step('key',key='Home');step('key',key='Right');step('key',key='Right');step('type',text='9');check_edit(box,'1293.45')
step('key',key='Backspace');check_edit(box,'123.45');step('key',key='Tab');close(get()['loc'][0],123.45)
box=field('Location');step('click',at=box['path'],offset=[.35,.5]);step('key',key='Ctrl+A')
step('type',text='42.75',frames=1);step('key',key='Tab');close(get()['loc'][0],42.75)
print('PASS insertion/deletion inside a number, Tab and immediate focus-loss submission',flush=True)

box=field('Location');type_number(box,'0.25')
step('click',at=box['path'],offset=[.97,.2]);close(get()['loc'][0],1.25)
box=field('Location');step('click',at=box['path'],offset=[.35,.5]);step('key',key='Ctrl+A')
step('type',text='2.5',frames=1);step('scroll',at=box['path'],clicks=1);close(get()['loc'][0],3.5)
print('PASS stepper and wheel use the entered value and normal arrow increment',flush=True)

box=field('Location');type_number(box,'1',finish=None)
call('apply',dict(ops=[dict(op='update',id='Door0',scale=1.1)]))
step('type',text='2');check_edit(box,'12');step('type',text='.5');check_edit(box,'12.5')
step('key',key='Tab')
next_box=field('Location',axis=1)
step('type',text='8');check_edit(next_box,'8');step('key',key='Enter');close(get()['loc'][1],8)
print('PASS a design refresh during typing preserves the active field and subsequent Tab focus',flush=True)

width_before=get()['door']['width']
type_number(field('Draw scale'),'1.25');close(get()['door']['width'],width_before*1.25)
call('undo');close(get()['door']['width'],width_before)
call('apply',dict(ops=[dict(op='create',id='NumericProbe',**{'class':'Engine.Light'},props=dict(CollisionRadius=40.0)),dict(op='select',id='NumericProbe')]))
nav('panels: Inspector');type_number(field('Collision Radius'),'2.75');close(get('NumericProbe')['props']['CollisionRadius'],2.75)
type_number(field('Draw scale'),'1.25');close(get('NumericProbe')['scale'],1.25)
print('PASS scalar scale and generic numeric property controls',flush=True)

call('apply',dict(ops=[dict(op='select',id='Door0')]))
nav('panels: Inspector');type_number(field('Width'),'192');close(get()['door']['width'],192)
type_number(field('Width'),'192.5');close(get()['door']['width'],192.5)
type_number(field('Open time'),'1.75');close(get()['door']['moveTime'],1.75)
nav('panels: Doors');box=field('Width','/Doors/Doors/');type_number(box,'256.5')
box=field('Open time','/Doors/Doors/');type_number(box,'2.5');close(float(next(c for c in tree()['controls'] if c['path']==box['path'])['text']),2.5)
nav('panels: Prefabs')
search=next(c for c in tree()['controls'] if c['type']=='LineEdit' and '/Prefabs/Prefabs/' in c['path'])
step('click',at=search['path']);step('type',text='hinged double')
grid=next(c for c in tree()['controls'] if c['type']=='ItemList' and '/Prefabs/Prefabs/' in c['path'])
step('click',at=grid['path'],offset=[.2,.15])
box=field('Width','/Prefabs/Prefabs/');type_number(box,'384.5');close(float(next(c for c in tree()['controls'] if c['path']==box['path'])['text']),384.5)
box=field('Move Time','/Prefabs/Prefabs/');type_number(box,'1.5');close(float(next(c for c in tree()['controls'] if c['path']==box['path'])['text']),1.5)
print('PASS integer and decimal entry in door Inspector, placement palette and prefab parameters',flush=True)
if len(sys.argv)>3:
    nav('panels: Inspector')
    for _ in range(100):
        if not call('editor')['dirty']:break
        call('undo')
    call('open',dict(path=str(pathlib.Path(sys.argv[3]).resolve())))
    window=next(a for a in call('query',dict(q='find',**{'class':'SBase.SVitre'},fields=['id','window']))['items'] if a.get('window'))
    call('apply',dict(ops=[dict(op='select',id=window['id'])]));nav('panels: Inspector')
    width=window['window']['width'];type_number(field('Draw scale'),'1.25')
    close(get(window['id'],['window'])['window']['width'],width*1.25)
    call('undo');close(get(window['id'],['window'])['window']['width'],width)
    call('apply',dict(ops=[dict(op='stair',id='NumericStair',at=[0,600,0],set=dict(layout='straight')),dict(op='select',id='NumericStair')]))
    nav('panels: Inspector');run_before=get('NumericStair',['stair'])['stair']['run']
    type_number(field('Main scale'),'1.25');close(get('NumericStair',['stair'])['stair']['run'],run_before*1.25)
    call('undo');close(get('NumericStair',['stair'])['stair']['run'],run_before)
    print('PASS authored window and stair scale submits once and undoes in one step',flush=True)
call('ui.shot',dict(name='numeric-entry-checked',dir=str(run/'shots')))
print('PASS numeric input regression checks',flush=True)
