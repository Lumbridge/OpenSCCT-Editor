"""Real Choose mission/objective and state-aware tutorial regression, against a disposable Ui.osmap fixture."""
import json, pathlib, socket, sys, time
run=pathlib.Path(sys.argv[1]).resolve()
deadline=time.monotonic()+30
while not (files:=list((run/'live').glob('editor-*.json'))):
    if time.monotonic()>deadline: raise RuntimeError('Editor did not publish its endpoint')
    time.sleep(.1)
meta=json.loads(files[0].read_text()); sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=90); stream=sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'],client='choose-parent-regression'))+'\n').encode()); stream.flush()
assert json.loads(stream.readline())['ok']
seq=0
def call(method,params=None):
    global seq
    seq+=1; stream.write((json.dumps(dict(id=seq,method=method,params=params or {}))+'\n').encode()); stream.flush()
    reply=json.loads(stream.readline()); assert 'error' not in reply,reply
    return reply['result']
def step(op,**args): return call('ui.input',dict(steps=[dict(op=op,frames=8,**args)]))
def tree(): return call('ui.tree',dict(limit=3000))['controls']
def search(text): step('key',key='Ctrl+Shift+P'); step('type',text=text); step('key',key='Enter')
def select(actor): search('@'+actor)
def props(actor): return call('query',dict(q='get',ids=[actor],fields=['props']))['items'][0]['props']
def linked(parent,prop,child): return child in json.dumps(props(parent).get(prop,[]))
def choose(row,text,cancel=False):
    button=next(c for c in tree() if '/'+row+'/' in c['path'] and c['type']=='Button')
    step('click',at=button['path']); step('type',text=text); step('key',key='Down')
    step('click',at='text:Cancel' if cancel else 'text:Choose')
def contains(text): return any(text in c.get('text','') for c in tree())
def shot(name): call('ui.shot',dict(name=name,dir=str(run/'shots')))
call('ui.tree',dict(limit=1)) # Ordinary interactive editors reject synthetic UI calls.
assert pathlib.Path(call('editor')['folder']).name=='Ui.osmap','Use a disposable mission-link-checks fixture'
counts=call('summary')['counts']; before=props('Mission0')
assert not linked('Mission0','Objectives','ExtraObjective0'),'Generate a fresh fixture'
search('panel: Missions')
select('ExtraObjective0'); choose('MissionRow','Infiltrate')
assert linked('Mission0','Objectives','ExtraObjective0'),'Choose mission only changed the viewed mission; it must link the selected objective'
shot('choose-mission-links-selected-objective')
call('undo'); assert props('Mission0')==before,'One undo restores the mission'
call('redo'); assert linked('Mission0','Objectives','ExtraObjective0')
after=props('Mission0'); choose('MissionRow','Infiltrate'); assert props('Mission0')==after,'Already-linked choice is a no-op'
call('undo'); assert props('Mission0')==before,'Duplicate choice must not add an undo step'
call('redo')
print('PASS Choose mission links the selected objective, undo/redo, duplicate no-op',flush=True)

select('ExtraObjective1') # Keep an unlinked objective as the chooser target.
select('ExtraComputer1'); before_objective=props('ExtraObjective1')
choose('ObjectiveRow','Research files')
assert linked('ExtraObjective1','Triggers','ExtraComputer1'),'Choose objective must link the selected device'
call('undo'); assert props('ExtraObjective1')==before_objective
call('redo'); assert linked('ExtraObjective1','Triggers','ExtraComputer1')
choose('MissionRow','Infiltrate')
assert linked('Mission0','Objectives','ExtraObjective1'),'Choosing a mission for a selected laptop links its existing objective'
shot('choose-mission-follows-device-objective')
select('Mission0'); before=props('Mission0'); choose('MissionRow','Infiltrate'); assert props('Mission0')==before,'Browsing with a mission selected must not self-link'
select('Objective1'); before=props('Mission0'); choose('MissionRow','Infiltrate',cancel=True); assert props('Mission0')==before
print('PASS Choose objective links devices; device-to-mission follows its owner; view-only and Cancel preserve links',flush=True)

select('Computer0'); search('tutorial: laptop')
assert contains('Use the device already placed')
step('click',at='text:Next'); assert contains('Use the existing objective')
step('click',at='text:Next'); assert contains('The mission link is already complete')
step('click',at='text:Next'); assert contains('The device link is already complete')
shot('laptop-guide-recognises-existing-chain'); step('click',at='text:Cancel')
assert call('summary')['counts']==counts,'Guidance must not create duplicate devices, objectives or missions'
call('save')
print('PASS laptop guide follows the real chain and never creates duplicate actors',flush=True)
