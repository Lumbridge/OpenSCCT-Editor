"""Drive only a disposable background editor opened on this suite's Handles.osmap."""
import json, math, pathlib, socket, sys, time

run = pathlib.Path(sys.argv[1]).resolve()
deadline = time.monotonic() + 30
while not list((run / "live").glob("editor-*.json")) and time.monotonic() < deadline: time.sleep(.1)
meta = json.loads(next((run / "live").glob("editor-*.json")).read_text())
sock = socket.create_connection(("127.0.0.1", meta["port"]), timeout=60)
stream = sock.makefile("rwb")
stream.write((json.dumps(dict(auth=meta["token"], client="ge-snap-regression")) + "\n").encode()); stream.flush()
assert json.loads(stream.readline())["ok"]
seq = 0

def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {})) + "\n").encode()); stream.flush()
    reply = json.loads(stream.readline())
    assert "error" not in reply, reply
    return reply["result"]

def step(op, **args): return call("ui.input", dict(steps=[dict(op=op, frames=4, **args)]))
def tree(): return call("ui.tree", dict(limit=1200))
def points(): return call("query", dict(q="get", id="Ladder", fields=["points"]))["items"][0]["points"]
def toggle(action, wanted):
    button = next(c for c in tree()["controls"] if c.get("action") == action)
    if button.get("pressed", False) != wanted: step("click", at="action:" + action)

def project(point):
    camera = call("editor")["camera"]
    p, y, _ = [v * math.tau / 65536 for v in camera["rot"]]
    forward = (math.cos(p)*math.cos(y), math.cos(p)*math.sin(y), math.sin(p))
    right = (-math.sin(y), math.cos(y), 0)
    up = (-math.sin(p)*math.cos(y), -math.sin(p)*math.sin(y), math.cos(p))
    rel = [a-b for a, b in zip(point, camera["loc"])]
    dot = lambda a, b: sum(x*y for x, y in zip(a, b))
    x, y, w, h = next(c["rect"] for c in tree()["controls"] if c["path"].endswith("/Perspective"))
    depth = dot(rel, forward)
    return [x+w/2+dot(rel, right)*w/(2*depth), y+h/2-dot(rel, up)*w/(2*depth)]

call("ui.tree", dict(limit=1))  # A normal interactive editor refuses this call.
assert call("editor")["map"] == "GeSnap"
if len(sys.argv) > 2 and sys.argv[2] == "inspect":
    print(json.dumps(tree(), indent=2)); print(call("editor")); sys.exit()

call("apply", dict(ops=[dict(op="select", ids=["Ladder"])]))
if any(c.get("action") == "workspace.expand" for c in tree()["controls"]): step("click", at="action:workspace.expand")
step("click", at="action:tools.ge")
toggle("view.grid.snap", True); toggle("view.neighbourSnap", True)
before = points()
step("press", at=project(before[1]))
step("move", at=project([0, 0, 295]))
print(call("ui.shot", dict(name="endpoint-neighbour-snap", dir=str(run))))
step("release", at=project([0, 0, 295]))
assert abs(points()[1][2] - 300) < .01, ("Top dot must stick exactly to ceiling, ahead of grid", points())
call("undo"); assert points() == before, "Endpoint drag is one undo step"
call("redo"); assert abs(points()[1][2] - 300) < .01
call("undo")

# Alt can turn snapping off and on at the same cursor position.
step("press", at=project(before[1])); step("move", at=project([0, 0, 295]))
step("hold", key="Alt", down=True); step("release", at=project([0, 0, 295])); step("hold", key="Alt", down=False)
assert points()[1][2] != 300, "Holding Alt disables neighbour snapping"
call("undo")
step("press", at=project(before[1])); step("move", at=project([0, 0, 295]))
step("hold", key="Alt", down=True)
step("hold", key="Alt", down=False)
step("release", at=project([0, 0, 295]))
assert abs(points()[1][2] - 300) < .01, "Releasing Alt re-solves the endpoint snap"
call("undo")

# Escape discards the draft without touching the map or its history.
step("press", at=project(before[1])); step("move", at=project([0, 0, 295])); step("key", key="Escape")
step("release", at=project([0, 0, 295]))
assert points() == before, "Cancel must preserve all original points"

