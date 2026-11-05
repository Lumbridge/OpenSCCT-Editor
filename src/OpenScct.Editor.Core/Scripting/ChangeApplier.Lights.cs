using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc.Lights;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// The <c>lights.recover</c> op: runs the .sdc light recovery again on a map imported from a cooked copy
/// (<see cref="LostLightRecovery"/>): <c>mode</c> (<c>off</c>, <c>exact</c> or <c>full</c>, the default), <c>twins</c>
/// (more folders of editor copies), <c>fitSeconds</c> (a time limit on the fit; none by default, so the result does
/// not depend on the machine), <c>ignoreTwins</c>, <c>forceFit</c> (fit past the fit gate). Lights an earlier run
/// added are replaced whole; the whole run is one undo step. It runs on the caller's thread and can take minutes on a
/// big map (a live editor waits for it, and a dry run or proposal preview runs it too).
/// </summary>
public static partial class ChangeApplier
{
    private static string[] LightRecoverKeys => ["mode", "twins", "fitSeconds", "ignoreTwins", "forceFit"];

    private static void LightRecoverOp(ChangeContext ctx, SvJsonObject op)
    {
        LightRecoveryMode mode = ApiJson.GetString(op, "mode")?.ToLowerInvariant() switch
        {
            null or "full" => LightRecoveryMode.Full,
            "exact" => LightRecoveryMode.Exact,
            "off" => LightRecoveryMode.Off,
            var m => throw new ApiException($"mode '{m}' is off, exact or full", "mode"),
        };
        var options = new LightRecoveryOptions
        {
            Mode = mode,
            TwinFolders = ApiJson.StringList(op["twins"], "twins") ?? [],
            IgnoreTwins = ApiJson.GetBool(op, "ignoreTwins", false),
            ForceFit = ApiJson.GetBool(op, "forceFit", false),
            Fit = new LightFitSettings
            {
                TimeLimit = op["fitSeconds"] is { } t ? TimeSpan.FromSeconds(ApiJson.RequireNumber(t, "fitSeconds")) : null,
            },
        };

        // The recovery runs on a copy; the differences go through the undo stack.
        MapProject work = ProjectSnapshot.Take(ctx.Project);
        var findings = new List<Finding>();
        try
        {
            if (LostLightRecovery.Rerun(work, ctx.Folder, options, findings) is null)
            {
                throw new ApiException("the map's .sdc is an editor copy: it holds every light, nothing was lost", "op");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            throw new ApiException(ex.Message, "op");
        }

        var after = work.Actors.Where(a => BakedContent.Recovered(a) is not null).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        List<ObjectRef> gone = ctx.Project.Actors.Where(a => BakedContent.Recovered(a) is not null && !after.ContainsKey(a.Id))
            .Select(a => ObjectRef.Actor(a.Id)).ToList();
        if (gone.Count > 0)
        {
            ctx.Delete(gone);
        }

        // A rerun gives its lights the ids an earlier run used (RecoveredLight0...), so a light with a live id is replaced
        // whole: where it is and what it is, not only its marks.
        var changed = new List<ObjectRef>();
        var added = new List<Actor>();
        foreach (Actor a in after.Values)
        {
            if (ctx.Project.FindActor(a.Id) is { } live)
            {
                if (!SameRecoveredLight(live, a))
                {
                    changed.Add(ObjectRef.Actor(a.Id));
                }
            }
            else
            {
                added.Add(a);
            }
        }

        if (changed.Count > 0)
        {
            ctx.Edit(changed, "Replace recovered lights", actor: a =>
            {
                Actor from = after[a.Id];
                a.Class = from.Class;
                a.Location = from.Location;
                a.Rotation = from.Rotation;
                a.DrawScale = from.DrawScale;
                a.DrawScale3D = from.DrawScale3D;
                a.PrePivot = from.PrePivot;
                a.Props = from.Props.Clone();
                a.Source = from.Source;
                a.Editor.Folder = from.Editor.Folder;
            });
        }

        if (added.Count > 0)
        {
            ctx.Add("Recover lights", actors: added);
        }

        double? radius = work.Lighting.BakeDefaultLightRadius;
        SvJsonObject extra = work.Extra;
        ctx.Run(new ProjectSettingsCommand("Light recovery report", p =>
        {
            p.Lighting.BakeDefaultLightRadius = radius;
            p.Extra = extra;
        }), touchesBrushes: false);
        foreach (Finding f in findings.Where(f => f.Severity <= Severity.Warning))
        {
            ctx.Warn("lights", f.Message);
        }
    }

    private static bool SameRecoveredLight(Actor a, Actor b) =>
        a.Class == b.Class && a.Location == b.Location && a.Rotation == b.Rotation && a.DrawScale == b.DrawScale
        && a.DrawScale3D == b.DrawScale3D && a.PrePivot == b.PrePivot && a.Source == b.Source && a.Editor.Folder == b.Editor.Folder
        && CanonicalJson.ToCanonicalString(a.Props.ToJson()) == CanonicalJson.ToCanonicalString(b.Props.ToJson());
}
