using Godot;
using OpenScct.Core;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Viewports;

/// <summary>
/// A native conveyor in the textured views: the same frame, belt, steps and drums OpenSCCT's map build bakes
/// (<see cref="ConveyorGeometry"/>), in its belt and frame materials, so the author sees the built look while drawing it.
/// While the conveyor tool runs the belts, <see cref="Animate"/> moves the look as the game does: the belt's and drums'
/// textures and the scroll targets' slide with the belt and an escalator's steps travel along it.
/// </summary>
public static class ConveyorLook
{
    private static readonly string[] s_shapeProps = ["Path", "bLoop", "Width", "Look", "Walls", "BeltMaterial", "FrameMaterial"];

    /// <summary>The moving parts of each drawn conveyor, by actor id (the latest build of each).</summary>
    private static readonly Dictionary<string, Drawn> s_drawn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Texture slides set up for the conveyors being animated, by actor id.</summary>
    private static readonly Dictionary<string, Motion> s_moving = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Changes when the built look would, including where other conveyors join it (its walls open there): the scene
    /// rebuilds the conveyor then.
    /// </summary>
    public static int Key(Actor a, MapProject project)
    {
        var h = new HashCode();
        foreach (string prop in s_shapeProps)
        {
            h.Add(a.Props[prop]?.ToString());
        }

        foreach (ConveyorJoin join in ConveyorJoins.Find(a, project.Actors))
        {
            h.Add(join);
        }

        return h.ToHashCode();
    }

    /// <summary>Adds the look under <paramref name="root"/>, which sits at the actor's location.</summary>
    public static void Attach(Node3D root, Actor a, MapProject project)
    {
        Conveyor c = ConveyorDesign.Read(a);
        ConveyorGeometry look = ConveyorGeometry.Build(c, ConveyorJoins.Find(a, project.Actors));
        var drawn = new Drawn(root, a.Location);
        foreach ((ConveyorPart part, ConveyorMesh mesh) in look.Parts)
        {
            ViewMaterial material = ViewMaterials.Surface(part == ConveyorPart.Frame ? c.FrameMaterial.Path : c.BeltMaterial.Path, 0);
            ArrayMesh built = ToGodot(mesh, a.Location);
            MeshInstance3D unlit = Add(root, built, material.Unlit, VisualLayers.Unlit);
            MeshInstance3D lit = Add(root, built, material.Lit, VisualLayers.Lit);
            if (part != ConveyorPart.Frame)
            {
                drawn.Parts.Add(new DrawnPart(part, material, unlit, lit));
            }
        }

        // Conveyors deleted or renamed since leave nodes the scene has freed.
        foreach (string gone in s_drawn.Where(p => !GodotObject.IsInstanceValid(p.Value.Root)).Select(p => p.Key).ToList())
        {
            s_drawn.Remove(gone);
        }

        s_drawn[a.Id] = drawn;
    }

    /// <summary>
    /// Moves each conveyor's drawn look to where its belt is at <paramref name="tick"/> (server ticks since the belts
    /// started; <see cref="Conveyor.PhaseAt"/>). <paramref name="rootOf"/> finds a drawn actor's node, for the scroll
    /// targets' meshes. Conveyors animated before and not in this call go back to their built look.
    /// </summary>
    public static void Animate(IEnumerable<(Actor Actor, Conveyor Conveyor)> conveyors, double tick, Func<string, Node3D?> rootOf)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((Actor a, Conveyor c) in conveyors)
        {
            if (!s_drawn.TryGetValue(a.Id, out Drawn? drawn))
            {
                continue;
            }

            seen.Add(a.Id);
            if (!s_moving.TryGetValue(a.Id, out Motion? motion) || !motion.Fits(drawn, rootOf))
            {
                motion?.Restore();
                motion = Motion.Of(drawn, c, rootOf);
                s_moving[a.Id] = motion;
            }

            double phase = c.PhaseAt(tick);
            motion.Slide(phase);
            if (c.Look == EConveyorLook.CL_Escalator && drawn.Parts.Find(p => p.Part == ConveyorPart.Steps) is { } steps && steps.Valid)
            {
                ArrayMesh moved = ToGodot(ConveyorGeometry.StepsAt(c, c.WorldPath, phase), drawn.Origin);
                steps.Unlit.Mesh = moved;
                steps.Lit.Mesh = moved;
            }
        }

