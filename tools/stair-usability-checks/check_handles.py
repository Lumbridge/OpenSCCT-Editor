"""Check real toolbar, dimension snapping and repeated scale events in a disposable background Editor.
Start it with this suite's Handles.osmap and pass its dedicated run folder. No map is saved.
"""
import json, math, pathlib, socket, sys

run = pathlib.Path(sys.argv[1]).resolve()
files = list((run / "live").glob("editor-*.json"))
assert len(files) == 1, "Use a dedicated background run folder"
meta = json.loads(files[0].read_text())
sock = socket.create_connection(("127.0.0.1", meta["port"]), timeout=60)
stream = sock.makefile("rwb")
stream.write((json.dumps(dict(auth=meta["token"], client="stair-handle-regression")) + "\n").encode()); stream.flush()
assert json.loads(stream.readline())["ok"]
seq = 0

def call(method, params=None, error=False):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {})) + "\n").encode()); stream.flush()
    reply = json.loads(stream.readline())
    assert ("error" in reply) == error, reply
    return reply.get("result", reply)

def step(op, **args): return call("ui.input", dict(steps=[dict(op=op, frames=3, **args)]))
def tree(): return call("ui.tree", dict(limit=1200))
def design(): return call("query", dict(q="get", id="Stair", fields=["stair"]))["items"][0]["stair"]
def toggle(action, wanted):
    button = next(c for c in tree()["controls"] if c.get("action") == action)
    if button.get("pressed", False) != wanted: step("click", at="action:" + action)

def project(point):
    camera = call("editor")["camera"]
    p, y, _ = [v * math.tau / 65536 for v in camera["rot"]]
    forward = (math.cos(p)*math.cos(y), math.cos(p)*math.sin(y), math.sin(p))
    right = (-math.sin(y), math.cos(y), 0)
    up = (-math.sin(p)*math.cos(y), -math.sin(p)*math.sin(y), math.cos(p))
    relative = [a-b for a,b in zip(point, camera["loc"])]
    dot = lambda a,b: sum(x*y for x,y in zip(a,b))
    x,y,w,h = next(c["rect"] for c in tree()["controls"] if c["path"].endswith("/Perspective"))
    depth = dot(relative, forward)
    return [x+w/2+dot(relative,right)*w/(2*depth), y+h/2-dot(relative,up)*w/(2*depth)]

call("ui.tree", dict(limit=1))  # Interactive editors refuse synthetic input.
assert call("editor")["map"] == "StairFit", "Open the disposable Handles.osmap"
call("apply", dict(ops=[dict(op="select", ids=["Stair"])]))
for action, panel in [("tools.stairs", "Stairs"), ("tools.windows", "Windows")]:
    step("click", at="action:" + action)
    assert any(c.get("currentTab") == panel for c in tree()["controls"]), "Toolbar must open " + panel
call("ui.input", dict(steps=[dict(op="click", at="menu:Window")]), error=True)
step("click", at="action:tools.scale")
step("click", at="tab:Inspector")
toggle("view.grid.snap", True); toggle("view.neighbourSnap", False)

before = design()
step("press", at=project([529,32,256]))
for height in (272,288,304):
    step("move", at=project([529,32,height]))
    assert abs(design()["rise"]-height) < .001, "Rise handle must follow the grid without compounding"
step("release", at=project([529,32,304]))
call("undo"); assert design()["rise"] == 256, "A handle drag is one undo step"
call("redo"); assert design()["rise"] == 304

toggle("view.neighbourSnap", True)
step("press", at=project([529,32,304]))
step("move", at=project([529,32,292]))
assert abs(design()["rise"]-300) < .001, "Neighbour snap must actually place the stair top on the 300-unit ceiling"
call("ui.shot", dict(name="rise-snapped-to-ceiling", dir=str(run)))
step("release", at=project([529,32,292]))
toggle("view.neighbourSnap", False); toggle("view.grid.snap", False)

# The ordinary scale gizmo must also use its original snapshot at every mouse event.
origin = project([17,32,0]); at100 = project([17,32,100])
unit = [a-b for a,b in zip(at100,origin)]; length = math.hypot(*unit); unit = [v/length for v in unit]
state = call("editor")["camera"]; pitch,yaw,_ = [v*math.tau/65536 for v in state["rot"]]
fwd = [math.cos(pitch)*math.cos(yaw), math.cos(pitch)*math.sin(yaw), math.sin(pitch)]
depth = sum((a-b)*c for a,b,c in zip([17,32,0],state["loc"],fwd))
width = next(c["rect"][2] for c in tree()["controls"] if c["path"].endswith("/Perspective"))
end = project([17,32,90*2*depth/width])
screen_length = math.dist(end,origin)
step("press", at=end)
for factor in (1.05,1.1,1.15):
    point = [a+b*screen_length*(factor-1) for a,b in zip(end,unit)]
    step("move", at=point)
    assert abs(design()["rise"]-300*factor) < .1, ("Scale compounding",factor,design()["rise"])
step("release", at=point)
call("undo"); assert abs(design()["rise"]-300) < .001
call("redo"); assert abs(design()["rise"]-345) < .1
toggle("view.grid.snap", True)
step("press", at=project([529,32,569]))
step("move", at=project([529,32,585]))
step("release", at=project([529,32,585]))
assert abs(345 + design()["headroom"] - 592) < .001, "Headroom ceiling handle snaps in world space"
end_at = project([577,32,592]); beyond = project([677,32,592]); length = math.dist(end_at,beyond)
leader = [(b-a)*24/length for a,b in zip(end_at,beyond)]
end_target = [a+b for a,b in zip(project([593,32,592]),leader)]
step("press", at=[a+b for a,b in zip(end_at,leader)])
step("move", at=end_target)
step("release", at=end_target)
assert abs(529 + design()["clearanceEnd"] - 592) < .001, "Clearance end has its own draggable handle"
step("press", at=project([17,128,0]))
step("move", at=project([17,144,0]))
step("release", at=project([17,144,0]))
assert abs(design()["width"] - 224) < .001, "Width handle snaps the physical edge to the grid"
toggle("view.grid.snap", False); toggle("view.neighbourSnap", True)
step("press", at=end)
point = [a+b*screen_length*(-.1) for a,b in zip(end,unit)]
step("move", at=point)
assert abs(design()["rise"]-300) < .01, "The ordinary gizmo's snap indicator matches the actual stair top"
call("ui.shot", dict(name="gizmo-snapped-to-ceiling", dir=str(run)))
step("release", at=point)
toggle("view.neighbourSnap", False); toggle("view.grid.snap", True)
step("press", at=end)
point = [a+b*screen_length*.07 for a,b in zip(end,unit)]
step("move", at=point)
step("release", at=point)
assert abs(design()["rise"]-320) < .01, "The scale gizmo snaps actual dimensions to the world grid"
call("ui.shot", dict(name="scaled-stair-and-clearance", dir=str(run)))
print("PASS toolbar access, removed Window menu, grid handle drag, exact neighbour alignment, stable repeated scale events, clearance handles and undo/redo")
