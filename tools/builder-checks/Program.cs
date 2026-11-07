using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Tools;

EditorServices.Register(new BuiltinShapeBuilder());
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
bool Near(Vec3 a, Vec3 b) => (a-b).Length < 1e-6;
foreach (BuilderKind kind in BuilderCatalog.All)
{
    BuilderSpec spec = kind.Defaults();
    var handles = BuilderHandles.For(spec);
    Check(handles.Count > 0, kind.Kind + " has sizing controls");
    foreach (Rot rotation in new[] { Rot.Zero, new Rot(4096, 8192, 2048) })
    foreach (BuilderSizeHandle handle in handles)
    {
        var start = new BuilderBrush.State(spec, new Vec3(100, -64, 200), rotation, null);
        var next = handle.Candidate(start, 32);
        var parts = kind.Build(next.Spec);
        Check(parts.Count > 0, kind.Kind + " resize makes geometry");
        Check(next.Spec.Numbers.Keys.SequenceEqual(spec.Numbers.Keys), kind.Kind + " does not add parameters");
        Check(spec.Numbers.Where(p => p.Key != handle.Key).All(p => next.Spec.Numbers[p.Key] == p.Value), kind.Kind + " preserves secondary parameters");
        if (handle.Radial) Check(Near(next.Location, start.Location), kind.Kind + " radius keeps centerline");
        else
        {
            double opposite = handle.Sign > 0 ? -handle.Anchor : 1-handle.Anchor;
            Vec3 oldFace = start.Location + rotation.Rotate(Vec3.Zero.With(handle.Axis, handle.Offset + opposite*handle.Value(spec)));
            Vec3 newFace = next.Location + rotation.Rotate(Vec3.Zero.With(handle.Axis, handle.Offset + opposite*handle.Value(next.Spec)));
            Check(Near(oldFace, newFace), kind.Kind + " anchors opposite semantic plane under rotation");
        }
        var minimum = handle.Candidate(start, -1e9);
        Check(double.IsFinite(minimum.Spec.Numbers[handle.Key]) && minimum.Spec.Numbers[handle.Key] >= kind.Params.First(p => p.Name == handle.Key).Min, kind.Kind + " enforces minimum");
    }
}
// Test actual cube vertex planes, not only descriptor arithmetic.
foreach (Rot rotation in new[] { Rot.Zero, new Rot(8000, 12000, 4000) })
foreach (BuilderSizeHandle handle in BuilderHandles.For(BuilderCatalog.Get("cube").Defaults()))
{
    BuilderSpec spec = BuilderCatalog.Get("cube").Defaults();
    var state = new BuilderBrush.State(spec, new Vec3(0, 0, 128), rotation, null);
    var candidate = handle.Candidate(state, 64);
    Vec3 direction = rotation.Rotate(Vec3.Zero.With(handle.Axis, handle.Sign));
    double Plane(BuilderSpec s, Vec3 location) => BuilderCatalog.Get("cube").Build(s).SelectMany(p => p.Shape.Faces).SelectMany(f => f.Vertices)
        .Min(p => Vec3.Dot(location + rotation.Rotate(p), direction));
    Check(Math.Abs(Plane(spec,state.Location)-Plane(candidate.Spec,candidate.Location))<1e-6, "actual cube opposite face stays fixed");
}
foreach (string layout in new[] { "straight", "l", "u", "spiral" })
{
    BuilderSpec spec = BuilderCatalog.Get("stairs").Defaults(); spec.Strings["layout"] = layout;
    foreach (var handle in BuilderHandles.For(spec)) Check(BuilderCatalog.Get("stairs").Build(handle.Candidate(new(spec, Vec3.Zero, Rot.Zero, null), 16).Spec).Count > 0, "blockout stair layout " + layout);
}
foreach (string axis in new[] { "horizontal", "x", "y" })
{
    BuilderSpec spec = BuilderCatalog.Get("sheet").Defaults(); spec.Strings["axis"] = axis;
    Check(BuilderHandles.For(spec).All(h => h.Axis != (axis == "horizontal" ? 2 : axis == "x" ? 0 : 1)), "sheet handles stay in plane");
}
var builder = new BuilderBrush();
builder.SetPieces([Box.Shape(new Vec3(64,96,128))]); builder.Location = new Vec3(200,300,400); builder.Rotation = new Rot(0,8000,0);
var custom = builder.Capture();
builder.SetKind("cone"); builder.Restore(custom);
Check(builder.IsCustom && builder.Location == custom.Location && builder.Rotation == custom.Rotation, "cancel restores complete custom state");
builder.SetPieces([Box.Shape(new Vec3(32,32,32))]);
Check(custom.Custom![0].Bounds() == (new Vec3(-32,-48,-64), new Vec3(32,48,64)), "checkpoints own independent geometry");
builder.SetKind("cone"); var before = builder.Capture();
BuilderSpec invalid = before.Spec.Clone(); invalid.Numbers["hollow"] = 1; invalid.Numbers["capHeight"] = invalid.Numbers["height"];
Check(!builder.TryApply(invalid, Vec3.Zero, Rot.Zero, out string? error) && error is not null, "invalid resize is rejected");
Check(builder.Spec.Numbers["hollow"] == 0 && builder.Location == before.Location, "invalid resize preserves last valid preview");
foreach (GeometryPreset preset in GeometryPresets.All)
{
    BuilderSpec spec = preset.Build();
    Check(BuilderCatalog.Get(spec.Kind).Build(spec).Count > 0, "preset builds " + preset.Title);
    spec.Numbers[spec.Numbers.Keys.First()] += 1;
    Check(preset.Build().Numbers[spec.Numbers.Keys.First()] != spec.Numbers[spec.Numbers.Keys.First()], "preset produces independent specs");
}
Check(BuilderCatalog.All.All(k => GeometryPresets.All.Any(p => p.Kind == k.Kind)), "palette covers every built-in shape");
Check(GeometryPresets.All.Select(p=>p.Title).Distinct().Count()==GeometryPresets.All.Count, "unique preset titles");
// Radius controls must survive an end-on axis in every orthographic view.
foreach (string kind in new[] { "cylinder", "cone", "tetrahedron", "volumetric", "curvedStair", "spiralStair" })
foreach (int depth in new[] { 0, 1, 2 })
    Check(BuilderHandles.For(BuilderCatalog.Get(kind).Defaults()).Any(h => h.Radial && h.Axis != depth), kind + " radius usable in every orthographic view");
// A solid box thumbnail has exactly three visible faces. Rear faces caused the impossible folded-slab illusion.
foreach (int size in new[] { 128, 256, 512, 1024, 2048 })
{
    var slab = Box.Shape(new Vec3(size, size, 32));
    var visible = GeometryThumbnailFaces.Visible(slab.Faces).ToList();
    Check(visible.Count == 3, "slab thumbnail has only three visible faces");
    Check(visible.Count(f => f.Normal().Z > .5) == 1, "slab thumbnail has one top");
    Check(visible.All(f => Vec3.Dot(f.Normal(), GeometryThumbnailFaces.Eye) > 0), "slab thumbnail excludes rear faces");
}
Console.WriteLine($"PASS {checks} builder and palette checks; {GeometryPresets.All.Count} presets; {BuilderCatalog.All.Count} shape kinds");
