"""Verify viewport capture through the real editor endpoint without changing the map or UI.

Pass an isolated run folder (with live/) by default. --live accepts the discovery directory of an already open
interactive editor, and verifies that input/UI inspection stay unavailable. Only empty input is ever submitted.
The caller owns starting/stopping the editor. Python standard library only.
"""
import argparse,json,pathlib,socket,struct,time

parser=argparse.ArgumentParser()
parser.add_argument('folder',type=pathlib.Path)
parser.add_argument('--live',action='store_true')
parser.add_argument('--out',type=pathlib.Path)
args=parser.parse_args()
discovery=args.folder if args.live else args.folder/'live'
deadline=time.monotonic()+30
while not (files:=list(discovery.glob('editor-*.json'))):
 if time.monotonic()>deadline: raise RuntimeError('no live editor endpoint')
 time.sleep(.1)
assert len(files)==1,'use a directory identifying exactly one editor'
meta=json.loads(files[0].read_text())
sock=socket.create_connection(('127.0.0.1',meta['port']),timeout=15)
stream=sock.makefile('rwb')
stream.write((json.dumps({'auth':meta['token'],'client':'viewport-capture-check'})+'\n').encode());stream.flush()
assert 'error' not in json.loads(stream.readline())
seq=0
def call(method,params={},error=False):
 global seq
 seq+=1;stream.write((json.dumps(dict(id=seq,method=method,params=params))+'\n').encode());stream.flush()
 reply=json.loads(stream.readline())
 assert ('error' in reply)==error,reply
 return reply.get('error') if error else reply['result']

call('ui.tree',dict(limit=1),error=args.live)
if args.live:
 call('ui.input',dict(steps=[]),error=True)
 call('ui.shot',dict(views=False),error=True)
 print('PASS live UI inspection/input and full-window capture remain unavailable',flush=True)
out=(args.out or args.folder/'capture-check').resolve()
name='viewports-'+str(time.time_ns())
before=call('editor')
capture=call('ui.shot',dict(name=name,dir=str(out),views=True,viewportsOnly=True,lint=True))
after=call('editor')
for key in ['map','folder','dirty','undo','camera','selection','selectionCount']:
 assert before.get(key)==after.get(key),(key,before.get(key),after.get(key))
assert capture['scope']=='viewports' and capture['map']==before['map'] and capture['views'],capture
assert 'lint' not in capture and not (out/(name+'.png')).exists()
assert pathlib.Path(capture['path']).is_file()
for view in capture['views']:
 path=pathlib.Path(view['raw']);data=path.read_bytes()
 assert data[:8]==b'\x89PNG\r\n\x1a\n'
 assert struct.unpack('>II',data[16:24])==(view['width'],view['height'])
 assert view['width']>0 and view['height']>0 and 'camera' in view
assert len(list(out.glob(name+'*.png')))==len(capture['views'])
call('ui.shot',dict(views=True,viewportsOnly=True,crop='view:Perspective',dir=str(out)),error=True)
print('PASS viewport PNGs and camera metadata; no full-window image, lint or map/UI changes',flush=True)
if not args.live:
 full=call('ui.shot',dict(name=name+'-full',dir=str(out),lint=False))
 assert pathlib.Path(full['path']).is_file() and 'size' in full
 print('PASS background full-window screenshots remain available',flush=True)
print(json.dumps(capture),flush=True)
