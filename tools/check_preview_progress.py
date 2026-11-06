"""Check real preview readiness, cancellation and pause/resume in an isolated background editor.

The caller starts/stops the editor and supplies a light actor in its test map. Edits are undone, never saved.
Requires the live editor API; uses no real pointer/keyboard. Python standard library only.
"""
import argparse, json, pathlib, socket, time

parser=argparse.ArgumentParser()
parser.add_argument('run',type=pathlib.Path)
parser.add_argument('--light',required=True)
args=parser.parse_args(); run=args.run.resolve()
deadline=time.monotonic()+60
while not (files:=list((run/'live').glob('editor-*.json'))):
 if time.monotonic()>deadline: raise RuntimeError('No background editor endpoint')
 time.sleep(.1)
assert len(files)==1,'Use a run folder containing exactly one editor endpoint'
meta=json.loads(files[0].read_text())
sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=120); stream=sock.makefile('rwb')
stream.write((json.dumps({'auth':meta['token'],'client':'preview-progress-check'})+'\n').encode()); stream.flush()
assert 'error' not in json.loads(stream.readline())
seq=0; observations=[]
def call(method,params={}):
 global seq
 seq+=1; stream.write((json.dumps(dict(id=seq,method=method,params=params))+'\n').encode()); stream.flush()
 reply=json.loads(stream.readline()); assert 'error' not in reply,reply
 return reply['result']
def state():
 p=call('editor')['preview']; observations.append(dict(time=time.time(),**p))
 assert not p.get('error'),p
 return p
def wait(predicate):
 deadline=time.monotonic()+150
 while time.monotonic()<deadline:
  p=state()
  if predicate(p): return p
  time.sleep(.2)
 raise RuntimeError('Preview wait timed out: '+str(p))
def ready():
 p=wait(lambda p:p['litReady'])
 assert p['geometry']==p['sky']==p['lighting']=='ready',p
 assert p['completedLightingVersion']==p['lightingVersion'],p
 return p['lightingVersion']
def key(value): call('ui.input',dict(steps=[dict(op='key',key=value)]))
def update(brightness):
 doc=dict(label='Preview progress regression',ops=[dict(op='update',id=args.light,props={'LightBrightness':brightness})])
 call('apply',dict(**doc,dryRun=True)); call('apply',doc)
 assert not state()['litReady'],'edited scene reported ready before synchronization'
def status():
 return [c.get('text','') for c in call('ui.tree',dict(limit=700)).get('controls',[]) if c.get('path','').endswith('/Status')]

# Refused by a normal live user session: do this before any mutations or input.
call('ui.tree',dict(limit=1))
key('Ctrl+Shift+2'); key('Alt+4'); first=ready()
print('PASS initial completed preview',first,flush=True)
update(10)
cancelled=wait(lambda p:p['lighting']=='baking' and p['lightingVersion']>first)['lightingVersion']
assert any('Baking lit preview' in s for s in status()),status()
call('undo'); assert not state()['litReady']
restored=ready(); assert restored>cancelled
assert not any(p.get('completedLightingVersion')==cancelled for p in observations)
print('PASS edit invalidation, visible baking status, cancellation and undo',flush=True)

key('Alt+1'); update(20)
paused=wait(lambda p:p['lighting']=='paused' and not p['sceneSyncPending'])
time.sleep(.7); assert state()['lighting']=='paused'
call('undo'); key('Alt+4'); ready()
print('PASS hidden lit view pauses work and resumes after undo',flush=True)

before=ready(); update(30); finished=ready(); assert finished>before
call('undo'); final=ready(); assert final>finished
capture=call('ui.shot',dict(name='preview-ready',dir=str(run/'shots'),views=True,viewportsOnly=True))
(run/'preview-progress.json').write_text(json.dumps(dict(passed=True,firstVersion=first,cancelledVersion=cancelled,
 finalVersion=final,observations=observations,capture=capture),indent=2))
print('PASS complete changed bake and complete undo bake; final version',final,flush=True)
