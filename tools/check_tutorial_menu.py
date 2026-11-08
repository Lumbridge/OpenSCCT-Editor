"""Check the bounded Tutorials submenu and reach its last lesson at 100% and 150% UI scale.

Usage: python -B tools/check_tutorial_menu.py <disposable-background-run-folder>
"""
import json,pathlib,re,socket,sys,time

catalog = pathlib.Path(__file__).resolve().parents[1] / 'app/Scripts/Shell/Discovery'
expected_lessons = sum(len(re.findall(r'new\("[^"]+",', source.read_text())) for source in catalog.glob('TutorialCatalog*.cs'))

run=pathlib.Path(sys.argv[1]).resolve()
deadline=time.monotonic()+45
while not (endpoints:=list((run/'live').glob('editor-*.json'))):
    if time.monotonic()>deadline: raise RuntimeError('No disposable editor endpoint')
    time.sleep(.1)
meta=json.loads(endpoints[0].read_text())
sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=90);stream=sock.makefile('rwb');seq=0
stream.write((json.dumps(dict(auth=meta['token'],client='tutorial-menu-check'))+'\n').encode());stream.flush()
assert json.loads(stream.readline())['ok']
def call(method,params=None):
    global seq
    seq+=1;stream.write((json.dumps(dict(id=seq,method=method,params=params or {}))+'\n').encode());stream.flush()
    r=json.loads(stream.readline());assert 'error' not in r,r;return r['result']
def step(op,frames=10,**kw):return call('ui.input',dict(steps=[dict(op=op,frames=frames,**kw)]))
def nav(text):step('key',key='Ctrl+Shift+P');step('type',text=text);step('key',key='Enter')
def tree():return call('ui.tree',dict(limit=5000))
def shot(name):return call('ui.shot',dict(name=name,dir=str(run/'shots')))
call('ui.tree',dict(limit=1))
before=call('summary')['counts'];dirty=call('editor')['dirty']
for percent in [100,150]:
    nav('commands: '+str(percent)+'%')
    step('click',at='menu:Help');step('hover',at='item:Tutorials',seconds=.75)
    state=tree();popup=next(p for p in state['popups'] if p['path'].endswith('/Tutorials'))
    items=[i for i in popup['items'] if i['text']!='---']
    assert len(items)==expected_lessons,('All built-in lessons must remain in the submenu',len(items),expected_lessons)
    x,y,w,h=popup['rect']
    assert h<=480*state['scale']+2 and h<state['window'][1],('Tutorial menu exceeds height limit',popup['rect'],state['window'])
    shot('tutorial-menu-'+str(percent)+'-top')
    step('move',at=[x+w*.5,y+h*.5]);step('scroll',at=[x+w*.5,y+h*.5],clicks=-50)
    shot('tutorial-menu-'+str(percent)+'-bottom')
    step('click',at=[x+w*.5,y+h-18])
    assert any(c.get('tutorial')=='storeys' for c in tree()['controls']),'Last tutorial must remain reachable'
    step('click',at='text:Cancel')
    print(f'PASS {percent}% Tutorials menu: {len(items)} lessons, bounded height {h}, wheel scrolling and last-item activation',flush=True)
nav('commands: 100%')
assert call('summary')['counts']==before and call('editor')['dirty']==dirty,'Browsing tutorials must leave the map unchanged'
print('PASS scrolling tutorial submenu checks',flush=True)
