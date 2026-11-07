import json, math, pathlib, socket, sys
run=pathlib.Path(sys.argv[1]).resolve()
meta=json.loads(next((run/'live').glob('editor-*.json')).read_text())
sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=60)
stream=sock.makefile('rwb'); stream.write((json.dumps(dict(auth=meta['token'],client='window-selection-regression'))+'\n').encode()); stream.flush()
assert json.loads(stream.readline())['ok']
seq=0
def call(method, params=None, error=False):
 global seq
 seq+=1; stream.write((json.dumps(dict(id=seq,method=method,params=params or {}))+'\n').encode()); stream.flush()
 reply=json.loads(stream.readline()); assert ('error' in reply)==error, reply
 return reply.get('result',reply)
def step(op,**args): return call('ui.input',dict(steps=[dict(op=op,frames=4,**args)]))
def tree(): return call('ui.tree',dict(limit=1200))
def project(point):
 camera=call('editor')['camera']; p,y,_=[v*math.tau/65536 for v in camera['rot']]
 f=(math.cos(p)*math.cos(y),math.cos(p)*math.sin(y),math.sin(p)); r=(-math.sin(y),math.cos(y),0); u=(-math.sin(p)*math.cos(y),-math.sin(p)*math.sin(y),math.cos(p))
 rel=[a-b for a,b in zip(point,camera['loc'])]; dot=lambda a,b: sum(x*y for x,y in zip(a,b))
 x,y,w,h=next(c['rect'] for c in tree()['controls'] if c['path'].endswith('/Perspective')); dep=dot(rel,f)
 return [x+w/2+dot(rel,r)*w/(2*dep),y+h/2-dot(rel,u)*w/(2*dep)]
call('ui.tree',dict(limit=1)); assert call('editor')['map']=='WindowPick'
if len(sys.argv)>2 and sys.argv[2]=='inspect':
 print(json.dumps(tree(),indent=2)); print(call('ui.shot',dict(name='initial',dir=str(run))))
 sys.exit()
call('ui.input',dict(steps=[dict(op='escape',frames=180)]))
step('click',at='action:tools.select'); step('key',key='Alt+6')
for side in (-1,1):
 call('apply',dict(ops=[dict(op='workspace',camera=dict(at=[0 if side==-1 else 1100,0,300],rot=[0,0 if side==-1 else 32768,0]))]))
 for i in range(6):
  call('apply',dict(ops=[dict(op='select',mode='clear')]))
  step('click',at=project([544,-500+i*200,300]))
  assert call('editor')['selection']==['Window'+str(i)], ('glass',side,i,call('editor')['selection'])
  call('apply',dict(ops=[dict(op='select',mode='clear')]))
  # Each shape's bottom edge or top vertex lies on its solid frame.
  point=[510 if side==-1 else 578,-500+i*200, 218 if i in (0,3,4) else (372 if i==1 else 382)]
  step('click',at=project(point))
  assert call('editor')['selection']==['Window'+str(i)], ('frame',side,i,call('editor')['selection'])
 call('apply',dict(ops=[dict(op='select',mode='clear')]))
 step('click',at=project([512 if side==-1 else 576,-240,360]))
 assert call('editor')['selection']==['Room' if side==-1 else 'NextRoom'], ('circle corner',side,call('editor')['selection'])
 print('PASS six panes, six frames and surrounding wall from side',side)
call('apply',dict(ops=[dict(op='workspace',camera=dict(at=[0,0,300],rot=[0,0,0]))]))
call('ui.shot',dict(name='window-selection',dir=str(run)))
step('click',at='menu:Help'); step('click',at='item:Your First Map')
assert any(c.get('currentTab')=='Your first map' for c in tree()['controls'])
print(call('ui.shot',dict(name='tutorial-open',dir=str(run))))

# Closed/hidden panes should never intercept a click; undo must restore ordinary picking.
for flag in ('hidden','locked'):
 call('apply',dict(ops=[dict(op='update',id='Window0',**{flag:True}),dict(op='select',mode='clear')]))
 step('click',at=project([544,-500,300]))
 assert call('editor')['selection']!=['Window0'], flag
 call('undo')
 call('apply',dict(ops=[dict(op='select',mode='clear')]))
 step('click',at=project([544,-500,300]))
 assert call('editor')['selection']==['Window0'], 'undo '+flag
print('PASS hidden/locked panes and restored selection after undo')

def guide_dock(): return next(c for c in tree()['controls'] if c.get('currentTab')=='Your first map')
def close_guide():
 guide_dock()
 step('click',at='tab:Your first map',button='middle')
 assert not any(c.get('currentTab')=='Your first map' for c in tree()['controls']), 'Middle-click must dismiss tutorial'
 assert not any(c.get('text')=='Your first map' for c in tree()['controls']), 'Tutorial body must be hidden'
def open_guide():
 step('click',at='menu:Help'); step('click',at='item:Your First Map'); guide_dock()
close_guide()
call('save'); folder=call('editor')['folder']; call('open',dict(path=folder))
assert not any(c.get('currentTab')=='Your first map' for c in tree()['controls']), 'Closed tutorial must stay closed after reloading map'
open_guide()
step('click',at='text:Hide guide')
assert not any(c.get('currentTab')=='Your first map' for c in tree()['controls']), 'Hide guide must dismiss the tab too'
open_guide()
step('click',at='menu:View'); step('hover',at='item:UI Scale'); step('click',at='item:150%'); step('escape')
call('ui.shot',dict(name='tutorial-150',dir=str(run)))
close_guide(); open_guide()
call('ui.shot',dict(name='tutorial-reopened-150',dir=str(run)))
step('click',at='menu:View'); step('hover',at='item:UI Scale'); step('click',at='item:100%'); step('escape')
close_guide(); open_guide()
call('ui.shot',dict(name='tutorial-reopened',dir=str(run)))
print('PASS tutorial close, Hide guide, save/reload, reopen and 150% UI scale')
