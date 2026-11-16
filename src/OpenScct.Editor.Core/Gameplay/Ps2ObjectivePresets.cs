using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Authoring presets lowered to the registered runtime classes, with explicit per-instance PS2 data.</summary>
public static class Ps2ObjectivePresets
{
    public const string Hack = "ps2.nd133-hack";
    public const string Vial = "ps2.vial";
    public const string VialMesh = "P2_Objectives_Vial.CTF.UnknownGlobalPS2_857191";
    public const string ConsoleMesh = "nd133_1mesh.nd133_mesh";
    private const string Materials = "P2_Objectives_ND133.UnknownGlobalPS2_856860.UnknownGlobalPS2_";
    private static readonly Lazy<IClassCatalog> s_previewClasses = new(Classes.ClassCatalog.GeneratedOnly);

    public static bool Is(Placeable entry) => entry.PresetId is Hack or Vial;

    public static Actor Preview(Placeable entry, string id = "preview")
    {
        var actor = new Actor(id, Is(entry) ? "SBase.SComputerObjectiveTrigger" : entry.Class);
        if (Is(entry)) Console(actor, s_previewClasses.Value);
        return actor;
    }

    private static SvJsonNode Asset(string path) => new SvJsonObject { { "$asset", SvJsonNode.String(path) } };
    private static SvJsonNode Ref(string id) => new SvJsonObject { { "$actor", SvJsonNode.String(id) } };
    private static SvJsonNode Refs(params string[] ids) => new SvJsonArray(ids.Select(Ref).ToArray());

    // All preset values pass the ordinary schema/coercion path. An explicit null
    // clears a PC class asset; the public change API's null means "remove override".
    private static void Set(Actor actor, IClassCatalog catalog, string name, SvJsonNode value)
    {
        var coercion = new PropCoercion(catalog, id => id);
        var warnings = new List<string>();
        if (value.Kind == SvJsonKind.Null)
        {
            coercion.Lookup(actor.Class, name, name, warnings);
            actor.Props.Set(name, SvJsonNode.Null);
        }
        else
        {
            var typed = coercion.Coerce(actor.Class, name, value, actor.Props, name, warnings);
            actor.Props.Set(typed.Name, typed.Value!);
        }
    }

    private static void Base(Actor actor, IClassCatalog catalog)
    {
        Set(actor, catalog, "bPS2Objective", SvJsonNode.Bool(true));
        Set(actor, catalog, "bNoDelete", SvJsonNode.Bool(false));
    }

    private static void Console(Actor actor, IClassCatalog catalog)
    {
        Base(actor, catalog);
        Set(actor, catalog, "Mesh", Asset(ConsoleMesh));
        Set(actor, catalog, "StaticMesh", SvJsonNode.Null);
        Set(actor, catalog, "DrawType", SvJsonNode.Integer(2));
        Set(actor, catalog, "UseRelativeLocation", SvJson.Parse("{\"X\":90.0,\"Y\":0.0,\"Z\":0.0}"));
        Set(actor, catalog, "UseRelativeRotation", SvJson.Parse("{\"Pitch\":0,\"Yaw\":32768,\"Roll\":0}"));
        foreach (var (name, suffix) in new[] { ("Ps2SkinActive", "856858"), ("Ps2SkinInactive", "856848"),
                     ("Ps2SkinHacked", "856854"), ("Ps2SkinKeyboard", "856859"), ("Ps2SkinKeyboardHacked", "856841") })
            Set(actor, catalog, name, Asset(Materials + suffix));
        foreach (var (name, sequence) in new[] { ("Ps2HackAnimation", "nd133_hack_ps2"), ("Ps2HackedAnimation", "nd133_keyboard_in_ps2"),
                     ("Ps2AlarmAnimation", "nd133_alarm_ps2"), ("Ps2CtfAnimation", "nd133_ctf_seq_ps2") })
            Set(actor, catalog, name, SvJsonNode.String(sequence));
        Set(actor, catalog, "Skins", new SvJsonArray(new[] { "858141", "858143", "858135", "856859" }.Select(s => Asset(Materials + s)).ToArray()));
    }

