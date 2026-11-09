"""Check real native material windows on an invisible Windows desktop, with isolated settings."""
import argparse
import os
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', required=True, type=Path)
parser.add_argument('--fixture', type=Path, help='Optional .osmap containing MyLevel.Textures.Material; otherwise uses generated maps in all slots')
parser.add_argument('--out', type=Path, default=root / 'local/material-window-checks')
args = parser.parse_args()
if os.name != 'nt':
    raise SystemExit('Native Windows dialog checks require Windows.')
sys.path.insert(0, str(root.parent / 'OpenSCCT-Editor-deps/tools/golden'))
import render_svm
render_svm.DESKTOP = 'OSEMaterialBounds-' + str(os.getpid())
out = args.out.resolve()
out.mkdir(parents=True, exist_ok=True)
for key in ['APPDATA', 'LOCALAPPDATA', 'OSE_USER_DATA', 'OSE_LIVE_DIR', 'OSE_CACHE']:
    folder = out / key
    folder.mkdir(exist_ok=True)
    os.environ[key] = str(folder)
os.environ['OSE_NATIVE_MATERIAL_CHECKS'] = '1'
for key in ['OSE_BACKGROUND', 'OSE_FOCUS', 'OSE_NATIVE_MATERIAL_FIXTURE']:
    os.environ.pop(key, None)
if args.fixture:
    os.environ['OSE_NATIVE_MATERIAL_FIXTURE'] = str(args.fixture.resolve())
godot = args.godot.resolve()
if godot.stem.endswith('_console'):
    godot = godot.with_name(godot.stem.removesuffix('_console') + godot.suffix)
log = out / 'native.log'
code, seconds = render_svm.run_hidden([str(godot), '--path', str(root / 'app'), '--audio-driver', 'Dummy', '--log-file', str(log)], str(root), 120)
lines = log.read_text(errors='replace').splitlines()
for line in lines:
    if 'MATERIAL_NATIVE' in line or 'ERROR' in line:
        print(line)
print(f'Native checks exited {code} after {seconds:.1f}s; {out}')
sys.exit(code if code else 0 if any(line.startswith('MATERIAL_NATIVE OK') for line in lines) else 1)
