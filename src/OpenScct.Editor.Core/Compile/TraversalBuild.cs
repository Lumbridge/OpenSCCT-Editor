using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// <c>sim/traversal.json</c> from the project's GEs (ledges, pipes, ladders, zip lines...): points in UE space, the
/// activation mask a GE answers to, its platform, and whether it is active under the level's GEActivationMask.
/// </summary>
public static class TraversalBuild
{
    public static void Write(CompileContext ctx)
    {
        int activation = ctx.Project.LevelInfo.GetInt("GEActivationMask") & 0xFF;
        var ges = new SvJsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Ge ge in ctx.Project.Ges)
        {
            ObjectRef at = ObjectRef.Ge(ge.Id);
            string kind = ge.Kind.ToUpperInvariant();
            int points = GeKinds.HasThirdPoint(kind) ? 3 : 2;
            if (!GeKinds.All.Contains(kind))
            {
                ctx.Error("traversal.kind", $"GE {ge.Id} has unknown kind '{ge.Kind}'", at);
                continue;
            }

            if (!GameplayBuild.IsIdentifier(ge.Id) || !seen.Add(ge.Id))
            {
                ctx.Error("traversal.id", $"GE id '{ge.Id}' is not an identifier or is used twice", at);
                continue;
            }

            if (ge.Points.Count != points)
            {
                ctx.Error("traversal.points", $"a {kind} GE has {points} points; {ge.Id} has {ge.Points.Count}", at);
                continue;
            }

            if (ge.Mask is < 0 or > 255 || ge.Platform is < 0 or > 2)
            {
                ctx.Error("traversal.mask", $"GE {ge.Id}: mask {ge.Mask} must be 0..255 and platform {ge.Platform} 0..2", at);
                continue;
            }

            var pts = new SvJsonArray();
            foreach (Vec3 p in ge.Points)
            {
                pts.Add(new SvJsonArray([SvJsonNode.Float((float)p.X), SvJsonNode.Float((float)p.Y), SvJsonNode.Float((float)p.Z)]));
            }

            var src = new SvJsonObject();
            src.Add("kind", SvJsonNode.String("authored"));
            src.Add("node", SvJsonNode.String(ge.Source ?? ge.Id));
            var obj = new SvJsonObject();
            obj.Add("activeAtStart", SvJsonNode.Bool(ge.Mask == 0 || (ge.Mask & activation) != 0));
            obj.Add("flags", SvJsonNode.Integer(ge.Flags));
            obj.Add("id", SvJsonNode.String(ge.Id));
            obj.Add("kind", SvJsonNode.String(kind));
            obj.Add("mask", SvJsonNode.Integer(ge.Mask));
            obj.Add("platform", SvJsonNode.Integer(ge.Platform));
            obj.Add("points", pts);
            obj.Add("src", src);
            ges.Add(obj);
        }

        var root = new SvJsonObject();
        root.Add("activationMask", SvJsonNode.Integer(activation));
        root.Add("formatVersion", SvJsonNode.String(GameplayBuild.FormatVersion));
        root.Add("ges", ges);
        root.Add("map", SvJsonNode.String(ctx.MapId));
        try
        {
            TraversalDoc doc = TraversalDoc.FromJson(root);
            ctx.Files[TraversalDoc.RelativePath] = CanonicalJson.ToFileBytes(root);
            ctx.Count("ges", doc.Ges.Count);
        }
        catch (Exception e) when (e is SvMapFormatException or ArgumentException)
        {
            ctx.Error("traversal.invalid", e.Message);
        }
    }
}
