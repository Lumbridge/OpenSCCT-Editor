using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// The scripting API's asset queries, over the same catalogue as the browser and <c>ose assets</c>:
/// <c>{"q":"assets","kind":"mesh","text":"chair","origin":"MyLevel","limit":50}</c> lists assets (best match first) as
/// rows of [kind, path, origin, facts, english]; <c>{"q":"asset","path":"..."}</c> gives one asset's details and where
/// this map uses it. A mesh is placed with a create op (class StaticMeshActor, set StaticMesh to the path) and a
/// material applied with an update op's <c>mat</c>.
/// </summary>
public static class AssetQueries
{
    public static SvJsonNode List(EditorSession s, SvJsonObject q)
    {
        AssetCatalog cat = Catalog(s);
        AssetKind? kind = Kind(q);
        int limit = Math.Max(1, (int)ApiJson.Number(q, "limit", 50));
        List<AssetEntry> found = cat.Search(ApiJson.GetString(q, "text"), kind, ApiJson.GetString(q, "origin")).ToList();
        var rows = found.Take(limit).Select(e => (SvJsonNode)ApiJson.Arr([
            ApiJson.Str(AssetKinds.Name(e.Kind)), ApiJson.Str(e.Path), ApiJson.Str(e.Origin), ApiJson.Str(Facts(e)),
            e.Gloss is { } g ? ApiJson.Str(g) : SvJsonNode.Null,
        ]));
        return ApiJson.Obj(("count", SvJsonNode.Integer(found.Count)), ("columns", ApiJson.Strings(["kind", "path", "origin", "facts", "english"])),
            ("rows", ApiJson.Arr(rows)), ("more", found.Count > limit ? SvJsonNode.True : null));
    }

    public static SvJsonNode Detail(EditorSession s, SvJsonObject q)
    {
        string path = ApiJson.GetString(q, "path") ?? throw new ApiException("path is the asset path (Package.Group.Name)", "path");
        AssetCatalog cat = Catalog(s);
        AssetEntry e = (Kind(q) is { } k ? cat.Find(k, path) : cat.Find(path))
            ?? throw new ApiException($"no asset '{path}' (try {{\"q\":\"assets\",\"text\":\"...\"}})", "path");
        var used = new SvJsonObject();
        foreach ((string map, int n) in e.UsedIn.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(20))
        {
            used.Add(map, SvJsonNode.Integer(n));
        }

        List<AssetUse> uses = AssetEdits.Usages(s.Project, e.Path);
        return ApiJson.Obj(
            ("kind", ApiJson.Str(AssetKinds.Name(e.Kind))),
            ("path", ApiJson.Str(e.Path)),
            ("origin", ApiJson.Str(e.Origin)),
            ("english", e.Gloss is { } g ? ApiJson.Str(g) : null),
            ("facts", ApiJson.Str(Facts(e))),
            ("bounds", e.Kind == AssetKind.Mesh ? ApiJson.Arr([ApiJson.Vec(e.BoundsMin), ApiJson.Vec(e.BoundsMax)]) : null),
            ("materials", e.Materials.Count > 0 ? ApiJson.Strings(e.Materials) : null),
            ("usedIn", used),
            ("usages", ApiJson.Arr(uses.Select(u => (SvJsonNode)ApiJson.Arr([ApiJson.Str(u.Object.Id), ApiJson.Str(u.How)])))));
    }

    /// <summary>A one-line summary of an asset (size, triangles, length).</summary>
    public static string Facts(AssetEntry e) => e.Kind switch
    {
        AssetKind.Mesh => $"{e.Triangles} tris, {ApiJson.Inv(e.Size.X)}x{ApiJson.Inv(e.Size.Y)}x{ApiJson.Inv(e.Size.Z)}, placed {e.UsedIn.Values.Sum()}x",
        AssetKind.Material or AssetKind.Texture => $"{e.Width}x{e.Height}",
        _ => $"{ApiJson.Inv(Math.Round(e.Duration, 1))} s {e.SoundKind}".TrimEnd(),
    };

    private static AssetCatalog Catalog(EditorSession s) => AssetCatalog.For(EditorServices.ContentRoot, s.Folder);

    private static AssetKind? Kind(SvJsonObject q)
    {
        try
        {
            return AssetKinds.Parse(ApiJson.GetString(q, "kind"));
        }
        catch (ArgumentException e)
        {
            throw new ApiException(e.Message, "kind");
        }
    }
}