    public static void DropZone(Actor actor, IClassCatalog catalog)
    {
        Base(actor, catalog);
        Set(actor, catalog, "StaticMesh", SvJsonNode.Null);
        Set(actor, catalog, "InteractionClass", SvJsonNode.Null);
        Set(actor, catalog, "bHidden", SvJsonNode.Bool(true));
        Set(actor, catalog, "CollisionRadius", SvJsonNode.Float(100));
        Set(actor, catalog, "CollisionHeight", SvJsonNode.Float(100));
        Set(actor, catalog, "bCollideActors", SvJsonNode.Bool(true));
        Set(actor, catalog, "bBlockActors", SvJsonNode.Bool(false));
        Set(actor, catalog, "bBlockPlayers", SvJsonNode.Bool(false));
        Set(actor, catalog, "bBlockZeroExtentTraces", SvJsonNode.Bool(false));
        Set(actor, catalog, "PrePivot", SvJson.Parse("{\"X\":0.0,\"Y\":0.0,\"Z\":0.0}"));
        actor.PrePivot = Vec3.Zero;
    }

    private static void VialFields(Actor actor, IClassCatalog catalog, string console)
    {
        Base(actor, catalog);
        foreach (string name in new[] { "StaticMesh", "InitialStaticMesh", "Ps2SourceStaticMesh" }) Set(actor, catalog, name, Asset(VialMesh));
        Set(actor, catalog, "Ps2Computer", Ref(console));
        Set(actor, catalog, "Ps2InteractionDistance", SvJsonNode.Float(80));
        Set(actor, catalog, "LightHue", SvJsonNode.Integer(95));
        Set(actor, catalog, "LightSaturation", SvJsonNode.Integer(118));
        Set(actor, catalog, "bActorShadows", SvJsonNode.Bool(false));
    }

    /// <summary>The ordinary linked-prefab template used for editing the paired assembly.</summary>
    public static PrefabDefinition VialTemplate()
    {
        var console = new Actor("console", "SBase.SComputerObjectiveTrigger");
        var vial = new Actor("vial", "SBase.SFlag") { Location = new Vec3(0, 0, 25) };
        Console(console, s_previewClasses.Value);
        VialFields(vial, s_previewClasses.Value, "$console");
        vial.Props.Set("Ps2Computer", SvJsonNode.String("$console"));
        foreach (Actor actor in new[] { console, vial }) actor.Props.Set("TriggerName", SvJsonNode.String("Tube"));
        SvJsonObject Create(Actor actor)
        {
            var props = new SvJsonObject();
            foreach (var (key, value) in actor.Props)
                props.Add(key, value);
            return new SvJsonObject
            {
                { "op", SvJsonNode.String("create") }, { "id", SvJsonNode.String("$" + actor.Id) },
                { "class", SvJsonNode.String(actor.Class) }, { "at", ApiJson.Vec(actor.Location) }, { "props", props }
            };
        }
        return PrefabDefinition.FromJson(new SvJsonObject
        {
            { "format", SvJsonNode.String("osmap.prefab/1") }, { "name", SvJsonNode.String("ps2_vial") },
            { "title", SvJsonNode.String("PS2 vial and console") }, { "category", SvJsonNode.String("objectives") },
            { "summary", SvJsonNode.String("The original PS2 extraction tube and its animated console companion.") },
            { "ops", new SvJsonArray(new SvJsonNode[] { Create(console), Create(vial) }) }
        }, PrefabSource.Builtin, null);
    }

    /// <summary>Runtime opt-in clears the PC console mesh after normal API null/unset coercion.</summary>
    public static void Normalize(Actor actor, IClassCatalog? catalog)
    {
        if (actor.Props.GetBool("bPS2Objective") &&
            (catalog?.IsA(actor.Class, "SBase.SComputerObjectiveTrigger") ?? actor.Class == "SBase.SComputerObjectiveTrigger"))
            actor.Props.Set("StaticMesh", SvJsonNode.Null);
    }

