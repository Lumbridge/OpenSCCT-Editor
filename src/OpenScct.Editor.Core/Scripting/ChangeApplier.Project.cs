using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    /// <summary>
    /// link: UE2 event wiring. <c>{"from":a,"to":b}</c> sets a.Event and b.Tag to one name (<c>tag</c>, else b's
    /// stored Tag, else b's id), so a fires b; <c>from</c> may list several actors. An SAlarm sender links as
    /// <see cref="Gameplay.Wiring.Link(MapProject, string, IReadOnlyList{string})"/> does: movers and objectives go to its
    /// lock lists, anything else to its Events. With <c>prop</c> it sets an actor reference property instead
    /// (<c>{"from":cam,"prop":"m_Target","to":x}</c>; several targets fill an array). <c>clear</c> removes the link.
    /// </summary>
    private static void Link(ChangeContext ctx, SvJsonObject op)
    {
        List<string> fromIds = ApiJson.StringList(op["from"], "from") ?? throw new ApiException("from is the actor (or actors) that fire", "from");
        var froms = fromIds.Select((f, i) => ctx.Resolve(f, fromIds.Count == 1 ? "from" : $"from[{i}]")).ToList();
        foreach (ObjectRef f in froms.Where(f => f.Kind != ObjectKind.Actor))
        {
            throw new ApiException($"{f.Id} is not an actor", "from");
        }

        string? prop = ApiJson.GetString(op, "prop");
        bool clear = ApiJson.GetBool(op, "clear", false);
        List<string> toIds = ApiJson.StringList(op["to"], "to") ?? [];
        if (!clear && toIds.Count == 0)
        {
            throw new ApiException("to is the actor (or actors) to trigger", "to");
        }

        var to = toIds.Select((t, i) => ctx.Resolve(t, toIds.Count == 1 ? "to" : $"to[{i}]")).ToList();
        if (prop is not null)
        {
            foreach (ObjectRef from in froms)
            {
                LinkProp(ctx, from, prop, to, clear);
            }

            return;
        }

        if (clear)
        {
            ctx.Edit(froms, "Unlink", actor: a =>
            {
                a.Props.Remove("Event");
                if (IsAlarm(a))
                {
                    a.Props.Remove("Events");
                }
            });
            return;
        }

        if (to.Any(t => t.Kind != ObjectKind.Actor))
        {
            throw new ApiException("Event/Tag links join actors", "to");
        }

        // An alarm fires its Events list (and locks movers and objectives), not Event: it links as the wiring tool does.
        List<ObjectRef> alarms = froms.Where(f => IsAlarm(ctx.Project.FindActor(f.Id)!)).ToList();
        List<ObjectRef> senders = froms.Except(alarms).ToList();
        string? given = ApiJson.GetString(op, "tag");
        if (senders.Count > 0 || given is not null)
        {
            // The class-name default Tag would also reach every other actor of that class, so a fresh link uses the id.
            string? stored = ctx.Project.FindActor(to[0].Id)!.Props.GetString("Tag");
            string tag = given ?? (stored is { Length: > 0 } && stored != "None" ? stored : to[0].Id);
            foreach (ObjectRef t in to)
            {
                string? old = ctx.Project.FindActor(t.Id)!.Props.GetString("Tag");
                if (old is { Length: > 0 } && old != tag && ctx.Project.Actors.Any(a => a.Props.GetString("Event") == old))
                {
                    ctx.Warn("tag", $"{t.Id}'s Tag changes from {old} to {tag}; events that fire {old} no longer reach it");
                }
            }

            ctx.Edit(to, $"Tag {tag}", actor: a => a.Props.Set("Tag", SvJsonNode.String(tag)));
            ctx.Edit(senders, $"Link to {tag}", actor: a => a.Props.Set("Event", SvJsonNode.String(tag)));
        }

        foreach (ObjectRef alarm in alarms)
        {
            if (Gameplay.Wiring.Link(ctx.Project, alarm.Id, to.Select(t => t.Id).ToList()) is not { } cmd)
            {
                continue;
            }

            List<ObjectRef> touched = [alarm, .. to];
            touched.ForEach(ctx.Touch);
            ctx.Run(cmd, touchesBrushes: false);
            ctx.Edit(touched, "Stamp", actor: _ => { }); // provenance, as every other edit gets
        }
    }

    private static bool IsAlarm(Actor a) => a.ShortClass == "SAlarm";

    private static void LinkProp(ChangeContext ctx, ObjectRef from, string prop, List<ObjectRef> to, bool clear)
    {
        Actor actor = ctx.Project.FindActor(from.Id)!;
        PropInfo? info = ctx.Coercion.Lookup(actor.Class, prop, "prop", ctx.Warnings);
        string name = info?.Name ?? prop;
        bool isArray = info?.Type == "array" || (info is null && actor.Props[name] is SvJsonArray);
        if (!clear && to.Count > 1 && !isArray)
        {
            throw new ApiException($"{name} holds one reference; give one target", "to");
        }

        ctx.Edit([from], $"Link {from.Id}.{name}", actor: a =>
        {
            if (clear && to.Count == 0)
            {
                a.Props.Remove(name);
                return;
            }

            if (!isArray)
            {
                a.Props[name] = clear ? null : PropValues.ActorRef(to[0].Id);
                return;
            }

            var items = (a.Props[name] as SvJsonArray)?.ToList() ?? new List<SvJsonNode>();
            foreach (ObjectRef t in to)
            {
                items.RemoveAll(x => string.Equals(PropValues.ActorId(x), t.Id, StringComparison.OrdinalIgnoreCase));
                if (!clear)
                {
                    items.Add(PropValues.ActorRef(t.Id));
                }
            }

            a.Props[name] = items.Count == 0 ? null : new SvJsonArray(items);
        });
    }

    /// <summary>layer: creates the layer <c>name</c> if needed, then sets <c>color</c>/<c>visible</c>/<c>locked</c>,
    /// <c>rename</c>s or <c>delete</c>s it (its objects go to Default), and puts objects on it (<c>add</c> ids or
    /// <c>where</c>).</summary>
    private static void LayerOp(ChangeContext ctx, SvJsonObject op)
    {
        string name = ApiJson.GetString(op, "name") ?? throw new ApiException("name is the layer name", "name");
        bool isDefault = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        string stored = isDefault ? "" : name;
        ctx.SnapshotProject();
        if (ApiJson.GetBool(op, "delete", false))
        {
            if (isDefault)
            {
                throw new ApiException("the Default layer cannot be deleted", "delete");
            }

            ctx.Edit(OnLayer(ctx, name), $"Clear layer {name}", brush: b => b.Editor.Layer = "", actor: a => a.Editor.Layer = "", ge: g => g.Editor.Layer = "");
            ctx.Run(new LayerListCommand($"Delete layer {name}", list => list.RemoveAll(l => Same(l.Name, name))), touchesBrushes: false);
            return;
        }

        string? color = ApiJson.GetString(op, "color");
        if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new ApiException("color is #RRGGBB", "color");
        }

        string? rename = ApiJson.GetString(op, "rename");
        if (rename is not null)
        {
            if (isDefault)
            {
                throw new ApiException("the Default layer cannot be renamed", "rename");
            }

            if (!ctx.Project.Layers.Any(l => Same(l.Name, name)))
            {
                throw new ApiException($"no layer {name} to rename", "name");
            }

            if (rename.Length == 0 || Same(rename, "Default") || ctx.Project.Layers.Any(l => Same(l.Name, rename)))
            {
                throw new ApiException($"layer {rename} exists", "rename");
            }
        }

        bool? visible = op["visible"] is { } v ? v.Kind == SvJsonKind.True : null;
        bool? locked = op["locked"] is { } lk ? lk.Kind == SvJsonKind.True : null;
        ctx.Run(new LayerListCommand($"Layer {name}", list =>
        {
            Layer layer = list.Find(l => Same(l.Name, name)) ?? Add(list, new Layer(isDefault ? "Default" : name));
            layer.Color = color ?? layer.Color;
            layer.Visible = visible ?? layer.Visible;
            layer.Locked = locked ?? layer.Locked;
            if (rename is not null)
            {
                layer.Name = rename;
            }
        }), touchesBrushes: false);
        if (rename is not null)
        {
            ctx.Edit(OnLayer(ctx, name), $"Rename layer {name}", brush: b => b.Editor.Layer = rename, actor: a => a.Editor.Layer = rename, ge: g => g.Editor.Layer = rename);
            stored = rename;
        }

        var members = new List<ObjectRef>();
        if (ApiJson.StringList(op["add"], "add") is { } add)
        {
            members.AddRange(add.Select((id, i) => ctx.Resolve(id, $"add[{i}]")));
        }

        if (op["where"] is not null)
        {
            members.AddRange(ctx.Targets(Subset(op, ["where"], null), required: false));
        }

        ctx.Edit(members.Distinct().ToList(), $"Move to layer {name}", brush: b => b.Editor.Layer = stored, actor: a => a.Editor.Layer = stored,
            ge: g => g.Editor.Layer = stored);

        static Layer Add(List<Layer> list, Layer l)
        {
            list.Add(l);
            return l;
        }
    }

    private static List<ObjectRef> OnLayer(ChangeContext ctx, string name) =>
        ctx.Project.AllRefs().Where(r => ObjectFilter.LayerIs(ctx.Project.MetaOf(r)?.Layer ?? "", name)).ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>folder: puts objects (<c>id</c>/<c>ids</c>/<c>where</c>) in the outliner folder <c>path</c>, or
    /// <c>rename</c>s the folder (and its subfolders) to a new path.</summary>
    private static void FolderOp(ChangeContext ctx, SvJsonObject op)
    {
        string path = (ApiJson.GetString(op, "path") ?? throw new ApiException("path is the folder path, e.g. Security/Cameras", "path")).Trim('/');
        if (ApiJson.GetString(op, "rename") is { } renamed)
        {
            string to = renamed.Trim('/');
            List<ObjectRef> inside = ctx.Project.AllRefs().Where(r => ctx.Project.MetaOf(r)?.Folder is { } f && (Same(f, path) || f.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))).ToList();
            void Move(EditorMeta m) => m.Folder = to + m.Folder[path.Length..];
            ctx.Edit(inside, $"Rename folder {path}", brush: b => Move(b.Editor), actor: a => Move(a.Editor), ge: g => Move(g.Editor));
            return;
        }

        List<ObjectRef> targets = ctx.Targets(op);
        ctx.Edit(targets, $"Move to folder {path}", brush: b => b.Editor.Folder = path, actor: a => a.Editor.Folder = path, ge: g => g.Editor.Folder = path);
    }

    /// <summary>settings: <c>levelInfo</c> props, <c>mapSettings</c> per mode (<c>"*"</c> = every mode), <c>mapInfo</c>
    /// strings, <c>title</c>, <c>mapId</c>, <c>lighting</c> (the lightmap bake settings, any subset of
    /// map.json's <c>lighting</c> object), <c>baked</c> (<c>keepRender</c>, <c>keepCollision</c>: which layers of an
    /// imported map's baked world the build keeps). Null values remove.</summary>
    private static void Settings(ChangeContext ctx, SvJsonObject op)
    {
        ctx.SnapshotProject();
        var level = new List<(string Name, SvJsonNode? Value)>();
        if (op["levelInfo"] is { } li)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in Members(li, "levelInfo"))
            {
                level.Add(ctx.Coercion.Coerce("Engine.LevelInfo", kv.Key, kv.Value, ctx.Project.LevelInfo, $"levelInfo.{kv.Key}", ctx.Warnings));
            }
        }

        var modes = new List<(string Mode, string Name, SvJsonNode? Value)>();
        if (op["mapSettings"] is { } ms)
        {
            // Per-mode values are fields of the mode settings struct, not class properties: keep each value's
            // existing type (TimeLimit stays an integer) and the existing name's spelling.
            foreach (KeyValuePair<string, SvJsonNode> mode in Members(ms, "mapSettings"))
            {
                IEnumerable<string> names = mode.Key == "*" ? ctx.Project.MapSettings.Keys.ToList() : [mode.Key];
                foreach (KeyValuePair<string, SvJsonNode> kv in Members(mode.Value, $"mapSettings.{mode.Key}"))
                {
                    (string name, SvJsonNode? value) = LikeExisting(ctx.Project.MapSettings.Values, kv.Key, kv.Value, $"mapSettings.{mode.Key}.{kv.Key}");
                    modes.AddRange(names.Select(m => (m, name, value)));
                }
            }
        }

        var info = new List<(string Name, SvJsonNode? Value)>();
        if (op["mapInfo"] is { } mi)
        {
            info.AddRange(Members(mi, "mapInfo").Select(kv => (kv.Key, ApiJson.IsNull(kv.Value) ? null : (SvJsonNode?)kv.Value)));
        }

        string? title = ApiJson.GetString(op, "title")?.Trim();
        if (title is { Length: 0 or > ProjectIdentity.MaxName })
        {
            // The title is the map's name in menus (identity.json): the same limits as the identity op's name.
            throw new ApiException($"title is 1 to {ProjectIdentity.MaxName} characters", "title");
        }
        string? mapId = ApiJson.GetString(op, "mapId");
        if (mapId is not null && !System.Text.RegularExpressions.Regex.IsMatch(mapId, "^[A-Za-z0-9_]+$"))
        {
            throw new ApiException("mapId is letters, digits and _ (the original game needs exactly 5 characters)", "mapId");
        }

        if (mapId is { Length: not 5 })
        {
            ctx.Warn("mapId", "the original game needs a 5-character map id");
        }

        LightingSettings? lighting = null;
        if (op["lighting"] is { } lo)
        {
            if (lo is not SvJsonObject lightingObject)
            {
                throw new ApiException("lighting is an object (mode, density, samples, lightSize, sunSize, bounce, bounceSamples, atlasSize, relightImported, bakeDefaultLightRadius)", "lighting");
            }

            try
            {
                lighting = ProjectStore.LightingFromJson(lightingObject, ctx.Project.Lighting);
            }
            catch (FormatException e)
            {
                throw new ApiException(e.Message, "lighting");
            }

            if (lighting.Problem() is { } problem)
            {
                throw new ApiException(problem, "lighting");
            }
        }

        (bool Render, bool Collision)? keep = null;
        if (op["baked"] is { } bo)
        {
            if (bo is not SvJsonObject bakedObject)
            {
                throw new ApiException("baked is an object (keepRender, keepCollision)", "baked");
            }

            if (ctx.Project.Baked is not { } current)
            {
                throw new ApiException("the map has no baked world (only an imported map has one)", "baked");
            }

            keep = (ApiJson.GetBool(bakedObject, "keepRender", current.KeepRender), ApiJson.GetBool(bakedObject, "keepCollision", current.KeepCollision));
            if (keep.Value.Render != keep.Value.Collision && !ctx.Project.Brushes.Any(BakedContent.IsBaked))
            {
                ctx.Warn("baked", "the map has no brushes to rebuild the layer that is not kept from, so that layer will be empty");
            }
        }

        ctx.Run(new ProjectSettingsCommand("Map settings", p =>
        {
            foreach ((string name, SvJsonNode? value) in level)
            {
                p.LevelInfo[name] = value;
            }

            foreach ((string mode, string name, SvJsonNode? value) in modes)
            {
                if (!p.MapSettings.TryGetValue(mode, out PropSet? set))
                {
                    p.MapSettings[mode] = set = new PropSet();
                }

                set[name] = value;
            }

            foreach ((string name, SvJsonNode? value) in info)
            {
                p.MapInfo[name] = value;
            }

            p.Title = title ?? p.Title;
            p.Id = mapId ?? p.Id;
            p.Lighting = lighting?.Clone() ?? p.Lighting;
            if (keep is { } k && p.Baked is { } baked)
            {
                baked.KeepRender = k.Render;
                baked.KeepCollision = k.Collision;
            }
        }), touchesBrushes: keep is not null);
    }

    /// <summary>A value shaped like the same setting elsewhere: an integer where it is stored as one, a float where
    /// it is a float; unknown names pass through.</summary>
    private static (string Name, SvJsonNode? Value) LikeExisting(IEnumerable<PropSet> sets, string key, SvJsonNode value, string field)
    {
        if (ApiJson.IsNull(value))
        {
            return (key, null);
        }

        foreach (PropSet set in sets)
        {
            string? name = set.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                continue;
            }

            SvJsonNode? like = set[name];
            if (like is SvJsonNumber n && ApiJson.TryNumber(value, out double v))
            {
                if (!n.IsFloat && Math.Abs(v - Math.Round(v)) > 1e-6)
                {
                    throw new ApiException($"{name} is an integer", field);
                }

                return (name, n.IsFloat ? SvJsonNode.Float((float)v) : SvJsonNode.Integer((long)Math.Round(v)));
            }

            return (name, value);
        }

        return (key, value);
    }

    private static SvJsonObject Members(SvJsonNode node, string field) =>
        node as SvJsonObject ?? throw new ApiException("expected an object of name: value", field);

    /// <summary>select: sets (<c>mode</c> set, add, remove, clear) the editor selection after the document commits.</summary>
    private static void Select(ChangeContext ctx, SvJsonObject op)
    {
        string mode = ApiJson.GetString(op, "mode") ?? "set";
        if (mode is not ("set" or "add" or "remove" or "clear"))
        {
            throw new ApiException("mode is set, add, remove or clear", "mode");
        }

        ctx.SelectRequests.Add((mode, mode == "clear" ? [] : ctx.Targets(op)));
    }
}
