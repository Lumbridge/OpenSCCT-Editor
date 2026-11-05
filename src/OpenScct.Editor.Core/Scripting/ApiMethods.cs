using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>A method of the scripting API: its name, its title, what it does, and its input schema (JSON Schema).</summary>
public sealed record ApiMethod(string Name, string Title, string Description, string Schema)
{
    public SvJsonNode SchemaJson => ApiJson.Parse(Schema, Name + " schema");
}

/// <summary>The scripting API's methods, described once for the JSON-lines <c>describe</c> call.</summary>
public static class ApiMethods
{
    public const string ApplyHelp =
        "Edit the map with a change document. Ops run in order as ONE undo step; an error rolls everything back and names " +
        "the op index and field. dryRun:true returns the diff and changes nothing.\n" +
        "IDS: an op's id may be \"$name\" (a symbol: a fresh id is made, later ops use \"$name\"), a new literal id, or an " +
        "existing id to edit. Targets: id | ids | where (a find filter, e.g. {\"class\":\"SCamera\"}).\n" +
        "POINTS: [x,y,z] | \"x,y,z\" | \"Name@anchor+dx,dy,dz\". Anchors: center, origin, top, bottom, xmin, xmax, ymin, ymax, " +
        "joined with _ (xmax_bottom). Unreal units: X forward, Y right, Z up, 16 = grid, a player is 175 tall.\n" +
        "PLACEMENT (create/move/update): at, offset, near (+dist), pivot (which anchor of the placed object lands on the " +
        "point, e.g. \"xmin_bottom\"), wall (\"nearest\"|\"+x\"|\"-x\"|\"+y\"|\"-y\": back on the wall, an actor faces out; gap), " +
        "on (true = drop to the floor below the point; an id = onto or into that object), lift. Placement by reference finds floor heights, so a script never needs a floor z. " +
        "rot/rotDeg apply before placement, face after.\n" +
        "OPS: create {class, props} = actor | {brush: SHAPE, csg: \"subtract\" (default) | \"add\", flags, mat, mats:{top,bottom,sides}} " +
        "= brush | {ge: LG|ZL|PIPE|LADDER|HOH|POLE|FENCE, points, mask} = GE; plus rot [pitch,yaw,roll] (65536 = one turn) | " +
        "rotDeg | face (a point to look at); scale; layer; folder; note; count + step (a row of copies).\n" +
        "SHAPE: \"cube\" | {size:[x,y,z]} | {min:[..],max:[..]} (world corners; also places it) | {kind, ...builder params} | " +
        "{faces:[[[x,y,z],...],...]}.\n" +
        "update {set:{Prop: value|null}, unset:[..], rename, shape, csg, flags, mat, points, mask, placement, rot, layer...}; " +
        "delete; move {by:[dx,dy,dz]} | {to: POINT} (+ on, wall); rotate {by|byDeg|to|toDeg|face, around: \"center\"|POINT}; " +
        "scale {by|to}; duplicate {by, count, as}; brush / ge (create, or edit when id exists); " +
        "surface {id|ids|where (brushes), faces: [indexes]|\"all\"|\"walls\"|\"floors\"|\"ceilings\", mat, flags, clearFlags, lightmap, " +
        "align: default|floor|wall|face|fit|cylinder, scale, rotate, pan, flip} (face edits); " +
        "link {from, to, tag?} (from.Event = to.Tag; an SAlarm adds to its Events or lock lists) | {from, prop, to} (actor reference) | clear:true; " +
        "layer {name, color, visible, locked, rename, delete, add:[ids], where}; folder {path, ids|where, rename}; " +
        "settings {levelInfo, mapSettings: {Mode|\"*\": {..}}, mapInfo, title, mapId, lighting {mode: quality|faithful, density, samples, " +
        "lightSize, sunSize, bounce, bounceSamples, atlasSize, relightImported, bakeDefaultLightRadius}, baked {keepRender, keepCollision} (an imported map: a layer " +
        "not kept is built from the brushes)}; " +
        "identity {name, family, variant, description, dossier {when, where, summary}, authors [..], version, language, players " +
        "(recommended lobby size), tags [..], floors \"auto\"|[z..], places [{number, name, at, floor}] | place {number, name, at, " +
        "floor, delete}, pictures {menu, loading {spy:[..], merc:[..]}, briefing, banner}: each {from: \"file\", file, focus [x,y]} | " +
        "{from: \"camera\", bookmark, look: plain|nightvision|thermal, fov} | {from: \"plan\"} | null} (the map's name, pictures " +
        "and briefing places; ose mapshot renders camera pictures); select {ids|where, mode}; " +
        "workspace {grid, rotationGrid, camera {at, rot|rotDeg|look}, play {side, mode, bots, rules, players}, movement {stepHeight, " +
        "walkableSlope, standHeight, spyCrouchHeight, mercCrouchHeight, radius, spyRunSpeed, mercRunSpeed}} (movement also sizes " +
        "blockout pieces and Map Check); bookmark {name, at, rot|rotDeg|look, note, rename, delete}; reference {path, view, origin, " +
        "unitsPerPixel, opacity, delete}; conveyor {id (creates, or edits that conveyor), points: [POINT | {at: POINT, radius}] " +
        "(on the riding surface; a radius rounds that corner), floor:true (drop each point to the floor, lifted for the look), " +
        "look: frame|driver|treadmill|escalator, walls: both|left|right|none, width, speed (uu/s, negative runs backwards), on, " +
        "loop, trigger: toggle|on|off|reverse (what an event does), items: [mesh paths], props} (a native Svm.Conveyor).\n" +
        "PREFABS (query prefabs lists them): prefab.place {name, params, placement (the pivot lands on the point, floor or wall; " +
        "its front, +X, faces out of a wall), rotDeg, id: \"$door\" binds $door (the instance) and $door.part for each part}; " +
        "prefab.update {id|ids (instances or their parts) | name (every instance of a prefab) | all; params (one instance; null = " +
        "default)}: rebuilds from the prefab, keeping place and part ids; prefab.break {id|ids|name|all, delete}; prefab.save {name, " +
        "id|ids|where, pivot, rotDeg (the way the group faces), title, summary, to: project|library, overwrite, link, as} " +
        "(the file is written when the document commits).\n" +
        "GAMEPLAY WORKBENCHES (queries mover, magic, camnets, propsets, tips, zone, modes show the same): mover {id, keys: " +
        "[{at: offset from base, rot}] (1 to 8), addKey: true|POINT, deleteKey, startKey, moveTime, stayOpenTime, delayTime, " +
        "delayTimeLastKey, otherTime (seconds), eased, state (InitialState), showKey (draw it at a key: a later move/rotate of " +
        "the mover places that key)}; magic {id (new when absent or a new $symbol: at, " +
        "tag), groups: [{actions: [{event, delay, type: trigger|untrigger|triggerUntrigger|untriggerTrigger, on}], repeat, " +
        "sequence}], stopActor}; camnet {order: [ids], loop} (links whole networks in that order) | {remove: [ids]}; camera " +
        "{id, name, pitch: [below, above], yaw: [below, above]} (rotator units; the game limits only when both ends are " +
        "non-zero); props.paste {from: actor | props: osmap.props/1 document, id|ids|where, keys (default: all but identity, " +
        "placement and actor links)}; propset {set: Class.Name, id|ids|where}; tip {id (new when absent: at, spy), merc, " +
        "radius, height, marker (help marker 100 below, true), mesh (default: the map's marker mesh, else the stock " +
        "help_path_INFO), endsGame, onlyWithHelp, stay, endGameDelay}; zone.action {mission, add: actor id, type, delay | " +
        "message: {spy, merc, seconds}, delay | set: index, delay, type | remove: index} (through the mission's own " +
        "SMagicEvent relay; actors already reached on completion are refused).\n" +
        "MATCH SIZE (query match previews it): match {id|ids|where, players: [min, max] (0 = no limit; \"any\" clears), " +
        "outside: \"hidden\"|{key: n} (movers; default key 0), inside: \"normal\"|\"hidden\"|{key: n} (a mover is held exactly at key n; an open mover uses its last key), " +
        "win: [1v1..12v12 objective counts] (missions; 0 keeps the mission's own), clear}; the player count is everyone in the " +
        "match, bots included. outfits {team: spy|merc, body, head (PNG paths), model: {glb: path}|{stock: \"SPerso.ATT_01\"}, " +
        "goggles {bone, offset}, cosmetics: map|player|none, clear}; null removes a member; files outside the map are copied " +
        "into assets/outfits/ when the document commits.\n" +
        "VALUES follow the class schema (query schema): numbers, strings, [x,y,z] vectors, enum value names, actor ids for references.";

