"""Exercise colour dragging, cancellation and undo in an isolated background Editor.

Start tools/Start-BackgroundEditor.ps1 with the window checks' WindowTest.osmap,
then pass its dedicated run folder here. Does not save or touch the real mouse.
"""
import json
import pathlib
import socket
import sys

run = pathlib.Path(sys.argv[1]).resolve()
endpoints = list((run / "live").glob("editor-*.json"))
assert len(endpoints) == 1, "Use a dedicated background run folder"
meta = json.loads(endpoints[0].read_text())
sock = socket.create_connection(("127.0.0.1", meta["port"]), timeout=60)
stream = sock.makefile("rwb")
stream.write((json.dumps({"auth": meta["token"], "client": "window-tint-check"}) + "\n").encode())
stream.flush()
assert json.loads(stream.readline())["ok"]
seq = 0


def call(method, params=None):
    global seq
    seq += 1
    stream.write((json.dumps(dict(id=seq, method=method, params=params or {})) + "\n").encode())
    stream.flush()
    reply = json.loads(stream.readline())
    assert "error" not in reply, reply
    return reply["result"]


def input_step(op, **args):
    return call("ui.input", dict(steps=[dict(op=op, frames=6, **args)]))


def tree():
    return call("ui.tree", dict(limit=2000))


def colour():
    return call("query", dict(q="get", id="Window0", fields=["window"]))["items"][0]["window"]["tint"]


def open_picker():
    swatch = next(c for c in tree()["controls"] if c["type"] == "ColorPickerButton" and "/Inspector/" in c["path"])
    input_step("click", at=swatch["path"])
    popup = next(p for p in tree()["popups"] if p["type"] == "PopupPanel")
    return popup, popup["rect"]


call("ui.tree", dict(limit=1))  # Interactive editors reject this call before any edit.
assert call("editor")["map"] == "WindowTest", "Open the disposable window test gallery"
call("apply", dict(ops=[dict(op="select", ids=["Window0"])]))
input_step("click", at="tab:Inspector")
original = colour()
popup, (x, y, w, h) = open_picker()
input_step("click", at=[x + 70, y + 70])
assert any(p["path"] == popup["path"] for p in tree()["popups"]), "First colour adjustment closed the picker"
assert colour() != original, "The window tint must preview live"
first = colour()
input_step("press", at=[x + 100, y + 90])
input_step("move", at=[x + 140, y + 110])
input_step("move", at=[x + 180, y + 130])
input_step("release", at=[x + 200, y + 160])
assert any(p["path"] == popup["path"] for p in tree()["popups"]), "Dragging closed or replaced the picker"
last = colour()
assert last != first, "A second adjustment must update the tint"
call("ui.shot", dict(name="picker-after-drag", dir=str(run)))
input_step("click", at=[x - 20, y + 20])
assert not tree()["popups"], "Clicking outside closes the picker normally"
assert colour() == last, "Closing retains the chosen colour"
call("undo")
assert colour() == first, "One undo restores the colour from before the drag"
call("undo")
assert colour() == original, "The first pick is a separate undo step"
call("redo")
call("redo")
assert colour() == last, "Redo restores the chosen tint"
open_picker()
popup = next(p for p in tree()["popups"] if p["type"] == "PopupPanel")
x, y, _, _ = popup["rect"]
input_step("click", at=[x + 60, y + 150])
input_step("escape")
assert colour() == last, "Escape cancels the current picker session"
assert not tree()["popups"], "Escape closes the picker"
print("PASS live tint preview, repeated picks, continuous drag, close, undo/redo and Escape cancellation")
