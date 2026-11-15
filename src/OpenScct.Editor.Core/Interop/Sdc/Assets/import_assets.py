"""Editor-owned dependency collection, run with the installed OpenSCCT content builder's readers.

Keep original object paths: actors, material graphs and composite sounds continue to refer to the same objects.
Each dependency has an explicit outcome. A fallback image or an absent sound is never counted as success.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
from collections import deque
from pathlib import Path


def collect(args):
    os.environ["SVM_CONTENT"] = str(args.content)
    if args.addons:
        os.environ["SVM_ADDONS"] = str(args.addons)
    else:
        os.environ.pop("SVM_ADDONS", None)
    from svmbuild.paths import import_scctpkg
    import_scctpkg(require_class_defaults=True)
    from svmbuild.mapconv.source import MapSource
    from svmbuild.sources.classdb import class_db
    from svmbuild.sources.typedprops import Ref
    from scctpkg.properties import skip_state_frame
    from scctpkg import export_gltf as gltf
    from scctpkg.materials import MaterialResolver
    from scctpkg.staticmesh import read_static_mesh
    from scctpkg.packageset import PackageSet
    from svmbuild.gamefiles import package_roots
    from svmbuild.writers.glb import pack_glb
    from svmbuild.audio import sound
    from svmbuild.audio.build import sound_json

    db = class_db(str(args.game))
    src = MapSource(args.game, args.map, db)
    packages = PackageSet(package_roots(args.game, "System/_PC_"))
    packages.register(src.pkg)
    added_classes = False
    for i, imp in enumerate(src.pkg.imports):
        if imp.class_name != "Class":
            continue
        # Classes in companion .u packages may contribute asset defaults of their own.
        path = src.pset.object_path(src.pkg, -i - 1)
        top = path.split(".", 1)[0]
        if not db.find(path) and top.lower() not in {"core", "engine", "sbase", "gui", "ipdrv", "softbody"}:
            owner = packages.get(top)
            if owner is not None and owner.path.suffix.lower() == ".u":
                db.add_package_classes(owner)
                added_classes = True
    if added_classes:
        src = MapSource(args.game, args.map, db)
        packages.register(src.pkg)
    src.pset = packages
    # Package.Name aliases collide when two groups contain the same name. Keep full paths throughout this pass.
    packages.import_path = packages.object_path
    original_safe = gltf._safe
    gltf._safe = lambda name: original_safe(name) + "_" + hashlib.sha256(name.encode()).hexdigest()[:10]
    assets = args.project / "assets"
    assets.mkdir(parents=True, exist_ok=True)
    tex_dir = assets / "textures"
    tex_dir.mkdir(exist_ok=True)
    manifest = {"format": "osmap.assets/1", "materials": {}, "textures": {}, "meshes": {}, "sounds": {}, "skeletalMeshes": {}}
    rows = {}
    pending = deque()
    objects = {}
    walked_objects = set()

    def exact(path):
        top = path.split(".", 1)[0]
        pkg = src.pset.get(top)
        if pkg is None:
            return None, None
        key = top.lower()
        if key not in objects:
            objects[key] = {db.cd.export_path(pkg, e).lower(): e for e in pkg.exports}
        return pkg, objects[key].get(path.lower())

    def kind(cls):
        if cls in ("Sound", "StaticMesh", "SkeletalMesh", "MeshAnimation", "VertMesh"):
            return cls
        full = db.find("Engine." + cls) or next((c for c in db.classes if c.rsplit(".", 1)[-1] == cls), None)
        if full and db.is_a(full, "Engine.Material"):
            return "Material"
        return None

    def add(path, cls, used_by):
        if not path or path == "None":
            return
        # Engine.S_* are the original editor's actor icons, absent from cooked installs.
        if path.lower().startswith("engine.s_"):
            return
        key = path.lower()
        if key not in rows:
            rows[key] = {"path": path, "class": cls, "usedBy": [], "status": "pending"}
            pending.append(key)
        if used_by and used_by not in rows[key]["usedBy"]:
            rows[key]["usedBy"].append(used_by)

    def ref(r, where):
        if not r.ref or not r.path or r.is_class:
            return
        pkg, e = src.ref_export(r)
        # Missing imports still carry their declared asset class, which tells us what was lost.
        owner = src.pkg if src.own(r) else src.pset.get(r.owner)
        declared = owner.imports[-r.ref - 1].class_name if owner and r.ref < 0 else "Object"
        cls = e.class_name if e else declared
        if kind(cls):
            add(r.path, cls, where)
        elif pkg is None or e is None:
            add(r.path, cls, where)
        elif pkg is not None and e is not None and r.path.lower() not in walked_objects:
            walked_objects.add(r.path.lower())
            full = db.find(f"{e.class_package}.{cls}")
            rec = db.record(full) if full else None
            if full and db.is_a(full, "Engine.Actor"):
                return  # All level actors are already roots of the dependency walk.
            if rec and e.size > 0:
                try:
                    reader = pkg.export_reader(e)
                    skip_state_frame(reader, e.flags)
                    walk(db.decoder.tagged(reader, pkg, rec["_field"]), r.path)
                except Exception as ex:
                    add(r.path, cls, where)
                known_data = cls in ("Model", "Polys", "Level", "LevelSummary", "StaticMeshInstance", "Palette") or \
                    db.is_a(full, "Engine.ParticleEmitter") or db.is_a(full, "Engine.I3DL2Listener")
                if not known_data:
                    add(r.path, cls, where)
            elif cls not in ("Model", "Polys", "Level", "LevelSummary", "StaticMeshInstance", "Palette"):
                add(r.path, cls, where)

    def walk(value, where):
        if isinstance(value, Ref):
            ref(value, where)
        elif isinstance(value, dict):
            for k, v in value.items():
                walk(v, f"{where}.{k}")
        elif isinstance(value, (list, tuple)):
            for i, v in enumerate(value):
                walk(v, f"{where}[{i}]")

    # Includes nested arrays, emitters, trigger/damage skins and class-default references.
    for a in src.actors + src.cooked_out:
        walk({**db.localized.get(a.cls, {}), **a.props}, a.name)
    # Native tails (BSP, mesh sections, cooked material links) are not tagged actor properties.
    # Import records and map-local asset exports also cover those references.
    for i, imp in enumerate(src.pkg.imports):
        if kind(imp.class_name):
            add(src.pset.object_path(src.pkg, -i - 1), imp.class_name, "map package")
    for e in src.pkg.exports:
        if kind(e.class_name) and e.class_name != "LightMapTexture":
            add(db.cd.export_path(src.pkg, e), e.class_name, "map package")

    writer = gltf.GltfWriter()
    writer.tex_dir, writer.tex_uri = tex_dir, {}
    resolver = MaterialResolver(src.pset)
    material_graph = {}
    skeletal = {}
    source_audio = args.content / "svmap" / args.map / "audio"
    audio_doc = json.loads((source_audio / "audio.json").read_text()) if (source_audio / "audio.json").is_file() else {}
    existing_sounds = {k.lower(): (k, v) for k, v in audio_doc.get("sounds", {}).items()}
    bank_streams = None

    def stream_file(s):
        nonlocal bank_streams
        if bank_streams is None:
            from svmbuild.audio import uas
            from svmbuild.audio.build import bank_file
            bank_path = bank_file(args.game, args.map)
            bank_streams = {}
            if bank_path.is_file():
                data = bank_path.read_bytes()
                bank = uas.read_bank(data)
                for entry in bank.entries:
                    bank_streams[f"{entry.package}.{entry.name}".lower()] = (data[entry.start:entry.end], entry.stream.duration)
        return bank_streams.get(f"{s.package}.{s.stream_name}".lower())

    def fail(row, status, detail):
        row.update(status=status, detail=detail)

    def copy_file(source, folder):
        if not source.is_file():
            raise FileNotFoundError(str(source))
        relative = folder + "/" + hashlib.sha256(str(source).encode()).hexdigest()[:20] + "/" + source.name
        target = assets / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        return relative

    while pending:
        key = pending.popleft()
        row = rows[key]
        path = row["path"]
        pkg, e = exact(path)
        if pkg is None or e is None:
            present = src.pset.files.get(path.split(".", 1)[0].lower())
            if pkg is None and present is not None and present.is_file():
                fail(row, "unsupported", f"Package {present} exists but could not be read")
            else:
                fail(row, "missing", f"Package {path.split('.')[0]} is unavailable" if pkg is None else "Object is absent from its package (including its group path)")
            continue
        cls = e.class_name
        try:
            full = db.find(f"{e.class_package}.{cls}")
            rec = db.record(full) if full else None
            if rec and e.size > 0 and cls not in ("SkeletalMesh", "MeshAnimation"):
                reader = pkg.export_reader(e)
                skip_state_frame(reader, e.flags)
                props = db.decoder.tagged(reader, pkg, rec["_field"])
                walk(props, path)
                if kind(cls) == "Material":
                    from svmbuild.sources.typedprops import bag_to_json, default_resolver
                    material_graph[path] = {"class": full, "props": bag_to_json(props, default_resolver)}
            if cls == "StaticMesh":
                mesh = read_static_mesh(pkg, e)
                # Section material references are decoded by the native mesh reader.
                for entry in mesh.props.get("Materials", []):
                    raw = entry.get("Material", 0) if isinstance(entry, dict) else 0
                    if raw:
                        owner, mat = src.pset.resolve(pkg, raw)
                        add(src.pset.object_path(pkg, raw), mat.class_name if mat else "Material", path)
                primitives = gltf._static_mesh_prims(mesh, pkg, resolver, writer, tex_dir, writer.tex_uri)
                if not primitives:
                    raise ValueError("Static mesh has no convertible triangles")
                index = writer.mesh(path, primitives)
                writer.node(path, index, extras={"svmap": {"role": "library", "asset": path}})
                manifest["meshes"][path] = {"file": "meshes/library.glb", "mesh": index}
            elif kind(cls) == "Material":
                resolved = resolver.resolve(pkg, e.index + 1)
                if resolved is None or (resolved.texture is None and resolved.color is None and not resolved.invisible):
                    raise ValueError("Material has no supported texture or colour; its object graph is retained for inspection")
                uri = gltf._ensure_texture(resolved, tex_dir, writer.tex_uri)
                if resolved.texture is not None and resolved.texture.image is None:
                    raise ValueError("Texture pixel format could not be decoded")
                writer.material(resolved.key, resolved, uri)
                gltf._self_illum_flags(writer, resolved.key, resolved, tex_dir, writer.tex_uri)
                flags = dict(writer.material_flags.get(resolved.key, {}))
                if not uri and resolved.color is not None:
                    from PIL import Image
                    uri = "textures/colour_" + hashlib.sha256(path.encode()).hexdigest()[:20] + ".png"
                    Image.new("RGBA", (1, 1), tuple(resolved.color)).save(assets / uri)
                    flags["texture"] = uri
                material_props = material_graph.get(path, {}).get("props", {})
                gameplay = {name: material_props.get(name, db.default(full, name)) for name in
                            ("SurfaceType", "bClimbable", "bNoSplitJump", "bNoStickyCam", "OcclusionType", "bHideInEMFVision", "bHideInThermalVision", "bEchelonLightThrough")}
                gameplay = {name: value for name, value in gameplay.items() if isinstance(value, (bool, int, float, str))}
                manifest["materials"][path] = {"flags": flags, "uSize": resolved.texture.width if resolved.texture else 256,
                                                 "vSize": resolved.texture.height if resolved.texture else 256, "gameplay": gameplay}
                if uri:
                    manifest["textures"][path] = {"file": uri}
            elif cls == "Sound":
                s = sound.decode_sound(pkg, e)
                resolved_entries = []
                for entry in s.entries:
                    candidates = [x for x in pkg.exports if x.class_name == "Sound" and x.name.lower() == entry.name.lower()]
                    local = [x for x in candidates if x.outer == e.outer]
                    hits = local or candidates
                    if len(hits) != 1:
                        raise ValueError(f"Composite sound child {entry.name!r} is missing or ambiguous")
                    child = db.cd.export_path(pkg, hits[0])
                    resolved_entries.append(child)
                    add(child, "Sound", path)
                info = sound_json(s, resolved_entries, None)
                if s.data is not None:
                    relative = "sounds/" + hashlib.sha256(path.encode()).hexdigest()[:20] + ".wav"
                    (assets / "sounds").mkdir(exist_ok=True)
                    (assets / relative).write_bytes(s.data)
                    info["file"] = relative
                elif info["kind"] in ("stream", "multichannel"):
                    _, converted = existing_sounds.get(key, (path, {}))
                    rel = converted.get("file")
                    if rel:
                        base = args.content / "shared" / "audio" if converted.get("root") == "shared" else source_audio
                        info.update(file=copy_file(base / rel, "sounds"), duration=converted.get("duration", 0))
                    else:
                        found = stream_file(s)
                        if found is None:
                            raise FileNotFoundError("Sound stream is missing from the map's .uas bank")
                        relative = "sounds/" + hashlib.sha256(path.encode()).hexdigest()[:20] + ".ogg"
                        (assets / "sounds").mkdir(exist_ok=True)
                        (assets / relative).write_bytes(found[0])
                        info.update(file=relative, duration=found[1])
                elif not info.get("entries"):
                    raise ValueError(f"Unsupported or empty sound kind {info['kind']}")
                manifest["sounds"][path] = info
            elif cls == "SkeletalMesh":
                if sum(x.class_name == cls and x.name.lower() == e.name.lower() for x in pkg.exports) > 1:
                    raise ValueError("Skeletal meshes with the same name in different groups cannot be exported separately by this builder")
                skeletal.setdefault(str(pkg.path), (pkg, []) )[1].append((path, e.name))
                row["status"] = "deferred"
                continue
            elif cls == "MeshAnimation":
                from svmbuild.charconv import animation_document
                document = animation_document(db, pkg, e, src.notes)
                relative = "animations/" + hashlib.sha256(path.encode()).hexdigest()[:20] + ".anim.json"
                (assets / "animations").mkdir(exist_ok=True)
                (assets / relative).write_text(json.dumps(document), encoding="utf-8")
            else:
                raise ValueError(f"No asset converter for {cls}")
            row["status"] = "imported"
        except FileNotFoundError as ex:
            fail(row, "missing", str(ex))
        except Exception as ex:
            fail(row, "unsupported", f"{type(ex).__name__}: {ex}")

    from svmbuild.charconv import export_package
    for pkg, wanted in skeletal.values():
        destination = assets / "chars" / pkg.path.stem
        result = export_package(pkg, src.pset, [name for _, name in wanted], destination, db, src.notes, args.project / "skeletal-work")
        for path, name in wanted:
            row = rows[path.lower()]
            file = destination / (name + ".glb")
            if file.is_file():
                manifest["skeletalMeshes"][path] = {"file": file.relative_to(assets).as_posix()}
                row["status"] = "imported"
            else:
                fail(row, "unsupported", "; ".join(result.get("failed", [])) or "Skeletal mesh conversion produced no file")
        for problem in result.get("failed", []):
            path = pkg.path.stem + "." + problem.split(":", 1)[0]
            if path.lower() not in rows:
                rows[path.lower()] = {"path": path, "class": "MeshAnimation", "status": "unsupported", "detail": problem,
                                     "usedBy": [path for path, _ in wanted]}

    (assets / "meshes").mkdir(exist_ok=True)
    temporary = assets / "meshes" / "library.gltf"
    writer.write(temporary)
    document = json.loads(temporary.read_text())
    # The writer expects textures/ next to the glTF; the manifest and GLB share the asset root instead.
    for img in document.get("images", []):
        if "uri" in img:
            img["uri"] = "../" + img["uri"]
    temporary.with_suffix(".glb").write_bytes(pack_glb(document, temporary.with_suffix(".bin").read_bytes()))
    temporary.unlink()
    temporary.with_suffix(".bin").unlink()
    (assets / "material-graph.json").write_text(json.dumps(material_graph, ensure_ascii=True), encoding="utf-8")
    (assets / "assets.json").write_text(json.dumps(manifest, ensure_ascii=True), encoding="utf-8")

    # Preserve custom script source through the native importer, and explicitly flag behavior without a port.
    lifted = {"smatchsizerules", "scharacteroutfits"}
    for a in src.actors:
        top, _, short = a.cls.partition(".")
        # ConveyorBelt has a native port only when its complete expected class is recognized by the builder.
        known_conveyor = short.lower() == "conveyorbelt" and bool(db.record(a.cls))
        custom = a.cls in src.map_classes or top.lower() not in {"core", "engine", "sbase", "softbody", "gui", "ipdrv"}
        if a.error:
            rows.setdefault("actor:" + a.name.lower(), {"path": a.cls, "class": "Actor", "status": "missing" if packages.get(top) is None else "unsupported",
                "detail": "Actor properties could not be completely read: " + a.error, "usedBy": [a.name + ".Class"]})
        elif custom and short.lower() not in lifted and not known_conveyor:
            rows.setdefault("script:" + a.cls.lower(), {"path": a.cls, "class": "UnrealScript", "status": "unsupported",
                "detail": "Class properties are preserved when readable; arbitrary UnrealScript execution is not supported", "usedBy": [a.name + ".Class"]})
    manifest["sourceMap"] = copy_file(src.path, "source")
    for row in rows.values():
        if row["status"] == "unsupported":
            pkg, _ = exact(row["path"])
            package_file = pkg.path if pkg else src.pset.files.get(row["path"].split(".", 1)[0].lower())
            if package_file is not None and package_file.is_file():
                row["originalPackage"] = manifest["sourceMap"] if package_file == src.path else copy_file(package_file, "original-packages")
                row["detail"] += "; original package retained in assets/" + row["originalPackage"]
    (assets / "assets.json").write_text(json.dumps(manifest, ensure_ascii=True), encoding="utf-8")
    output = sorted(rows.values(), key=lambda r: r["path"].lower())
    report = {"assets": output, **{status: sum(r["status"] == status for r in output) for status in ("imported", "missing", "unsupported")}}
    args.report.write_text(json.dumps(report, ensure_ascii=True), encoding="utf-8")
    (assets / "dependencies.json").write_text(json.dumps(report, ensure_ascii=True), encoding="utf-8")
    print(f"assets: {report['imported']} imported, {report['missing']} missing, {report['unsupported']} unsupported", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--map", required=True)
    for name in ("game", "content", "project", "report"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--addons", type=Path)
    collect(parser.parse_args())