    public const string QueryHelp =
        "Read the map. {q: KIND, ...}; {queries:[...]} runs several in one call.\n" +
        "find / count: filters kind (actor|brush|ge), ids, class (subclasses too unless exact:true), tag, event, near (POINT) + " +
        "radius, box [[min],[max]], layer, folder, text, has [props], props {Prop: value}, csg, author; fields [..] " +
        "(default id,class,loc; any of id kind class short loc rot scale tag event props layer folder note author bounds size " +
        "center csg flags builder faces mats shape points mask refs match, or a property name), rows:true (table form, smaller output), limit (100), offset, sort; count takes by: class|kind|layer|folder|author.\n" +
        "get {ids, fields}; summary; schema {class, text, all, own}; classes {under, text, all}; check {target: openscct|original, deep} (rows end with a fix {label, changes} or null; deep compiles the map for the deep rules); " +
        "edges {id?, depth} (Event->Tag and actor-reference wiring, unheard events); raycast {from, to | dir, max}; " +
        "pointInfo {at, radius} (solid?, floor, ceiling, walls, nearby actors); history; identity (name, dossier, authors, " +
        "players, tags, places, picture recipes); importReport (what the import brought in, cooked out, lifted; the import's " +
        "own findings are in ose import --json); workspace (grid, camera, bookmarks, " +
        "references, play options, movement limits, layers); prefabs {name?} (the prefabs the map can place and its instances; " +
        "name: one prefab in full); match {players} (actors with player ranges, the counts where the layout changes, problems, " +
        "and the layout at that count: missions, objectives left out, hidden actors, held movers); outfits (the teams' outfits " +
        "and the model check); assets {kind: material|mesh|texture|sound, text (matches English glosses of French names too), " +
        "origin, limit} (materials and meshes a map can use); asset {path} (details, used-in maps, usages here). " +
        "mover {id} (keys, times, one activation's timeline, triggers); magic {id, play: [{t, trigger, fromStopActor}]} (groups " +
        "and the firings the game would make); camnets; propsets {class|id}; tips; zone {id?} (mission on-complete actions); " +
        "modes {mode?} (game modes with their own actors; with mode, the ids).\n" +
        "Analyses of the compiled map (OpenSCCT's own loader, light model and bot navigation; compiled on first use, again " +
        "after edits): stealth {lights: start|blackout|lit|switch:ID, off [light ids], on [ids], stance: crouch|stand, " +
        "night (false: night vision), spacing (64), mercSight (true), near + radius (floors from 400 below to 200 above it), cells:true (rows x y z stand crouch band merc torch back albedo silhouette), limit} " +
        "(how dark the floors spies reach are, hidden/partial/exposed, merc tracker and torch reach, the background a merc sees behind the spy and silhouettes, per objective); " +
        "reach {drops: survivable|unhurt|bot, mask: N|all, limit} (spy and merc reach from their starts, spy-only areas, " +
        "unreachable objectives and switches by the game's use test, doors and lifts each side opened or found shut); route {from, to, side: spy|merc, drops, mask} (can that side get from A to B: " +
        "the legs, GEs, doors and lifts used, or the closest point it gets to and the doors or lifts that would let it through); nav {side, drops (bot), mask, near + radius, limit} " +
        "(the bots' graph: nodes, edges by kind, GE links). Reach and route: a shut door blocks unless something the side reaches opens it, a lift carries it once it can work it (the game's SLift by its call switches); the bots' nav graph takes doors as open (bots open them as they meet them) and has no lifts. " +
        "props filters compare effective values.";

