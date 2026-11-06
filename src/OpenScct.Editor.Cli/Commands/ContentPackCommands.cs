using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Cli.Commands;

internal static class ContentPackCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("content-packs", "content-packs list | install <id-or-map-code> [--content folder]",
            "Lists the OpenSCCT-Content catalogue or installs a pack and its required shared data. Prints JSON.", Run),
    ];

    private static async Task<int> Run(CliArgs a)
    {
        GeometryCliSupport.EnsureServices();
        var service = new ContentPacks(a.Get("content"));
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            SvJsonNode result = a.Positional(0) switch
            {
                "list" => ApiJson.Obj(("root", ApiJson.Str(service.Root)),
                    ("packs", ApiJson.Arr((await service.ListAsync(cancel.Token)).Select(Describe)))),
                "install" => await Install(a, service, cancel.Token),
                _ => throw new ArgumentException("Run 'ose content-packs --help' to see the pack commands."),
            };
            GeometryCliSupport.PrintJson(result, a);
            return 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("ose content-packs: Cancelled.");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Could not download content. {ex.Message}", ex);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<SvJsonNode> Install(CliArgs a, ContentPacks service, CancellationToken cancel)
    {
        string id = a.Positional(1) ?? throw new ArgumentException("Give a pack id or map code.");
        ContentPackInstall installed = await service.InstallAsync(id, progress: null, cancel);
        return ApiJson.Obj(("root", ApiJson.Str(service.Root)), ("pack", Describe(installed.Pack)),
            ("changed", SvJsonNode.Bool(installed.Changed)), ("warnings", GeometryCliSupport.Array(installed.Warnings)));
    }

    private static SvJsonNode Describe(ContentPackEntry entry) => ApiJson.Obj(
        ("id", ApiJson.Str(entry.Id)), ("code", ApiJson.Str(entry.Code)), ("title", ApiJson.Str(entry.Title)),
        ("category", ApiJson.Str(entry.Category)), ("version", ApiJson.Str(entry.Version)),
        ("bytes", SvJsonNode.Integer(entry.Size)), ("state", ApiJson.Str(entry.State.ToString())));
}
