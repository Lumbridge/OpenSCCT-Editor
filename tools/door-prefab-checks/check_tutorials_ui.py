"""Check the actual searchable lessons, prefab destinations and tutorial controls in a disposable editor."""
import json, pathlib, re, socket, sys, time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 45
while not (files := list((run / 'live').glob('editor-*.json'))):
    if time.monotonic() > deadline: raise RuntimeError('No disposable editor endpoint')
    time.sleep(.1)
meta = json.loads(files[0].read_text())
sock = socket.create_connection(('127.0.0.1', meta['port']), timeout=90)
stream = sock.makefile('rwb')
stream.write((json.dumps(dict(auth=meta['token'], client='door-tutorial-check'))+'\n').encode()); stream.flush()
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
def nav(text):
    step('key', key='Ctrl+Shift+P'); step('type', text=text); step('key', key='Enter')
def shot(name): return call('ui.shot', dict(name=name, dir=str(run/'shots')))

lessons = [
    ('Build a hinged door', 'door-hinged', 5),
    ('Build a sliding door', 'door-sliding', 5),
    ('Build a rising security door', 'door-rising', 5),
    ('Build double hinged doors', 'door-double-hinged', 5),
    ('Build double sliding doors', 'door-double-sliding', 5),
    ('Choose door activation and timing', 'door-activation', 6),
    ('Build a hackable door and move its panel', 'door-hacking', 6),
    ('Build doors with automatic team access', 'door-automatic', 6),
    ('Paint door leaves and frames', 'door-materials', 4),
]
destinations = {
    'door-rising': {0: ('Raised security door', None), 1: ('Raised security door', 'Height')},
    'door-double-hinged': {0: ('Double hinged doors', None), 1: ('Double hinged doors', 'Width'), 2: ('Double hinged doors', 'Front access')},
    'door-double-sliding': {0: ('Double sliding doors', None), 1: ('Double sliding doors', 'Width'), 2: ('Double sliding doors', 'Back access')},
    'door-activation': {2: ('Door with a switch', 'State')},
    'door-hacking': {0: ('Hackable access door', None), 1: ('Hackable access door', 'Hack time'), 2: ('Hackable access door', 'Panel height')},
    'door-automatic': {0: ('Automatic access door', None), 1: ('Automatic access door', 'Front access'), 2: ('Automatic access door', 'Back access'), 3: ('Automatic access door', 'Approach'), 4: ('Automatic access door', 'Hack panel')},
}

call('ui.tree', dict(limit=1))  # UI driving must be restricted to this disposable background session.
nav('commands: Full Workspace')
if len(sys.argv) > 2: call('open', dict(path=str(pathlib.Path(sys.argv[2]).resolve())))
before = call('summary')['counts']
dirty = call('editor')['dirty']
checked = 0
for title, lesson, count in lessons:
    nav('tutorials: '+title)
    for index in range(count):
        controls = tree()
        assert any(c.get('tutorial') == lesson and c.get('tutorialStep') == index for c in controls), (lesson, index, 'wrong step')
        highlights = [c for c in controls if c.get('highlightTarget')]
        assert highlights, (lesson, index, 'missing highlight')
        highlighted_paths = [c['highlightTarget'].removeprefix('/root/') for c in highlights]
        if destination := destinations.get(lesson, {}).get(index):
            prefab, field = destination
            assert any(c.get('text') == prefab and '/Prefabs/' in c['path'] for c in controls), (lesson, index, 'wrong prefab')
            if field:
                caption = next(c for c in controls if c.get('text', '').casefold() == field.casefold() and '/Prefabs/' in c['path'])
                row = caption['path'].rsplit('/', 1)[0]
                assert any(path.startswith(row+'/') for path in highlighted_paths), (lesson, index, field, 'wrong highlighted control')
        if lesson == 'door-sliding' and index == 0:
            # Perform the lesson's UI-only setting change so the conditional Travel field exists on the next step.
            caption = next(c for c in controls if c.get('text') == 'Movement' and '/Doors/' in c['path'])
            row = caption['path'].rsplit('/', 1)[0]
            pick = next(c for c in controls if c['type'] == 'OptionButton' and c['path'].startswith(row+'/'))
            step('click', at=pick['path']); step('click', at='item:Slide Left')
        if index == 1 and lesson == 'door-sliding':
            caption = next(c for c in controls if c.get('text') == 'Travel' and '/Doors/' in c['path'])
            assert any(path.startswith(caption['path'].rsplit('/',1)[0]+'/') for path in highlighted_paths)
        if lesson == 'door-hacking' and index == 3 and len(sys.argv) > 2:
            panel = call('query', dict(q='find', **{'class':'SBase.SInterrupteur'}, fields=['id']))['items'][0]
            call('apply', dict(ops=[dict(op='select', id=panel['id'])]))
        if lesson == 'door-hacking' and index == 4 and len(sys.argv) > 2:
            assert any(c.get('text') == 'Select linked doors' for c in controls), 'Hacking lesson exposes the real linked panel controls'
            assert any('Inspector' in path and not path.endswith('/Inspector') for path in highlighted_paths)
        if index+1 < count: step('click', at='text:Next')
        checked += 1
    shot('tutorial-'+lesson)
    step('click', at='text:Finish')
    assert not any(c.get('tutorial') == lesson for c in tree()), 'Finish closes the current lesson'
    print(f'PASS {lesson}: {count} live destinations and Finish', flush=True)

# Find the new lessons alongside unrelated existing lessons through the standard tutorial browser.
nav('tutorials: Set up a ladder')
assert any(c.get('tutorial') == 'ladder' for c in tree())
step('click', at='text:Cancel')
nav('panels: Doors')
scroll = next(c for c in tree() if c['type'] == 'ScrollContainer' and '/Doors/Doors/' in c['path'])
step('scroll', at=scroll['path'], clicks=-10)
step('click', at='text:Browse door tutorials')
controls = tree()
query = next(c for c in controls if c['type'] == 'LineEdit' and c.get('text') == 'tutorials: door')
assert query
step('key', key='Escape')
nav('panels: Doors')
scroll = next(c for c in tree() if c['type'] == 'ScrollContainer' and '/Doors/Doors/' in c['path'])
step('scroll', at=scroll['path'], clicks=40); step('click', at='text:Hinged pair')
details = next(c for c in tree() if c['type'] == 'ScrollContainer' and '/Prefabs/Prefabs/' in c['path'])
step('scroll', at=details['path'], clicks=40); step('click', at='text:Learn this door')
assert any(c.get('tutorial') == 'door-double-hinged' and c.get('tutorialStep') == 0 for c in tree())
step('click', at='text:Next'); step('click', at='text:Next'); step('click', at='text:Back')
assert any(c.get('tutorial') == 'door-double-hinged' and c.get('tutorialStep') == 1 for c in tree())
step('click', at='text:Cancel'); nav('commands: Resume Last Tutorial')
assert any(c.get('tutorial') == 'door-double-hinged' and c.get('tutorialStep') == 1 for c in tree())
step('click', at='text:Show me again')
assert any(c.get('highlightTarget') for c in tree())
shot('tutorial-double-hinged-resumed'); step('click', at='text:Cancel')
assert call('summary')['counts'] == before and call('editor')['dirty'] == dirty, 'Lessons must not change the map'
print(f'PASS {checked} new door tutorial destinations; shared tutorial browser, contextual entry, Back, Cancel, Resume and Show me again; map unchanged', flush=True)