    /// <summary>A JSON array of names, for the schemas' enums (built from the registered ops and queries so they stay in step).</summary>
    private static string EnumJson(IEnumerable<string> names) => "[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]";

    public static readonly IReadOnlyList<ApiMethod> All =
    [
        new("apply", "Apply changes", ApplyHelp,
            """{"type":"object","properties":{"ops":{"type":"array","items":{"type":"object","properties":{"op":{"type":"string","enum":""" + EnumJson(ChangeApplier.OpNames) + """}},"required":["op"]}},"dryRun":{"type":"boolean"},"author":{"type":"string"},"label":{"type":"string","description":"undo step name and provenance label"},"save":{"type":"boolean","description":"save the project after applying"}},"required":["ops"]}"""),
        new("query", "Query the map", QueryHelp,
            """{"type":"object","properties":{"q":{"type":"string","enum":""" + EnumJson(MapQuery.Names) + """},"queries":{"type":"array","items":{"type":"object"}},"fields":{"type":"array","items":{"type":"string"}},"rows":{"type":"boolean"},"limit":{"type":"integer"},"class":{},"ids":{},"near":{},"radius":{"type":"number"},"name":{"type":"string"},"path":{"type":"string"},"kind":{"type":"string"},"text":{"type":"string"},"players":{"type":"integer"},"mode":{"type":"string"}}}"""),
        new("summary", "Map summary",
            "A short overview of the whole map: counts, bounds, actors by class, team starts, objectives, GEs, layers, modes, top problems. Usually the first call.",
            """{"type":"object","properties":{"classes":{"type":"integer","description":"how many classes to list (20)"}}}"""),
        new("open", "Open a map",
            "Opens an .osmap project folder (or a map an importer reads, unsaved) and returns its summary. A cooked .sdc has its brushes recovered from the compiled BSP (brushes: merge, cells or off). Refuses while the open map has unsaved changes unless discard:true.",
            """{"type":"object","properties":{"path":{"type":"string"},"brushes":{"type":"string","enum":["merge","cells","off"],"description":"brush recovery of a cooked .sdc import (merge)"},"discard":{"type":"boolean","description":"drop unsaved changes to the open map"}},"required":["path"]}"""),
        new("new", "New map",
            "Creates a map (by default one carved 1024x1024x512 room with a spy start, a merc start and a light); saves it when path is given.",
            """{"type":"object","properties":{"path":{"type":"string","description":"the .osmap folder to create"},"id":{"type":"string","description":"map code; the original game needs 5 characters"},"title":{"type":"string"},"starter":{"type":"boolean","description":"add the starter room (true)"},"overwrite":{"type":"boolean"},"discard":{"type":"boolean","description":"drop unsaved changes to the open map"}}}"""),
        new("save", "Save", "Saves the project (to path, or where it was opened).",
            """{"type":"object","properties":{"path":{"type":"string"}}}"""),
        new("undo", "Undo", "Undoes steps (an applied change document is one step).",
            """{"type":"object","properties":{"steps":{"type":"integer","minimum":1}}}"""),
        new("redo", "Redo", "Redoes undone steps.",
            """{"type":"object","properties":{"steps":{"type":"integer","minimum":1}}}"""),
        new("build", "Build", "Compiles the map to an SVMap folder for OpenSCCT and lists the findings.",
            """{"type":"object","properties":{"out":{"type":"string","description":"output folder (default: build/<id> next to the project)"},"fast":{"type":"boolean","description":"quick lighting: a quarter of the lightmap density and no shadows"}}}"""),
        new("export", "Export for the original game",
            "Exports the open map for the original game after the round-trip lint, and lists the findings. format original (the default) writes "
            + "a folder for RE+: the T3D, the actors of the map's own classes and their sources, the map's own textures, meshes and sounds with a "
            + "script that imports them, the map's texts (.int) and a README of the steps in RE+. format t3d writes the T3D alone (for the stock "
            + "editor's File > Import); with only, just those objects in the Edit > Copy form. format sdc writes a map package natively: layout "
            + "editor, the copy for Packages\\MapsEd with brushes, the default; or cooked, the copy the game loads from Packages\\Maps. An .sdc is "
            + "read back and checked; a map imported from an .sdc keeps that file's objects it does not edit, and the findings say what differs "
            + "from it. ok is false when the lint found errors (the files are written anyway).",
            """{"type":"object","properties":{"format":{"type":"string","enum":["original","t3d","sdc"]},"out":{"type":"string","description":"the folder (original) or file (t3d, sdc) to write; default export/<id>, export/<id>.t3d or export/<id>.sdc next to the project"},"only":{"type":"array","items":{"type":"string"},"description":"t3d only: the object ids to write"},"layout":{"type":"string","enum":["editor","cooked"],"description":"sdc only"},"game":{"type":"string","description":"sdc: the original install new materials and meshes are looked up in"}}}"""),
        new("playtest", "Headless test round",
            "Compiles the map and plays a round of it in OpenSCCT with no window: whether it loaded, the errors and warnings the game printed, " +
            "where the player spawned and where each bot stood, with the map's starts. at spawns the player there instead of a start (\"camera\": " +
            "the editor's 3D view, which the open editor keeps current; a bookmark's name or number; or X,Y,Z[,Yaw[,Pitch]]; not with route or " +
            "client). Ends once every bot has reported (after 10 s of play) or after seconds (40). Takes half a minute or so. route: a route bot of side walks those stops in order (floor " +
            "points, or object ids reached where the game's use test lets it use them) with the side's own navigation and door handling, " +
            "while you stand by on the other side; no other bots unless bots is given; from starts it at that player start; seconds defaults " +
            "to 30 plus 40 a stop; lifts are not ridden. The report says each stop reached or where it got stuck (routeStops, routeReached, " +
            "routeStuck); ok is false unless every stop was reached. client:true plays it online instead (no bots, no route): this game hosts " +
            "with the lobby's rules for mode and a second headless game joins on the other side; peers says what each loaded and which pawns " +
            "each one's world held, and the client fails when its world does not match the host's.",
            """{"type":"object","properties":{"side":{"type":"string","enum":["spy","merc"]},"mode":{"type":"string"},"rules":{"type":"string"},"bots":{"type":"integer","minimum":0,"maximum":15,"description":"server bots (the map's Play setting, else 2)"},"difficulty":{"type":"string","enum":["easy","normal","hard","expert"]},"players":{"type":"integer","minimum":0,"maximum":24,"description":"the player count the match is laid out for (the map's Play setting; 0 counts the bots and you)"},"at":{"type":"string","description":"spawn the player here once: camera, a bookmark, or X,Y,Z[,Yaw[,Pitch]] (Unreal units and rotation units); not with route or client"},"seconds":{"type":"number","minimum":5,"maximum":600},"fast":{"type":"boolean","description":"quick lighting when compiling (true)"},"out":{"type":"string"},"output":{"type":"boolean","description":"include every line the game printed"},"route":{"type":"array","description":"stops ([x,y,z] floor points or object ids) a route bot walks in order"},"from":{"type":"string","description":"the player start the route bot starts at"},"client":{"type":"boolean","description":"host the round and join it from a second headless game"}}}"""),
        new("check", "Map check", "Runs Map Check: findings with codes, messages, the ids to fix and, where a rule knows a safe one, a fix (a change document to apply). deep:true also compiles the map and runs the deep rules (zone leaks, actors in the void, floating meshes, mesh collision caps, reachability).",
            """{"type":"object","properties":{"target":{"type":"string","enum":["openscct","original"]},"deep":{"type":"boolean","description":"compile the map and run the deep rules too"},"only":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer"}}}"""),
        new("describe", "Describe", "Lists the methods, ops and queries.", """{"type":"object","properties":{}}"""),
    ];

    public static SvJsonObject Describe() => ApiJson.Obj(
        ("format", ApiJson.Str(ChangeApplier.Format)),
        ("methods", ApiJson.Arr(All.Select(m => (SvJsonNode)ApiJson.Arr([ApiJson.Str(m.Name), ApiJson.Str(m.Title)])))),
        ("ops", ApiJson.Strings(ChangeApplier.OpNames)),
        ("queries", ApiJson.Strings(MapQuery.Names)),
        ("apply", ApiJson.Str(ApplyHelp)),
        ("query", ApiJson.Str(QueryHelp)));
}