toggle("view.neighbourSnap", False)
step("press", at=project(before[1])); step("move", at=project([0, 0, 291])); step("release", at=project([0, 0, 291]))
assert abs(points()[1][2] / 16 - round(points()[1][2] / 16)) < .001, ("Endpoint falls back to grid", points())
assert points()[1][2] != 300
call("undo")

# View from above so the ladder's facing dot is clear of its foot and top.
call("apply", dict(ops=[dict(op="workspace", camera=dict(at=[0, 0, 80], rot=[-16384, 0, 0]))]))
toggle("view.rotsnap", True)
step("click", at="menu:View"); step("hover", at="item:Rotation Snap Size"); step("click", at="item:45° (8192)")
before = points()
facing = before[2]
step("press", at=project(facing))
angle = math.radians(43)
target = project([math.cos(angle)*10, math.sin(angle)*10, 0])
step("move", at=target); step("release", at=target)
after = points(); angle = math.degrees(math.atan2(after[2][1]-after[0][1], after[2][0]-after[0][0]))
assert abs(angle-45) < .01, ("Facing dot must use the rotation grid", angle, after)
assert after[:2] == before[:2], "Facing rotation preserves the ladder endpoints"
call("undo"); assert points() == before

toggle("view.rotsnap", False); toggle("view.neighbourSnap", True)
step("press", at=project(before[2]))
angle = math.radians(28); target = project([math.cos(angle)*10, math.sin(angle)*10, 0])
step("move", at=target); step("release", at=target)
after = points(); angle = math.degrees(math.atan2(after[2][1]-after[0][1], after[2][0]-after[0][0]))
assert abs(angle-30) < .02, ("Facing dot lines up with neighbouring wall", angle)
call("undo")

call("apply", dict(ops=[dict(op="workspace", camera=dict(at=[-400, -600, 250], rot=[63788, 10251, 0]))]))
pivot = [5, 0, 128]
def gizmo_tip():
    camera = call("editor")["camera"]; p, y, _ = [v*math.tau/65536 for v in camera["rot"]]
    forward = [math.cos(p)*math.cos(y), math.cos(p)*math.sin(y), math.sin(p)]
    depth = sum((a-b)*c for a, b, c in zip(pivot, camera["loc"], forward))
    width = next(c["rect"][2] for c in tree()["controls"] if c["path"].endswith("/Perspective"))
    return [pivot[0], pivot[1], pivot[2]+90*2*depth/width]

step("click", at="action:tools.move"); before = points()
tip = gizmo_tip(); target = project([tip[0], tip[1], tip[2]+40])
step("press", at=project(tip)); step("move", at=target); step("release", at=target)
assert abs(points()[1][2]-300) < .01, ("GE-only Move gizmo uses neighbour snap", points())
call("undo"); assert points() == before

step("click", at="action:tools.scale")
tip = gizmo_tip(); origin = project(pivot); start = project(tip)
target = [a+(a-b)*.31 for a, b in zip(start, origin)]
step("press", at=start); step("move", at=target); step("release", at=target)
assert abs(points()[1][2]-300) < .01, ("GE-only Scale gizmo uses neighbour snap", points())
call("undo"); assert points() == before
# The ordinary rotation ring uses GE directions rather than a fictitious zero actor rotation.
call("apply", dict(ops=[dict(op="workspace", camera=dict(at=[5, 0, 500], rot=[-16384, 0, 0]))]))
step("click", at="action:tools.rotate")
width = next(c["rect"][2] for c in tree()["controls"] if c["path"].endswith("/Perspective"))
radius = 80 * 2 * (500-pivot[2]) / width
angle = math.radians(25)
start = project([pivot[0]+radius*math.cos(angle), pivot[1]+radius*math.sin(angle), pivot[2]])
mid = (math.floor(25/(360/64))+.5)*math.tau/64
travel = (28/360*65536) / ((65536/64)/(2*80*math.sin(math.pi/64)))
target = [start[0]+math.cos(mid)*travel, start[1]+math.sin(mid)*travel]
step("press", at=start); step("move", at=target); step("release", at=target)
after = points(); angle = math.degrees(math.atan2(after[2][1]-after[0][1], after[2][0]-after[0][0]))
assert abs(angle-30) < .02, ("GE-only Rotate gizmo uses neighbour directions", angle, after)
call("undo"); assert points() == before
print("PASS endpoint neighbour/grid snap, Alt, cancel, undo/redo, facing rotation grid/neighbour and GE-only Move/Rotate/Scale gizmos")
