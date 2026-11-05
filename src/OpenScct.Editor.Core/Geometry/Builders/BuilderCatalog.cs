using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>Every built-in builder kind: the UE2 brush builders and the blockout kit.</summary>
public static class BuilderCatalog
{
    public static readonly IReadOnlyList<BuilderKind> All =
    [
        new CubeBuilder(),
        new CylinderBuilder(),
        new ConeBuilder(),
        new SheetBuilder(),
        new LinearStairBuilder(),
        new CurvedStairBuilder(),
        new SpiralStairBuilder(),
        new TetrahedronBuilder(),
        new VolumetricBuilder(),
        new RoomPiece(),
        new CorridorPiece(),
        new VentPiece(),
        new CrawlwayPiece(),
        new DoorwayPiece(),
        new WindowPiece(),
        new StairsPiece(),
        new RampPiece(),
        new PlatformPiece(),
        new LedgePiece(),
    ];

    public static BuilderKind? Find(string kind) =>
        All.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));

    public static BuilderKind Get(string kind) =>
        Find(kind) ?? throw new ArgumentException($"unknown shape kind '{kind}' (kinds: {string.Join(", ", All.Select(k => k.Kind))})");
}

/// <summary>The built-in builders as the editor's <see cref="IShapeBuilder"/> service.</summary>
[EditorService]
public sealed class BuiltinShapeBuilder : IShapeBuilder
{
    public IReadOnlyList<string> Kinds { get; } = BuilderCatalog.All.Select(k => k.Kind).ToList();

    public BuilderSpec Defaults(string kind) => BuilderCatalog.Get(kind).Defaults();

    public IReadOnlyList<(BrushShape Shape, CsgOper Oper, Vec3 Offset)> Build(BuilderSpec spec) =>
        BuilderCatalog.Get(spec.Kind).Build(spec).Select(p => (p.Shape, p.Oper, p.Offset)).ToList();
}
