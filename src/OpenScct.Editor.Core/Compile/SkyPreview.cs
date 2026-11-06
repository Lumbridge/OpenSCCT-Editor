using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>The sky camera and the build's zone classification, captured from one immutable preview snapshot.</summary>
public sealed record SkyPreviewScene(Vec3 Location, Func<Vec3, bool> Contains)
{
    public bool ContainsPolygon(PreviewPolygon polygon) => polygon.Vertices.Count > 0 && Contains(
        polygon.Vertices.Aggregate(Vec3.Zero, (sum, v) => sum + v) / polygon.Vertices.Count + polygon.Normal * 2);
}

/// <summary>Finds the sky without baking light or writing a build. The viewport uses the same BSP zones as the game.</summary>
public sealed class SkyPreview
{
    private readonly WorldCache _world = new();

    public SkyPreviewScene? Build(MapProject project, string? folder)
    {
        var ctx = new CompileContext(project, new CompileOptions { ProjectFolder = folder }, EditorServices.Classes);
        if (!project.Actors.Any(ctx.Rules.IsSkyZone))
        {
            return null;
        }

        if (project.Baked is { } baked)
        {
            ctx.Baked = BakedSource.Open(ctx, baked);
        }

        GameplayBuild.SortActors(ctx);
        MapCompiler.BuildWorld(ctx, _world);
        Actor? sky = ctx.Gameplay.FirstOrDefault(a => ctx.Rules.IsSkyZone(a) && ctx.InSkyZone(a.Location));
        return sky is null ? null : new SkyPreviewScene(sky.Location, ctx.InSkyZone);
    }
}
