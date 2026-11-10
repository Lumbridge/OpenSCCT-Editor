"""Mission settings regression in an isolated background editor on a fresh Ui.osmap fixture."""
import json
import pathlib
import socket
import sys
import time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 30
while not (endpoints := list((run / 'live').glob('editor-*.json'))):
    if time.monotonic() > deadline:
        raise RuntimeError('Background editor did not publish its endpoint')
    time.sleep(.1)
meta = json.loads(endpoints[0].read_text())
sock = socket.create_connection(('127.0.0.1', meta['port']), timeout=90)
stream = sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'], client='mission-sequence-check')) + '\n').encode())
stream.flush()
assert json.loads(stream.readline())['ok']
seq = 0


def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {})) + '\n').encode())
    stream.flush()
    reply = json.loads(stream.readline())
    assert 'error' not in reply, reply
    result = reply['result']
    assert result.get('ok', True), result
    return result


def step(op, **args):
    if op == 'wait':
        op, args = 'move', dict(at=[0, 0], **args)
    return call('ui.input', dict(steps=[dict(op=op, **({'frames': 8} | args))]))


def controls():
    return call('ui.tree', dict(limit=3000))['controls']


def control(suffix):
    return next(c for c in controls() if c['path'].endswith('/' + suffix))


def click(suffix):
    step('click', at=control(suffix)['path'])


def command(text):
    step('key', key='Ctrl+Shift+P')
    step('type', text=text)
    step('key', key='Enter')


def select(actor):
    call('apply', dict(ops=[dict(op='select', ids=[actor])]))
    step('wait', frames=4)


def props(actor):
    return call('query', dict(q='get', ids=[actor], fields=['props']))['items'][0]['props']


def number(name, value):
    click(name)
    step('key', key='Ctrl+A')
    step('type', text=str(value))
    step('key', key='Enter')


def shot(name):
    state = call('ui.tree', dict(limit=3000))
    for c in state['controls']:
        if 'NewMissionZone' in c['path'] and (c['type'] == 'PanelContainer' or c.get('text') in ['Create zone', 'Cancel']):
            x, y, w, h = c['rect']
            assert x >= 0 and y >= 0 and x + w <= state['canvas'][0] and y + h <= state['canvas'][1], c
    result = call('ui.shot', dict(name=name, dir=str(run / 'shots'), crop='tab:Missions'))
    findings = [f for f in result.get('lint', []) if '/Missions/' in f['path'] or 'NewMissionZone' in f['path']]
    assert not findings, findings
    return result


def new_objective():
    button = next(c for c in controls() if c.get('tooltip') == 'Create an objective already linked to the chosen mission')
    step('click', at=button['path'])


call('ui.tree', dict(limit=1))  # A foreground editor refuses synthetic input.
assert pathlib.Path(call('editor')['folder']).name == 'Ui.osmap', 'Use a disposable mission fixture'
assert 'bChained' not in props('Mission0'), 'Generate a fresh fixture with default chaining'
command('panel: Missions')
select('Mission0')
assert control('Chained')['pressed'], 'The checkbox must reflect default-on chaining'
assert 'only objective 1' in control('SequenceHint')['text']
before = props('Mission0')
number('MinimumObjectives', 1)
assert props('Mission0') == before, 'Chaining disables the ignored win count'
shot('default-chained-compact')

# Give the tree room to show both objective rows, just as dragging the dock divider does.
dock = next(c for c in controls() if c['path'].endswith('/BottomDock'))
x, y, w, h = dock['rect']
step('drag', at=[x + w / 2, y - 2], to=[x + w / 2, 355])
click('Chained')
assert props('Mission0')['bChained'] is False
assert 'all objectives start available together' in control('SequenceHint')['text']
call('undo')
step('wait', frames=4)
assert props('Mission0') == before and control('Chained')['pressed'], 'One undo restores the inherited value and checkbox'
call('redo')
step('wait', frames=4)
assert control('Chained')['pressed'] is False
number('MinimumObjectives', 1)
number('RandomObjectives', 1)
assert props('Mission0')['MinimumObjectives'] == 1 and props('Mission0')['RandomObjectives'] == 1
assert 'random selection starts available together' in control('SequenceHint')['text']
shot('unchained-random-and-win-count')
click('Chained')
assert props('Mission0')['MinimumObjectives'] == 1 and props('Mission0')['RandomObjectives'] == 1, 'Chaining preserves the counts for later'
assert 'All are required' in control('SequenceHint')['text']
shot('chained-counts-disabled')
call('undo')
call('undo')
call('undo')
call('undo')
step('wait', frames=4)
assert props('Mission0') == before, 'Undo restores all mission settings without touching links'