        foreach (string id in s_moving.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Settle(id);
        }
    }

    /// <summary>Puts every animated conveyor back to its built look (the tool stopped animating).</summary>
    public static void Still()
    {
        foreach (string id in s_moving.Keys.ToList())
        {
            Settle(id);
        }
    }

    private static void Settle(string id)
    {
        s_moving[id].Restore();
        s_moving.Remove(id);
        // The steps go back to where the map build bakes them; the scene's next rebuild of the actor would too.
        if (s_drawn.TryGetValue(id, out Drawn? drawn) && drawn.Parts.Find(p => p.Part == ConveyorPart.Steps) is { Valid: true } steps &&
            steps.Built is { } built)
        {
            steps.Unlit.Mesh = built;
            steps.Lit.Mesh = built;
        }
    }

    private static MeshInstance3D Add(Node3D root, ArrayMesh mesh, Material material, uint layers)
    {
        // The material goes on the instance as an override of every surface, so it survives the steps' mesh changing.
        var mi = new MeshInstance3D
        {
            Mesh = mesh,
            Layers = layers,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = material,
        };
        root.AddChild(mi);
        return mi;
    }

    /// <summary>A conveyor part as a Godot mesh around <paramref name="origin"/> (the actor's location).</summary>
    private static ArrayMesh ToGodot(ConveyorMesh mesh, Vec3 origin)
    {
        int n = mesh.Indices.Count;
        var points = new Vector3[n];
        var normals = new Vector3[n];
        var uvs = new Vector2[n];
        for (int t = 0; t + 2 < n; t += 3)
        {
            int i0 = mesh.Indices[t], i1 = mesh.Indices[t + 1], i2 = mesh.Indices[t + 2];
            Vector3 p0 = Space.ToGodot(mesh.Positions[i0] - origin), p1 = Space.ToGodot(mesh.Positions[i1] - origin), p2 = Space.ToGodot(mesh.Positions[i2] - origin);
            Vector3 normal = Space.DirToGodot(mesh.Normals[i0]);
            // Godot front faces are clockwise seen from the front (the axis swap mirrors the editor's winding).
            if ((p1 - p0).Cross(p2 - p0).Dot(normal) > 0)
            {
                (i1, i2) = (i2, i1);
                (p1, p2) = (p2, p1);
            }

            int[] corners = [i0, i1, i2];
            Vector3[] at = [p0, p1, p2];
            for (int k = 0; k < 3; k++)
            {
                points[t + k] = at[k];
                normals[t + k] = Space.DirToGodot(mesh.Normals[corners[k]]);
                (double u, double v) = mesh.Uvs[corners[k]];
                uvs[t + k] = new Vector2((float)u, (float)v);
            }
        }

        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        var built = new ArrayMesh();
        if (n > 0)
        {
            MeshUpload.AddSurface(built, Mesh.PrimitiveType.Triangles, arrays);
        }

        return built;
    }

    /// <summary>A drawn conveyor's moving parts under its node <c>Root</c>, which sits at <c>Origin</c>.</summary>
    private sealed record Drawn(Node3D Root, Vec3 Origin)
    {
        public List<DrawnPart> Parts { get; } = [];
    }

    private sealed record DrawnPart(ConveyorPart Part, ViewMaterial Material, MeshInstance3D Unlit, MeshInstance3D Lit)
    {
        /// <summary>The mesh as built (the steps at phase 0), to put back after animating.</summary>
        public ArrayMesh? Built { get; } = Unlit.Mesh as ArrayMesh;

        public bool Valid => GodotObject.IsInstanceValid(Unlit) && GodotObject.IsInstanceValid(Lit);
    }

    /// <summary>
    /// One textured surface sliding with a belt: its own copy of the shared material while it moves. The conveyor's own
    /// parts draw with a StandardMaterial3D; a scroll target's mesh with the storey-clipped copy of one (StoreyClip).
    /// </summary>
    private sealed record Slide(MeshInstance3D Instance, int Surface, Material Shared, Material Copy, float UnitsPerTile, byte Axis)
    {
        public bool Valid => GodotObject.IsInstanceValid(Instance);

        /// <summary>Still drawn with the copy: the scene puts its own materials back when the Lit Preview's light changes.</summary>
        public bool Current =>
            Valid && ReferenceEquals(Surface < 0 ? Instance.MaterialOverride : Instance.GetSurfaceOverrideMaterial(Surface), Copy);

        public void Apply(double phase)
        {
            // As the game slides it: the offset runs against the direction of travel.
            float offset = -(float)(phase / UnitsPerTile - Math.Floor(phase / UnitsPerTile));
            var uv = Axis == 0 ? new Vector2(offset, 0) : new Vector2(0, offset);
            // The shared material may get its texture after the copy was made (textures load in the background).
            switch ((Shared, Copy))
            {
                case (StandardMaterial3D shared, StandardMaterial3D copy):
                    copy.AlbedoTexture = shared.AlbedoTexture;
                    copy.Uv1Offset = new Vector3(uv.X, uv.Y, 0);
                    break;
                case (ShaderMaterial shared, ShaderMaterial copy):
                    copy.SetShaderParameter(StoreyClip.Texture, shared.GetShaderParameter(StoreyClip.Texture));
                    copy.SetShaderParameter(StoreyClip.UvOffset, uv);
                    break;
            }
        }

        public void Put(Material material)
        {
            if (Surface < 0)
            {
                Instance.MaterialOverride = material;
            }
            else
            {
                Instance.SetSurfaceOverrideMaterial(Surface, material);
            }
        }
    }

    /// <summary>The slides of one animated conveyor: its belt, its drums and its scroll targets' meshes.</summary>
    private sealed class Motion
    {
        private readonly Drawn _drawn;
        private readonly List<Slide> _slides = [];

        /// <summary>Scroll targets whose mesh had not loaded: the motion is set up again once one has.</summary>
        private readonly List<string> _waiting = [];

        private Motion(Drawn drawn) => _drawn = drawn;

        public static Motion Of(Drawn drawn, Conveyor c, Func<string, Node3D?> rootOf)
        {
            var m = new Motion(drawn);
            foreach (DrawnPart part in drawn.Parts.Where(p => p.Valid))
            {
                (float tile, byte axis) = part.Part switch
                {
                    ConveyorPart.Belt => (ConveyorLayout.BeltTile, (byte)1),
                    ConveyorPart.Rollers => (2f * MathF.PI * ConveyorLayout.RollerRadius, (byte)0),
                    _ => (0f, (byte)0),
                };
                if (tile > 0)
                {
                    m.Add(part.Unlit, -1, part.Material.Unlit, tile, axis);
                    m.Add(part.Lit, -1, part.Material.Lit, tile, axis);
                }
            }

            // A negative size scrolls the other way (a converted RE+ tread panner).
            foreach (FConveyorScroll target in c.ScrollTargets.Where(t => t.UnitsPerTile != 0))
            {
                if (rootOf(target.Target)?.GetNodeOrNull<Node3D>("StaticMesh") is not { } holder)
                {
                    m._waiting.Add(target.Target);
                    continue;
                }

                foreach (MeshInstance3D mi in holder.GetChildren().OfType<MeshInstance3D>())
                {
                    for (int s = 0; s < mi.GetSurfaceOverrideMaterialCount(); s++)
                    {
                        if (mi.GetSurfaceOverrideMaterial(s) is (StandardMaterial3D or ShaderMaterial) and Material shared
                            && (target.Material.IsNone || mi.Mesh is ArrayMesh named && Draws(named.SurfaceGetName(s), target.Material)))
                        {
                            m.Add(mi, s, shared, target.UnitsPerTile, target.Axis);
                        }
                    }
                }
            }

            return m;
        }

        /// <summary>
        /// Still the motion of <paramref name="drawn"/>'s build: every surface it slides is still there with its copy,
        /// and no scroll target's mesh has loaded since.
        /// </summary>
        public bool Fits(Drawn drawn, Func<string, Node3D?> rootOf) =>
            ReferenceEquals(drawn, _drawn) && _slides.All(s => s.Current) && !_waiting.Any(id => rootOf(id)?.GetNodeOrNull<Node3D>("StaticMesh") is not null);

        public void Slide(double phase)
        {
            foreach (Slide s in _slides)
            {
                s.Apply(phase);
            }
        }

        public void Restore()
        {
            foreach (Slide s in _slides.Where(s => s.Current))
            {
                s.Put(s.Shared);
            }

            _slides.Clear();
        }

        /// <summary>
        /// Whether a mesh surface is drawn with <paramref name="material"/> (Package.Group.Name): the surface carries the
        /// glb material's name, Package.Name with the exporter's variant suffixes after "__", as the game matches it.
        /// </summary>
        private static bool Draws(string surface, AssetRef material)
        {
            int cut = surface.IndexOf("__", StringComparison.Ordinal);
            string root = cut >= 0 ? surface[..cut] : surface;
            return string.Equals(root, material.Path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(root, $"{material.Package}.{material.Name}", StringComparison.OrdinalIgnoreCase);
        }

        private void Add(MeshInstance3D mi, int surface, Material shared, float tile, byte axis)
        {
            var copy = (Material)shared.Duplicate();
            var slide = new Slide(mi, surface, shared, copy, tile, axis);
            slide.Put(copy);
            _slides.Add(slide);
        }
    }
}
