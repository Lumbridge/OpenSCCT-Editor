"""Place the real stair ghost beside a wall, comparing neighbour snapping on and off."""
from pathlib import Path

helper = Path(__file__).with_name("check_handles.py").read_text()
exec(helper[:helper.index('call("ui.tree", dict(limit=1))')])

def stairs():
    result = call("query", dict(q="find", kind="stair", fields=["id", "loc"], rows=True))
    return {row[0]: row[1] for row in result["rows"]}

toggle("view.grid.snap", False)
toggle("view.neighbourSnap", True)
step("click", at="action:tools.stairs")
before = stairs()
pointer = project([0, 1431, 0])
step("move", at=pointer)
call("ui.shot", dict(name="stair-ghost-neighbour-snap", dir=str(run)))
step("click", at=pointer)
after = stairs()
added = set(after) - set(before)
assert len(added) == 1, ("The snapped preview must place one staircase", after)
snapped = after[added.pop()]
assert abs(snapped[1] - 1440) < .01, ("The side of the 192-unit ghost must meet the wall at Y=1536", snapped)
call("undo")
toggle("view.neighbourSnap", False)
step("move", at=pointer)
step("click", at=pointer)
after = stairs()
added = set(after) - set(before)
assert len(added) == 1, ("The unsnapped preview must place one staircase", after)
unsnapped = after[added.pop()]
assert abs(unsnapped[1] - 1431) < 1, ("Disabling snapping preserves the pointer's floor position", unsnapped)
call("undo")

# Drag the first palette preset through Godot's real drag/drop path.
toggle("view.neighbourSnap", True)
step("click", at="action:tools.select")
step("click", at="tab:Stairs")
palette = next(c for c in tree()["controls"] if c["type"] == "ItemList" and "/Stairs/" in c["path"])
x, y, _, _ = palette["rect"]
start = [x+55,y+45]
step("press", at=start)
step("move", at=[start[0]+20,start[1]+15])
step("move", at=pointer)
call("ui.shot", dict(name="palette-stair-ghost-snap", dir=str(run)))
step("release", at=pointer)
after = stairs()
added = set(after)-set(before)
assert len(added) == 1, ("Palette drop places one snapped staircase", after)
assert abs(after[added.pop()][1]-1440) < .01, ("Palette ghost uses the same wall snapping", after)
call("undo")
print("PASS click and palette stair ghosts, exact neighbour placement and snapping disabled")