select('Objective0')
objective_before = props('Objective0')
assert control('ShowOnHud')['pressed'] and control('AwardScore')['pressed'], 'Objective flags use inherited defaults'
click('ShowOnHud')
click('AwardScore')
assert props('Objective0')['bShowStatusOnHud'] is False and props('Objective0')['bScore'] is False
select('Objective1')
assert control('ShowOnHud')['pressed'] and control('AwardScore')['pressed'], 'Switching objectives follows that objective only'
select('Objective0')
assert not control('ShowOnHud')['pressed'] and not control('AwardScore')['pressed']
call('undo')
call('undo')
step('wait', frames=4)
assert props('Objective0') == objective_before and control('ShowOnHud')['pressed'] and control('AwardScore')['pressed']
shot('objective-flags-restored')

select('Mission0')
click('GameMode')
step('click', at='item:Disk Hunt')
assert props('Mission0')['GameMode'] == 4
assert control('Chained').get('disabled') and 'Disk Hunt' in control('SequenceHint')['text']
call('undo')
step('wait', frames=4)
assert props('Mission0') == before and not control('Chained').get('disabled')

step('click', at='text:New mission')
new_id = call('editor')['selection'][0]
assert new_id != 'Mission0' and control('Chained')['pressed'], 'New missions visibly start chained'
call('undo')
step('wait', frames=4)
select('Mission0')
assert props('Mission0') == before

# Build two zones using only their visible controls, including parent navigation and cancellation.
step('click', at='text:New mission')
story = call('editor')['selection'][0]
step('click', at='text:New zone…')
assert control('SequentialZones')['pressed']
number('ZoneMinimum', 2)
click('ZoneName')
step('key', key='Ctrl+A')
step('type', text='Archives')
shot('create-named-zone')
step('click', at='text:Create zone')
zone_a = call('editor')['selection'][0]
assert props(zone_a)['ObjectiveName'] == 'Archives'
assert props(zone_a)['GameMode'] == 0 and props(zone_a)['bChained'] is False and props(zone_a)['MinimumObjectives'] == 2
assert control('Chained')['pressed'] is False and 'Inside New mission' in control('SequenceHint')['text']
for _ in range(3):
    new_objective()
assert len(props(zone_a)['Objectives']) == 3
step('click', at='text:Parent mission')
assert call('editor')['selection'] == [story] and 'Zones unlock in tree order' in control('SequenceHint')['text']
assert control('Chained')['pressed']
step('click', at='text:New zone…')
number('ZoneMinimum', 1)
step('click', at='text:Create zone')
zone_b = call('editor')['selection'][0]
assert zone_a != zone_b and props(zone_b)['GameMode'] == 0 and props(zone_b)['MinimumObjectives'] == 1
new_objective()
step('click', at='text:Parent mission')
links = props(story)['Objectives']
assert [x['$actor'] for x in links] == [zone_a, zone_b], 'The parent chains named zones in their created order'
shot('nested-zones-and-objectives')
click('Chained')
assert 'Zones are available together' in control('SequenceHint')['text']
call('undo')
step('wait', frames=4)
select(zone_a)
number('MinimumObjectives', 1)
assert props(zone_a)['MinimumObjectives'] == 1
call('undo')
step('wait', frames=4)
assert props(zone_a)['MinimumObjectives'] == 2 and control('MinimumObjectives')['text'] == '2'
step('click', at='text:Parent mission')
step('click', at='text:New zone…')
step('click', at='text:Cancel')
assert props(story)['Objectives'] == links, 'Cancelling a zone creates no actor or link'
assert props('Mission0') == before, 'Zone authoring preserves unrelated missions'
call('save')
print('PASS mission sequencing, defaults, counts, flags, modes, nested zone creation, parent navigation, cancellation, undo/redo, and layout', flush=True)
