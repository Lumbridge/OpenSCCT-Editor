"""Build the bounded authoring bundle from the verified converted PS2 objective assets.

Binary inputs and output stay outside Git. The optional contract contains only
paths, sizes, hashes and provenance, and is embedded by the Core library.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import struct


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False) + "\n").encode()


def read_glb(path):
    raw = path.read_bytes()
    assert struct.unpack_from("<III", raw) == (0x46546C67, 2, len(raw))
    size, kind = struct.unpack_from("<II", raw, 12)
    assert kind == 0x4E4F534A
    data = json.loads(raw[20:20 + size])
    start = 20 + size
    return data, raw[start + 8:] if start < len(raw) else b""


def write_glb(path, data, binary):
    binary += b"\0" * (-len(binary) % 4)
    data["buffers"] = [{"byteLength": len(binary)}]
    text = json_bytes(data).rstrip()
    text += b" " * (-len(text) % 4)
    payload = struct.pack("<II", len(text), 0x4E4F534A) + text
    payload += struct.pack("<II", len(binary), 0x004E4942) + binary
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + len(payload)) + payload)


def build(source, output):
    if output.exists():
        raise ValueError("Use a new bundle output folder; existing files are never replaced")
    render = source / "render"
    classes = json.loads((source / "sim/classes.json").read_text())["classes"]
    vial = classes["SBase.SFlag"]["defaults"]["Ps2SourceStaticMesh"]["$asset"]
    console = classes["SBase.SComputerObjectiveTrigger"]["defaults"]["Mesh"]["$asset"]
    source_gltf, source_binary = read_glb(render / "world.glb")
    node = next(n for n in source_gltf["nodes"] if n.get("extras", {}).get("svmap", {}).get("asset") == vial)
    mesh = copy.deepcopy(source_gltf["meshes"][node["mesh"]])
    data = {"asset": {"version": "2.0", "generator": "OpenSCCT Editor bounded PS2 authoring bundle"},
            "nodes": [{"name": vial, "mesh": 0, "extras": {"svmap": {"role": "libraryMesh", "asset": vial, "variant": ""}}}],
            "scenes": [{"nodes": [0]}], "scene": 0, "meshes": [mesh],
            "accessors": [], "bufferViews": [], "materials": [], "textures": [], "images": [],
            "samplers": copy.deepcopy(source_gltf.get("samplers", []))}
    binary = bytearray()
    accessors, views, materials, textures = {}, {}, {}, {}
    assets = output / "assets"
    prefix = "ps2-objectives"
    table = json.loads((render / "materials.json").read_text())
    manifest = {"format": "osmap.assets/1", "materials": {}, "meshes": {}, "skeletalMeshes": {},
                "textures": {}, "sounds": {}, "ps2Objectives": {"version": 1, "resources": []}}

    def accessor(old):
        if old in accessors:
            return accessors[old]
        a = copy.deepcopy(source_gltf["accessors"][old])
        v = a["bufferView"]
        if v not in views:
            view = copy.deepcopy(source_gltf["bufferViews"][v])
            start = view.get("byteOffset", 0)
            binary.extend(b"\0" * (-len(binary) % 4))
            view["byteOffset"] = len(binary)
            binary.extend(source_binary[start:start + view["byteLength"]])
            view["buffer"] = 0
            views[v] = len(data["bufferViews"])
            data["bufferViews"].append(view)
        a["bufferView"] = views[v]
        accessors[old] = len(data["accessors"])
        data["accessors"].append(a)
        return accessors[old]

    def texture(old):
        if old not in textures:
            t = copy.deepcopy(source_gltf["textures"][old])
            image = copy.deepcopy(source_gltf["images"][t["source"]])
            relative = image["uri"]
            target = assets / prefix / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(render / relative, target)
            image["uri"] = prefix + "/" + relative
            t["source"] = len(data["images"])
            data["images"].append(image)
            textures[old] = len(data["textures"])
            data["textures"].append(t)
        return textures[old]

    def material(old):
        if old not in materials:
            m = copy.deepcopy(source_gltf["materials"][old])
            def fix(value):
                if isinstance(value, dict):
                    for key, child in value.items():
                        if key.endswith("Texture") and isinstance(child, dict) and "index" in child:
                            child["index"] = texture(child["index"])
                        else:
                            fix(child)
                elif isinstance(value, list):
                    for child in value:
                        fix(child)
            fix(m)
            materials[old] = len(data["materials"])
            data["materials"].append(m)
            entry = copy.deepcopy(table["materials"][m["name"]])
            flags = entry["flags"]
            for key in ("texture", "selfIllumTexture", "opacityTexture"):
                if key in flags:
                    flags[key] = prefix + "/" + flags[key]
            manifest["materials"][entry["source"]] = {"flags": flags, "uSize": 64, "vSize": 64}
        return materials[old]

    for primitive in mesh["primitives"]:
        primitive["attributes"] = {key: accessor(value) for key, value in primitive["attributes"].items()}
        primitive["indices"] = accessor(primitive["indices"])
        primitive["material"] = material(primitive["material"])
    file = prefix + "/meshes/vial.glb"
    write_glb(assets / file, data, binary)
    manifest["meshes"][vial] = {"file": file, "mesh": 0}

    console_folder = render / "chars/nd133_1mesh"
    for source_file in sorted(console_folder.rglob("*")):
        if not source_file.is_file():
            continue
        relative = source_file.relative_to(render).as_posix()
        project_file = prefix + "/" + relative
        destination = assets / project_file
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_file, destination)
        manifest["ps2Objectives"]["resources"].append({"file": project_file, "output": "render/" + relative})
    manifest["skeletalMeshes"][console] = {"file": prefix + "/chars/nd133_1mesh/nd133_mesh.glb"}
    console_json, _ = read_glb(console_folder / "nd133_mesh.glb")
    for material in console_json["materials"]:
        extras = material.get("extras", {})
        if "scct_vision_source" not in extras:
            continue
        flags = copy.deepcopy(extras.get("scct_material_flags", {}))
        def local_textures(value):
            if isinstance(value, dict):
                for key, child in list(value.items()):
                    if isinstance(child, str) and child.startswith("textures/"):
                        value[key] = prefix + "/chars/nd133_1mesh/" + child
                    else:
                        local_textures(child)
            elif isinstance(value, list):
                for child in value:
                    local_textures(child)
        local_textures(flags)
        manifest["materials"][extras["scct_vision_source"]] = {"flags": flags, "uSize": 256, "vSize": 256}
    manifest["ps2Objectives"]["requiredFiles"] = [f.relative_to(assets).as_posix() for f in sorted(assets.rglob("*")) if f.is_file()]
    (assets / "assets.json").write_bytes(json_bytes(manifest))
    files = {}
    for file in sorted(assets.rglob("*")):
        if file.is_file():
            raw = file.read_bytes()
            files[file.relative_to(output).as_posix()] = {"size": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
    contract = {"format": "ose.ps2-objectives/1", "version": 1,
                "source": {"platform": "PlayStation 2", "game": "Splinter Cell Chaos Theory Versus",
                           "sourceMapManifestSha256": hashlib.sha256((source / "manifest.json").read_bytes()).hexdigest(),
                           "runtime": "0.8.158", "assetProof": "Original PS2 vial and ND133 materials; retained ND133 mesh/72 motion tracks verified against the PS2 ISO."},
                "files": files}
    (output / "bundle.json").write_bytes(json_bytes(contract))
    return contract


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Verified converted PS2 map folder")
    parser.add_argument("--output", type=Path, required=True, help="New ignored/local bundle folder")
    parser.add_argument("--write-contract", type=Path)
    options = parser.parse_args()
    result = build(options.source.resolve(), options.output.resolve())
    if options.write_contract:
        options.write_contract.write_bytes(json_bytes(result))
    print(f"Built {len(result['files'])} verified files / {sum(x['size'] for x in result['files'].values())} bytes")
