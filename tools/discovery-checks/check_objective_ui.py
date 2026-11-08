"""Existing-map objective guidance in an isolated background editor; uses synthetic UI input only."""
import json, pathlib, socket, sys, time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 30
while not (endpoints := list((run / 'live').glob('editor-*.json'))):
    if time.monotonic() > deadline: raise RuntimeError('background editor did not publish its endpoint')
    time.sleep(.1)
meta = json.loads(endpoints[0].read_text())
sock = socket.create_connection(('127.0.0.1', meta['port']), timeout=90)
stream = sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'], client='objective-tutorial-check'))+'\n').encode()); stream.flush()
assert json.loads(stream.readline())['ok']
seq = 0

def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {}))+'\n').encode()); stream.flush()
    reply = json.loads(stream.readline())
    assert 'error' not in reply, reply
    result = reply['result']
    assert result.get('ok', True), result
    return result

def step(op, **args): return call('ui.input', dict(steps=[dict(op=op, frames=8, **args)]))
def tree(): return call('ui.tree', dict(limit=3000))['controls']
def contains(text): return any(text in c.get('text', '') for c in tree())
def select(actor): call('apply', dict(ops=[dict(op='select', ids=[actor])]))
def search(text):
    step('key', key='Ctrl+Shift+P'); step('type', text=text)
def start(lesson):
    search('tutorial: '+lesson); step('key', key='Enter')
def cancel():
    search('command: Cancel Current Tutorial'); step('key', key='Enter')
def shot(name): return call('ui.shot', dict(name=name, dir=str(run/'shots')))
def ref(actor): return {'$actor': actor}
def create(actor, cls, **props): return dict(op='create', id=actor, **{'class':'SBase.'+cls}, props=props)

call('ui.tree', dict(limit=1)) # Rejects a normal foreground editor before making fixture edits.
step('key', key='Escape'); step('key', key='Escape')
call('save', dict(path=str(run/'fixture.osmap')))
call('new', dict(id='Guide', title='Objective tutorial checks', discard=True))
call('apply', dict(label='Disposable existing-objective tutorial fixture', ops=[
    create('Story', 'SMission', GameMode=5, Objectives=[ref('ZoneA'), ref('ZoneB')]),
    create('ZoneA', 'SMission', GameMode=0, Objectives=[ref('TaskA')]),
    create('ZoneB', 'SMission', GameMode=0, Objectives=[ref('TaskB')]),
    create('TaskA', 'SObjective', ObjectiveName='Server room', Triggers=[ref('ComputerA')]),
    create('TaskB', 'SObjective', ObjectiveName='Archives', Triggers=[ref('ComputerB')]),
    create('ComputerA', 'SComputerObjectiveTrigger', TriggerName='Server terminal'),
    create('ComputerB', 'SComputerObjectiveTrigger', TriggerName='Archive terminal'),
    create('ImportedComputer', 'SComputerObjectiveTrigger', TriggerName='Imported terminal'),
    create('DiskTask', 'SObjective', Triggers=[ref('CarryDisk')]),
    create('CarryDisk', 'SFlag'),
]))
before = call('query', dict(q='find', kind='actor', fields=['id','class','props'], limit=1000))
select('ComputerA'); start('Set up a laptop objective')
assert contains('Server terminal is already in your map')
step('click', at='text:Next')
select('TaskB')
assert contains('Archive terminal already completes Archives'), 'Changing objective must change the tutorial subject'
step('click', at='text:Next')
assert contains('Archives already belongs to ZoneB'), 'Keep the nested zone hierarchy'
shot('existing-nested-objective')
select('ZoneA')
assert contains('Server room already belongs to ZoneA'), 'Follow a selected zone recursively'
select('ImportedComputer'); step('click', at='text:Show me again')
assert call('editor')['selection'] == ['ImportedComputer'], 'Repair must preserve the unlinked device selection'
assert contains('Connect the device to an objective')
step('click', at='text:Choose objective…')
assert contains('Choose the objective to link Imported terminal'), 'Repair chooser must link this device'
step('type', text='Archives'); step('key', key='Down'); step('key', key='Enter')
assert contains('Archives already belongs to ZoneB')
step('key', key='Ctrl+Z')
assert contains('Connect the device to an objective'), 'Undo must refresh guidance'
cancel()

select('CarryDisk'); start('Set up a disk and drop-off objective')
step('click', at='text:Next')
assert call('editor')['selection'] == ['CarryDisk'], 'Drop-zone placement must select the intended disk'
cancel()
after = call('query', dict(q='find', kind='actor', fields=['id','class','props'], limit=1000))
assert after == before, 'Guidance and repairing then undoing a link must preserve all actors and properties'

start('Set up mission zones')
step('click', at='text:Mission / objective diagram')
assert contains('Devices complete objectives') and contains('Chain zones')
shot('mission-objective-diagram')
scroll = next(c for c in tree() if '/MissionDiagram/' in c['path'] and c['type']=='ScrollContainer')
zone_before = next(c['rect'][1] for c in tree() if c.get('text', '').startswith('3. Chain zones'))
step('scroll', at=scroll['path'], clicks=-12)
assert next(c['rect'][1] for c in tree() if c.get('text', '').startswith('3. Chain zones')) < zone_before, 'Diagram must scroll to its zone example'
shot('mission-zone-diagram')
step('click', at='text:Close')
assert not any('/MissionDiagram/' in c['path'] for c in tree()), 'Close returns to the tutorial'
step('click', at='text:Mission / objective diagram'); step('key', key='Escape')
assert not any('/MissionDiagram/' in c['path'] for c in tree()) and contains('Step 1 of 6'), 'Escape closes only the diagram'
for _ in range(5): step('click', at='text:Next')
assert contains('Open routes and test the stages')
cancel()
assert call('query', dict(q='find', kind='actor', fields=['id','class','props'], limit=1000)) == before
print('PASS existing tasks, nested zones, subject switching, imported-link repair/undo, disk selection and diagrams; map preserved', flush=True)
