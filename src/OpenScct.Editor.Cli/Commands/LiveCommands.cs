using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>Live editing from the command line: <c>ose live</c> lists the running editors, and
/// <c>ose serve --live</c> (see <see cref="Connect"/>) edits the map open in one of them.</summary>
internal static class LiveCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("live", "live [--json]",
            "Lists the running editors that take live connections (ose serve --live connects to the newest; " +
            "--pid picks one).", ListAsync),
    ];

    /// <summary>Connects to the running editor named by <c>--pid</c>, or the newest one.</summary>
    public static LiveClient Connect(CliArgs a, string client)
    {
        if (a.Get("live") is { } value && value != "true")
        {
            throw new ApiException($"--live connects to the map open in the editor, so give no project ('{value}')", "live");
        }

        if (a.Positional(0) is { } project)
        {
            throw new ApiException($"--live connects to the map open in the editor, so give no project ('{project}')", "live");
        }

        int? pid = a.Has("pid") ? a.GetInt("pid", 0) : null;
        LiveClient live = LiveClient.Connect(pid, client);
        Console.Error.WriteLine($"ose: connected to the editor (pid {live.Instance.Pid}, map {live.Instance.Map ?? "?"})");
        return live;
    }

    private static Task<int> ListAsync(CliArgs a)
    {
        List<LiveInstance> running = LiveDiscovery.Running();
        if (a.Has("json"))
        {
            Console.WriteLine(ApiJson.Write(ApiJson.Arr(running.Select(i => (SvJsonNode)ApiJson.Obj(
                ("pid", SvJsonNode.Integer(i.Pid)),
                ("port", SvJsonNode.Integer(i.Port)),
                ("map", i.Map is null ? null : ApiJson.Str(i.Map)),
                ("folder", i.Folder is null ? null : ApiJson.Str(i.Folder)),
                ("version", ApiJson.Str(i.Version)),
                ("started", ApiJson.Str(i.Started.ToString("o", CultureInfo.InvariantCulture))))))));
            return Task.FromResult(0);
        }

        if (running.Count == 0)
        {
            Console.WriteLine("No editor is running with live editing on.");
            return Task.FromResult(1);
        }

        foreach (LiveInstance i in running)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"pid {i.Pid}  port {i.Port}  map {i.Map ?? "?"}  {i.Folder ?? "(unsaved)"}  started {i.Started.ToLocalTime():g}"));
        }

        return Task.FromResult(0);
    }
}
