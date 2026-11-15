"""Run against the disposable background SoundDemo session; never the user's editor."""
import json
import pathlib
import socket
import sys
import time
from PIL import Image

run = pathlib.Path(sys.argv[1]).resolve()
meta = json.loads(next((run / "live").glob("editor-*.json")).read_text())
sock = socket.create_connection(("127.0.0.1", meta["port"]), timeout=90)
stream = sock.makefile("rwb")
stream.write((json.dumps(dict(auth=meta["token"], client="asset-and-preview-checks")) + "\n").encode())
stream.flush()
assert json.loads(stream.readline())["ok"]
sequence = 0


def call(method, params=None):
    global sequence
    sequence += 1
    stream.write((json.dumps(dict(id=sequence, method=method, params=params or {})) + "\n").encode())
    stream.flush()
    reply = json.loads(stream.readline())
    assert "error" not in reply, reply
    return reply["result"]


def step(op, **args):
    return call("ui.input", dict(steps=[dict(op=op, frames=8) | args]))


def controls():
    return call("ui.tree", dict(limit=1800))["controls"]


def search(suffix, text):
    entry = next(c for c in controls() if c["path"].endswith(suffix))
    step("click", at=entry["path"])
    step("key", key="Ctrl+A")
    step("type", text=text)


def shot(name):
    result = call("ui.shot", dict(name=name, dir=str(run)))
    assert not result["lint"], result["lint"]
    return pathlib.Path(result["path"])


assert call("editor")["map"] == "SoundDemo"
call("apply", dict(ops=[dict(op="select", mode="clear")]))
step("click", at="action:sound.library")
search("/SoundSearch", "Amb_Aqua AirDoor_Open")
tree = next(c for c in controls() if c["path"].endswith("/Library") and c["type"] == "Tree")
x, y, w, h = tree["rect"]
assert h > call("ui.tree")["canvas"][1] * 0.3, ("Sound list must use the available panel height", h)
step("click", at=[x + w * 0.5, y + 38])
image = Image.open(shot("short-audio-waveform")).convert("RGB")
wave = next(c for c in controls() if c["path"].endswith("/SoundWaveform"))
wx, wy, ww, wh = map(int, wave["rect"])
wave_pixels = image.crop((wx, wy, wx + ww, wy + wh))
assert sum(b > r * 1.3 and b > g * 1.1 for r, g, b in wave_pixels.getdata()) > ww * 3, "Selected short sound must show a waveform before playback"
step("click", at="text:Play")
assert any(c.get("text") == "Pause" for c in controls()), "Subsecond clip starts playback"
step("escape", frames=90)
assert any(c.get("text") == "Play" for c in controls()), "Short clip completes normally"
print("PASS short shared clip lookup, selected waveform, playback completion and expanded sound list")

# Exercise the decoder path too: OGG streams do not expose PCM via AudioStreamWav.Data.
packs = pathlib.Path.home() / "AppData/Local/OpenSCCT/packs/svmap"
compressed = []
for audio_file in packs.glob("*/audio/audio.json"):
    document = json.loads(audio_file.read_text())
    for path, sound in document.get("sounds", {}).items():
        relative = sound.get("file", "")
        if relative.endswith(".ogg") and sound.get("root") != "shared" and sound.get("duration", 0) > 0 and (audio_file.parent / relative).is_file():
            compressed.append((sound["duration"], path))
assert compressed, "A converted OGG fixture is installed"
_, path = min(compressed)
search("/SoundSearch", " ".join(path.split(".")))
tree = next(c for c in controls() if c["path"].endswith("/Library") and c["type"] == "Tree")
x, y, w, h = tree["rect"]
step("click", at=[x + w * 0.5, y + 38])
step("escape", frames=90)
image = Image.open(shot("compressed-audio-waveform")).convert("RGB")
wave = next(c for c in controls() if c["path"].endswith("/SoundWaveform"))
wx, wy, ww, wh = map(int, wave["rect"])
assert sum(b > r * 1.3 and b > g * 1.1 for r, g, b in image.crop((wx, wy, wx + ww, wy + wh)).getdata()) > ww * 2, "Compressed sound has a decoded waveform"
print("PASS decoded OGG waveform:", path)

step("click", at="action:emitter.library")
deadline = time.monotonic() + 45
while any(c.get("text") == "Reading effects…" for c in controls()):
    assert time.monotonic() < deadline, "Emitter catalogue did not finish loading"
    step("escape", frames=30)
search("/EffectSearch", "Rising bubbles")
tree = next(c for c in controls() if c["path"].endswith("/Effects") and c["type"] == "Tree")
x, y, w, h = tree["rect"]
row = [x + w * 0.5, y + 60]
step("click", at=row)
assert any("Rising bubbles ·" in c.get("text", "") for c in controls()), "Rising bubbles is selected"
step("click", at="text:Frame FX")
step("escape", frames=240)
image = Image.open(shot("emitter-bubbles-framed")).convert("RGB")
preview = next(c for c in controls() if c["path"].endswith("/EffectPreview"))
px, py, pw, ph = map(int, preview["rect"])
pixels = image.crop((px, py, px + pw, py + ph))
bright = [(i % pw, i // pw) for i, (r, g, b) in enumerate(pixels.getdata()) if max(r, g, b) > 35]
assert bright, "Particle preview draws the selected effect"
left, right = min(p[0] for p in bright), max(p[0] for p in bright)
top, bottom = min(p[1] for p in bright), max(p[1] for p in bright)
assert left > 1 and right < pw - 2 and top > 1 and bottom < ph - 2, "FX fits inside the preview"
assert bottom - top > ph * 0.35, ("Small FX should fill a useful part of the preview", top, bottom, ph)
before = call("query", dict(q="find", fields=["id", "class"]))["items"]
step("drag", at=row, to="view:Perspective", toOffset=[0.65, 0.55], steps=24)
after = call("query", dict(q="find", fields=["id", "class"]))["items"]
assert len(after) > len(before), "Dragging an emitter adds its actors"
assert call("editor")["selectionCount"] == len(after) - len(before), "New effect actors are selected"
step("click", at="action:edit.undo")
assert len(call("query", dict(q="find"))["items"]) == len(before), "One undo removes the whole dragged effect"
shot("emitter-layout-and-drag")
print("PASS emitter layout, full-effect framing, drag placement and one-step undo")
step("key", key="Ctrl+Shift+P")
step("type", text="150%")
step("key", key="Enter")
shot("emitter-layout-150")
step("key", key="Ctrl+Shift+P")
step("type", text="100%")
step("key", key="Enter")
print("PASS emitter panel fits at 150% scale")
stream.close()
sock.close()
