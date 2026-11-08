using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Caps of the original engine. They are errors when exporting to the original game; for OpenSCCT the ones it keeps
/// are warnings and the rest information. Mesh collision caps (5512 triangles, 32767 nodes, hull faces) need the
/// compiled collision and are deep rules (<see cref="DeepRules"/>).
/// </summary>
internal sealed class LimitRules : MapRule
{
    public const string MapName = "limit.map-name";
    public const string Zones = "limit.zones";
    public const string MoverKeys = "limit.mover-keys";
    public const string TriggerLights = "limit.trigger-lights";
    public const string BspPolys = "limit.bsp-polys";
    public const string Ascii = "limit.ascii";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(MapName, Severity.Error, $"The map id is not {Limits.MapNameLength} ASCII letters or digits (4 plus the mode letter)") { OriginalOnly = true },
        new(Zones, Severity.Error, $"More than {Limits.MaxZones} zones (ZoneInfo actors plus the default zone)"),
        new(MoverKeys, Severity.Error, $"Original-game export supports at most {Limits.MaxMoverKeys} mover stops") { OriginalOnly = true },
        new(TriggerLights, Severity.Warning, $"More than {Limits.TriggerLightWarning} in-game lights (STriggerLight); the raised cap is {Limits.TriggerLightCap}"),
        new(BspPolys, Severity.Error, $"More than {Limits.BspNodes} world polygons after CSG: the BSP cannot index them") { OriginalOnly = true },
        new(Ascii, Severity.Error, "A non-ASCII id, Tag, Event or material name") { OriginalOnly = true },
    ];

    public override void Run(CheckContext c)
    {
        MapProject p = c.Project;
        if (c.Original && !Limits.IsMapName(p.Id))
        {
            c.Report(Severity.Error, MapName, $"Map id '{p.Id}' must be exactly {Limits.MapNameLength} letters or digits for the original game", null);
        }

        int zones = p.Actors.Count(a => c.IsA(a, "Engine.ZoneInfo")) + 1;
        if (zones > Limits.MaxZones)
        {
            c.Report(c.ForTarget(Severity.Info), Zones, $"{zones} zones (ZoneInfo actors + the default zone); the original engine has {Limits.MaxZones}", null);
        }

        foreach (Actor a in p.Actors.Where(a => c.IsA(a, "Engine.Mover")))
        {
            int keys = Math.Max(c.Int(a, "NumKeys"), Math.Max(MaxElement(a.Props["KeyPos"]), MaxElement(a.Props["KeyRot"])) + 1);
            if (c.Original && keys > Limits.MaxMoverKeys)
            {
                c.Report(c.ForTarget(Severity.Warning), MoverKeys, $"{a.Id} has {keys} keys; movers have at most {Limits.MaxMoverKeys}", a.Location,
                    ObjectRef.Actor(a.Id));
            }
        }

        int lights = p.Actors.Count(a => c.IsA(a, "SBase.STriggerLight"));
        if (lights > Limits.TriggerLightCap && c.Original)
        {
            c.Report(Severity.Error, TriggerLights, $"{lights} in-game lights (STriggerLight); the game holds {Limits.TriggerLightCap}", null);
        }
        else if (lights > Limits.TriggerLightWarning)
        {
            c.Report(Severity.Warning, TriggerLights, $"{lights} in-game lights (STriggerLight); more than {Limits.TriggerLightWarning} needs the raised cap", null);
        }

        if (c.Original && EditorServices.Preview is { } preview && c.Wants(BspPolys))
        {
            int polys = preview.Build(p).Count;
            if (polys > Limits.BspNodes)
            {
                c.Report(Severity.Error, BspPolys, $"{polys} world polygons; the BSP indexes at most {Limits.BspNodes} nodes", null);
            }
        }

        if (c.Original)
        {
            CheckAscii(c);
        }
    }

    private static void CheckAscii(CheckContext c)
    {
        foreach (ObjectRef r in c.Project.AllRefs().Where(r => !IsAscii(r.Id)))
        {
            c.Report(Severity.Error, Ascii, $"id '{r.Id}' is not ASCII", null, r);
        }

        foreach (Actor a in c.Project.Actors)
        {
            foreach (string prop in new[] { "Tag", "Event" })
            {
                if (a.Props.GetString(prop) is { } v && !IsAscii(v))
                {
                    c.Report(Severity.Error, Ascii, $"{a.Id}.{prop} '{v}' is not ASCII", a.Location, ObjectRef.Actor(a.Id));
                }
            }
        }

        foreach (Brush b in c.Project.Brushes)
        {
            if (b.Shape.Faces.Select(f => f.Material).FirstOrDefault(m => m is not null && !IsAscii(m)) is { } m)
            {
                c.Report(Severity.Error, Ascii, $"{b.Id} uses material '{m}' with a non-ASCII name", null, ObjectRef.ForBrush(b));
            }
        }
    }

    /// <summary>The highest element index of a static array value, or -1.</summary>
    private static int MaxElement(SvJsonNode? node) =>
        node is SvJsonObject o && o["$elements"] is SvJsonObject e
            ? e.Select(kv => int.TryParse(kv.Key, out int i) ? i : -1).DefaultIfEmpty(-1).Max()
            : -1;

    private static bool IsAscii(string s) => s.All(ch => ch is >= ' ' and <= '~');
}