    public static PlacedPlaceable Place(MapProject project, Placeable entry, Vec3 location, Rot rotation,
        IReadOnlyList<string> selected, IClassCatalog catalog)
    {
        if (Ps2ObjectiveAssets.Bundled is null)
            throw new InvalidOperationException("The PS2 objective models are missing. Keep the Editor's Data folder beside the application.");
        bool carry = entry.PresetId == Vial;
        int mode = (int)(carry ? EGameMode.GM_Extraction : EGameMode.GM_Neutralization);
        string modeName = carry ? "Extraction" : "Neutralization";
        Func<string, string> ids = GeShapes.IdSource(project);
        var actor = new Actor(ids(carry ? "SFlag" : "SComputerObjectiveTrigger"), entry.Class) { Location = location, Rotation = rotation };
        var added = new List<Actor> { actor };
        var edits = new List<IEditCommand>();
        var wired = new List<string>();
        var needs = new List<string>();
        string label = carry ? "Tube" : "ND133";
        if (carry)
        {
            var console = new Actor(ids("SComputerObjectiveTrigger"), "SBase.SComputerObjectiveTrigger") { Location = location, Rotation = rotation };
            Console(console, catalog);
            Set(console, catalog, "TriggerName", SvJsonNode.String(label));
            added.Add(console);
            actor.Location += new Vec3(0, 0, 25);
            VialFields(actor, catalog, console.Id);
            string group = actor.Id + "Console";
            for (int n = 1; project.Prefabs.Any(p => p.Id == group); n++) group = actor.Id + "Console" + n;
            actor.Editor.PrefabInstance = console.Editor.PrefabInstance = group;
            actor.Editor.PrefabPart = "vial"; console.Editor.PrefabPart = "console";
            edits.Add(PrefabInstancesCommand.Put(new PrefabInstance(group, "ps2_vial")
            {
                Location = location, Rotation = rotation, AnchorPart = "console", AnchorLocal = Vec3.Zero,
                Hash = PrefabStore.Find("ps2_vial", null)?.Hash
            }));
            needs.Add("a delivery zone: select this vial, then place a disk drop zone");
        }
        else
        {
            Console(actor, catalog);
            Set(actor, catalog, "CaptureTime", SvJsonNode.Integer(10));
            Set(actor, catalog, "DominationCaptureTime", SvJsonNode.Integer(10));
        }
        Set(actor, catalog, "TriggerName", SvJsonNode.String(label));

        List<Actor> chosen = selected.Select(project.FindActor).OfType<Actor>().ToList();
        var objectives = chosen.Where(a => a.Class == "SBase.SObjective").ToList();
        Actor? objective = objectives.Count == 1 ? objectives[0] : null;
        if (objective is null)
        {
            objective = new Actor(ids("SObjective"), "SBase.SObjective") { Location = location + new Vec3(0, 0, 80) };
            objective.Props.Set("Triggers", Refs(actor.Id));
            objective.Props.Set("ObjectiveName", SvJsonNode.String(label));
            added.Add(objective);
        }
        else edits.Add(Append(project, objective, "Triggers", actor.Id));
        bool SameMode(Actor candidate) => candidate.Class == "SBase.SMission" && candidate.Props.GetInt("GameMode") == mode;
        var missions = chosen.Where(SameMode).ToList();
        if (missions.Count == 0) missions = project.Actors.Where(SameMode).ToList();
        if (missions.Count == 1) edits.Add(Append(project, missions[0], "Objectives", objective.Id));
        else
        {
            var mission = new Actor(ids("SMission"), "SBase.SMission") { Location = location + new Vec3(0, 0, 144) };
            mission.Props.Set("GameMode", SvJsonNode.Integer(mode));
            mission.Props.Set("MinimumObjectives", SvJsonNode.Integer(1));
            mission.Props.Set("bChained", SvJsonNode.Bool(false));
            mission.Props.Set("Objectives", Refs(objective.Id));
            mission.Props.Set("Description", SvJsonNode.String(carry ? "Deliver the tube" : "Hack the ND133"));
            added.Add(mission);
        }
        edits.Add(new ProjectSettingsCommand("Enable " + modeName, p =>
        {
            if (!p.MapSettings.ContainsKey(modeName)) p.MapSettings[modeName] = p.MapSettings.GetValueOrDefault("Story")?.Clone() ?? new PropSet();
            p.Workspace.Play.Mode = modeName;
            p.Workspace.Play.Rules = "map";
        }));
        wired.Add($"{modeName} objective and activating mission; original PS2 model and behaviour");
        return new PlacedPlaceable(new CompositeCommand("Place " + entry.Title,
            [new AddObjectsCommand("Place " + entry.Title, actors: added), .. edits]), actor.Id, wired, needs);
    }

    private static ReplaceObjectsCommand Append(MapProject project, Actor actor, string property, string id) =>
        ReplaceObjectsCommand.Edit(project, [ObjectRef.Actor(actor.Id)], "Link " + id,
            actor: copy => copy.Props.Set(property, new SvJsonArray((copy.Props[property] as SvJsonArray ?? new SvJsonArray())
                .Concat(new[] { Ref(id) }).ToArray())));
}
