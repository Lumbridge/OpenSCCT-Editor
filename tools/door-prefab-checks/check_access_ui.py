"""Real material drags and access-assembly navigation in a disposable --background Editor."""
import json, pathlib, socket, sys, time
run=pathlib.Path(sys.argv[1]).resolve(); fixture=pathlib.Path(sys.argv[2]).resolve()
meta=json.loads(next((run/'live').glob('editor-*.json')).read_text()); sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=90); stream=sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'],client='door-access-ui-check'))+'\n').encode());stream.flush(); assert json.loads(stream.readline())['ok']
seq=0
def call(method,params=None):
    global seq
    seq+=1;stream.write((json.dumps(dict(id=seq,method=method,params=params or {}))+'\n').encode());stream.flush();r=json.loads(stream.readline());assert 'error' not in r,r;return r['result']
def step(op,**kw):return call('ui.input',dict(steps=[dict(op=op,frames=8,**kw)]))
def tree():return call('ui.tree',dict(limit=4000))['controls']
def nav(text):step('key',key='Ctrl+Shift+P');step('type',text=text);step('key',key='Enter')
def shot(name):return call('ui.shot',dict(name=name,dir=str(run/'shots')))
def design():return call('query',dict(q='get',id='Door0',fields=['door']))['items'][0]['door']
def revert():
    for _ in range(50):
        if not call('editor')['dirty']:return
        call('undo')
    raise AssertionError('Could not revert disposable edits')
call('ui.tree',dict(limit=1)); revert();call('open',dict(path=str(fixture/'DoorMaterialDrop.osmap')))
nav('commands: Full Workspace');nav('assets: DoorPaint')
grid=next(c for c in tree() if c['type']=='ItemList' and '/Assets/Assets/' in c['path'])
step('drag',at=grid['path'],offset=[.04,.25],to='view:Perspective')
assert design()['material']=='MyLevel.Materials.DoorPaint','Material drag must update saved door leaf finish'
assert design()['frameMaterial']=='Editor.DoorFrame','Leaf painting keeps the frame finish'
shot('painted-door-leaf');call('undo');assert design()['material']=='';call('redo');assert design()['material']=='MyLevel.Materials.DoorPaint'
call('undo')
# A horizontal 90-degree FOV at x=-400: y=132 is the centre of the right jamb, whose front is x=-12.
step('drag',at=grid['path'],offset=[.04,.25],to='view:Perspective',toOffset=[.5+132/(2*388),.5])
assert design()['frameMaterial']=='MyLevel.Materials.DoorPaint','Dropping on a jamb paints the permanent frame'
assert design()['material']=='','Frame painting preserves the leaf finish'
shot('painted-door-frame');call('undo');assert design()['frameMaterial']=='Editor.DoorFrame'
step('drag',at=grid['path'],offset=[.04,.25],to='view:Perspective')
call('apply',dict(ops=[dict(op='update',id='Door0',scale3d=[1,1.5,1])]))
assert design()['width']==384 and design()['material']=='MyLevel.Materials.DoorPaint','Resizing keeps the dropped material'
painted=run/'PaintedDoor.osmap';call('save',dict(path=str(painted)));call('open',dict(path=str(painted)))
assert design()['width']==384 and design()['material']=='MyLevel.Materials.DoorPaint','Save/reopen retains dimensions and finish'
assert (painted/'assets/textures/door-paint.png').exists(),'Save As carries the applied texture'
assert call('build',dict(fast=True,out=str(run/'build/DoorMaterialDrop')))['ok'],'Painted resized door packages successfully'
call('open',dict(path=str(fixture/'DoorMaterialDrop.osmap')))
print('PASS real leaf and frame material drags, target selection, undo and redo',flush=True)
print('PASS dropped finish survives resizing, Save As, reopening and a strict map build',flush=True)

revert();call('open',dict(path=str(fixture/'AccessDoorGallery.osmap')));nav('panels: Doors')
step('scroll',at=next(c['path'] for c in tree() if c['type']=='ScrollContainer' and '/Doors/Doors/' in c['path']),clicks=-3)
step('click',at='text:Hinged pair');assert any(c.get('text')=='Double hinged doors' for c in tree());shot('double-hinged-prefab')
nav('panels: Doors');step('click',at='text:Sliding pair');assert any(c.get('text')=='Double sliding doors' for c in tree());shot('double-sliding-prefab')
nav('panels: Doors');step('click',at='text:Automatic access');assert any(c.get('text')=='Automatic access door' for c in tree());shot('automatic-side-access')
nav('panels: Doors');step('click',at='text:Hack panel');assert any(c.get('text')=='Hackable access door' for c in tree());shot('hackable-prefab')
nav('tutorial: Configure door hacking and team access')
for i in range(5):
    assert any(c.get('tutorial')=='door-access' and c.get('tutorialStep')==i for c in tree())
    if i<4:step('click',at='text:Next')
step('click',at='text:Cancel')
print('PASS all four assembly shortcuts and the door-access tutorial',flush=True)
call('open',dict(path=str(fixture/'hackable_door.osmap')))
panel=call('query',dict(q='find',**{'class':'SBase.SInterrupteur'},fields=['id','loc','event']))['items'][0]
call('apply',dict(ops=[dict(op='update',id=panel['id'],at=[panel['loc'][0]+64,*panel['loc'][1:]]),dict(op='select',id=panel['id'])]))
nav('panels: Inspector')
caption=next(c for c in tree() if c.get('text')=='Hack time' and '/Inspector/' in c['path']);parent=caption['path'].rsplit('/',1)[0]
spin=next(c for c in tree() if c['type']=='SpinBox' and c['path'].startswith(parent+'/'))
step('click',at=spin['path'],offset=[.35,.5]);step('key',key='Ctrl+A');step('type',text='12');step('key',key='Enter')
changed=call('query',dict(q='get',id=panel['id'],fields=['props','event','loc']))['items'][0]
assert changed['props']['iHackingTime']==12 and changed['event']==panel['event'] and changed['loc'][0]==panel['loc'][0]+64
shot('moved-hack-panel-inspector');step('click',at='text:Select linked doors')
assert call('editor')['selectionCount']==1,'Panel navigation retains its linked door'
print('PASS moving a panel, editing its hack time in Inspector and selecting the retained door link',flush=True)
