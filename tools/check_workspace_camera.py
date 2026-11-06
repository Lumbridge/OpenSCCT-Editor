"""Live regression check for workspace.camera and ordinary viewport navigation.

Build the app, then start a fresh empty editor with tools/Start-BackgroundEditor.ps1
and a dedicated -Out folder. Pass that folder as this script's only argument.
Requires Python 3; no packages. Only an empty background editor is accepted.
The caller owns stopping the background editor after the check.
"""
import pathlib,json,socket,sys,time
run=pathlib.Path(sys.argv[1])
deadline=time.monotonic()+15
while not (files:=list((run/'live').glob('editor-*.json'))):
 if time.monotonic()>deadline:
  raise RuntimeError('background editor did not publish a live endpoint')
 time.sleep(.1)
assert len(files)==1,'use a dedicated fresh background run folder'
meta=json.loads(files[0].read_text())
sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=60)
stream=sock.makefile('rwb')
stream.write((json.dumps({'auth':meta['token'],'client':'workspace-camera-check'})+'\n').encode());stream.flush()
hello=json.loads(stream.readline())
assert 'error' not in hello,hello
seq=0
def call(method,args={}):
 global seq
 seq+=1
 stream.write((json.dumps(dict(id=seq,method=method,params=args))+'\n').encode());stream.flush()
 reply=json.loads(stream.readline())
 assert 'error' not in reply,reply
 return reply['result']
call('ui.tree',dict(limit=1))  # Reject a user's interactive editor before changing anything.
state=call('editor')
assert state.get('folder') is None and not state.get('dirty'),'start with a fresh empty background editor'
before=state['camera']
target={'loc':[120,240,360],'rot':[-2000,8000,0]}
call('apply',dict(label='Explicit camera regression',ops=[dict(op='workspace',camera=dict(at=target['loc'],rot=target['rot']))]))
after=call('editor')['camera']
print(json.dumps(dict(before=before,target=target,after=after,passed=after==target)))
call('ui.shot',dict(name='camera-applied',dir=str(run/'shots'),views=True))
if after!=target:
 sys.exit(1)
call('undo')
assert call('editor')['camera']==before,'camera undo'
call('redo')
assert call('editor')['camera']==target,'camera redo'
call('apply',dict(label='Grid does not move camera',ops=[dict(op='workspace',grid=8)]))
assert call('editor')['camera']==target,'grid preserves camera'
call('undo')
assert call('editor')['camera']==target,'grid undo preserves camera'
print('PASS camera apply, undo, redo, unrelated grid apply/undo')
call('apply',dict(label='Focus target',ops=[dict(op='brush',id='CameraTarget',at=[1500,1000,250],brush=dict(size=[128,128,128]),csg='add')]))
call('apply',dict(label='Grid before navigating',ops=[dict(op='workspace',grid=4)]))
call('ui.input',dict(steps=[dict(op='move',at='view:Perspective'),dict(op='key',key='F',frames=3)]))
navigated=call('editor')['camera']
assert navigated!=target,'F should frame the selected brush'
call('undo')
assert call('editor')['camera']==navigated,'unrelated grid undo must preserve the navigated view'
call('redo')
assert call('editor')['camera']==navigated,'unrelated grid redo must preserve the navigated view'
print('PASS ordinary framing survives unrelated workspace undo/redo')
