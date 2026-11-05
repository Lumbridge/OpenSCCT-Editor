using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose storeys</c> and <c>ose backups</c>: the storeys the storey filter steps through, and the rotating
/// save backups with restore.</summary>
internal static class WorkbenchCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("storeys", "storeys <project> [--json] [--show <storey>|all] [--duplicate <storey> [--below|--by <z>]] [--dry-run]",
            "Lists the map's storeys, lowest first: its own (workspace storeys) or the ones found from its floors, and the one the views " +
            "show. --show picks the storey the editor shows (a name or a number, 1 for the lowest; saved with the map). --duplicate copies " +
            "everything on a storey to a new storey on top of the highest one (--below: under the lowest; --by: that many units up) and saves.",
            Storeys),
        new("backups", "backups <project> [--restore <name|n>] [--json] | backups --keep <n>",
            "Lists the save backups, newest first; --restore brings one back (n = 1 is the newest; the current files become a backup). " +
            "--keep sets how many backups each save keeps, for you (the editor's Preferences show the same number).",
            Backups),
    ];

    private static Task<int> Storeys(CliArgs a)
    {
        EnsureServices();
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        if (a.Get("show") is { } show)
        {
            return Task.FromResult(Edit(folder, a, ApiJson.Obj(("op", ApiJson.Str("workspace")), ("shown", ApiJson.Str(show)))));
        }

        if (a.Get("duplicate") is { } storey)
        {
            SvJsonObject op = ApiJson.Obj(("op", ApiJson.Str("storey.duplicate")), ("storey", ApiJson.Str(storey)));
            if (a.Get("by") is { } by)
            {
                op.Add("by", ApiJson.Num(double.Parse(by, CultureInfo.InvariantCulture)));
            }

            if (a.Has("below"))
            {
                op.Add("below", ApiJson.Bool(true));
            }

            return Task.FromResult(Edit(folder, a, op));
        }

        MapProject project = ProjectStore.Load(folder);
        if (a.Has("json"))
        {
            PrintJson(MapQuery.StoreysJson(project, Path.GetFullPath(folder)), a);
            return Task.FromResult(0);
        }

        IReadOnlyList<Storey> storeys = Core.Geometry.Storeys.Of(project, Path.GetFullPath(folder));
        int shown = Core.Geometry.Storeys.Shown(storeys, project.Workspace.ShownStorey);
        Console.WriteLine(project.Workspace.Storeys.Count > 0 ? "The map's own storeys:" : $"Detected storeys ({storeys.Count}):");
        for (int i = 0; i < storeys.Count; i++)
        {
            Storey s = storeys[i];
            string top = double.IsFinite(s.Top) ? Inv(s.Top) : "open";
            string floor = s.Floor is double f ? $", floor {Inv(f)}" : "";
            Console.WriteLine($"  {s.Name}: {Inv(s.Bottom)} to {top}{floor}{(i == shown ? " (shown)" : "")}");
        }

        return Task.FromResult(0);
    }

    /// <summary>Runs one op on the project and saves it unless --dry-run; prints the change result.</summary>
    private static int Edit(string folder, CliArgs a, SvJsonObject op)
    {
        EditorSession session = EditorSession.Open(folder);
        bool dry = a.Has("dry-run");
        SvJsonObject doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("ops", ApiJson.Arr([op])),
            ("label", ApiJson.Str($"ose {ApiJson.GetString(op, "op")}")));
        ChangeResult result = ChangeApplier.Apply(session, doc, dryRun: dry, author: a.Get("author") ?? "ose");
        if (result.Ok && !dry && session.IsDirty)
        {
            session.Save();
            result.Json.Add("saved", ApiJson.Str(session.Folder!));
            foreach (string warning in session.SaveWarnings)
            {
                Console.Error.WriteLine($"ose storeys: {warning}");
            }
        }

        PrintJson(result.Json, a);
        return result.Ok ? 0 : 1;
    }

    private static Task<int> Backups(CliArgs a)
    {
        if (a.Get("keep") is { } keep)
        {
            ProjectBackups.Kept = int.TryParse(keep, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? n : throw new ArgumentException("--keep takes a whole number (0 keeps none)");
            Console.WriteLine($"Each save keeps {ProjectBackups.Kept} backups ({ProjectBackups.SettingsFile}).");
            if (a.Positional(0) is null)
            {
                return Task.FromResult(0);
            }
        }

        string folder = Path.GetFullPath(a.Positional(0) ?? throw new ArgumentException("give the project folder"));
        if (!ProjectStore.IsProject(folder))
        {
            throw new ArgumentException($"{folder} is not a map project");
        }

        if (a.Get("restore") is { } which)
        {
            ProjectBackup backup = ProjectBackups.Find(folder, which) ?? throw new ArgumentException($"no backup '{which}' (ose backups {a.Positional(0)} lists them)");
            MapProject restored = ProjectStore.Load(backup.Folder);
            // The restore keeps the files it replaces, so it can be undone.
            foreach (string warning in ProjectStore.Save(restored, folder, ProjectBackups.Kept, restoreFrom: backup.Folder))
            {
                Console.Error.WriteLine($"ose backups: {warning}");
            }

            Console.WriteLine($"Restored {backup.Name} into {folder}; the files it replaced are the newest backup.");
            return Task.FromResult(0);
        }

        if (a.Has("json"))
        {
            PrintJson(ProjectBackups.ToJson(folder), a);
            return Task.FromResult(0);
        }

        IReadOnlyList<ProjectBackup> all = ProjectBackups.List(folder);
        Console.WriteLine(all.Count == 0
            ? "No backups yet (each save keeps the files it replaces)."
            : $"{all.Count} backups, newest first (each save keeps {ProjectBackups.Kept}):");
        for (int i = 0; i < all.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {all[i].Name}  (saved {all[i].SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        }

        return Task.FromResult(0);
    }
}
