namespace OpenScct.Editor.Core.Model;

/// <summary>Editor-only state every placed object carries (never compiled into the game map).</summary>
public sealed class EditorMeta
{
    /// <summary>The layer the object is on ("" = the default layer).</summary>
    public string Layer { get; set; } = "";

    /// <summary>The outliner folder path, '/'-separated ("" = top level).</summary>
    public string Folder { get; set; } = "";

    /// <summary>Hidden in the editor's viewports. Kept across builds and saves (unlike the stock editor).</summary>
    public bool Hidden { get; set; }

    /// <summary>Cannot be selected or moved in the viewports.</summary>
    public bool Locked { get; set; }

    /// <summary>The linked prefab instance this object belongs to (<see cref="MapProject.Prefabs"/>), or null.</summary>
    public string? PrefabInstance { get; set; }

    /// <summary>The part of the prefab this object was made from (its id inside the prefab), so updating the
    /// instance from a changed prefab keeps the object's id.</summary>
    public string? PrefabPart { get; set; }

    /// <summary>Free note for the author.</summary>
    public string? Note { get; set; }

    /// <summary>Provenance: who last created or edited the object through a change document (e.g. a script's name).</summary>
    public string? Author { get; set; }

    /// <summary>Provenance: the change document op that last created or edited the object, e.g. <c>rooms#3</c>.</summary>
    public string? Op { get; set; }

    /// <summary>The key a mover is drawn at while its keys are placed in the views, or null (drawn where it starts).
    /// Kept because two keys can share a pose, so the drawn pose alone cannot tell which key is being placed.</summary>
    public int? MoverKey { get; set; }

    public EditorMeta Clone() => (EditorMeta)MemberwiseClone();

    /// <summary>Makes the object a plain one (a copy of a prefab part, or a part of a broken instance).</summary>
    public void UnlinkPrefab()
    {
        PrefabInstance = null;
        PrefabPart = null;
    }

    public bool IsDefault => Layer.Length == 0 && Folder.Length == 0 && !Hidden && !Locked && PrefabInstance is null && PrefabPart is null && Note is null
        && Author is null && Op is null && MoverKey is null;
}

/// <summary>A placed actor: gameplay actors (what the server simulates) and render-only ones (static meshes, lights,
/// projectors...). The transform is typed; every other stored property is in <see cref="Props"/>.</summary>
public sealed class Actor
{
    public Actor(string id, string className)
    {
        Id = id;
        Class = className;
    }

    /// <summary>Unique in the map, ignoring ASCII case (the original export name, e.g. <c>PlayerStart3</c>).</summary>
    public string Id { get; set; }

    /// <summary><c>Package.Class</c>, e.g. <c>Engine.PlayerStart</c>, <c>SBase.SObjective</c>.</summary>
    public string Class { get; set; }

    public Vec3 Location { get; set; }

    public Rot Rotation { get; set; }

    /// <summary>DrawScale (uniform).</summary>
    public double DrawScale { get; set; } = 1;

    /// <summary>DrawScale3D (per axis).</summary>
    public Vec3 DrawScale3D { get; set; } = Vec3.One;

    public Vec3 PrePivot { get; set; }

    /// <summary>Stored properties other than the transform: only what differs from the class defaults.</summary>
    public PropSet Props { get; set; } = new();

    /// <summary>The shape of a brush-based actor (volumes, movers, zone/anti-portal brushes), in its local space.</summary>
    public BrushShape? Shape { get; set; }

    /// <summary>The player counts the actor is in the match for, and a mission's win targets; null for none.</summary>
    public ActorMatch? Match { get; set; }

    /// <summary>A mover's own time per leg and easing per key (compiled to SVMap 1.2 <c>sim/movers.json</c>); null
    /// for the game's timing, one MoveTime and glide for every leg.</summary>
    public OpenScct.Core.Engine.MoverLegs? Legs { get; set; }

    /// <summary>Parametric glass and its optional permanent frame, lowered at build time.</summary>
    public Windows.WindowDesign? Window { get; set; }
    public Doors.DoorDesign? Door { get; set; }

    public EditorMeta Editor { get; set; } = new();

    /// <summary>Where the actor came from (import provenance), e.g. <c>{"export": 12}</c>; kept for round trips.</summary>
    public string? Source { get; set; }

    public string ShortClass => Class.Contains('.') ? Class[(Class.LastIndexOf('.') + 1)..] : Class;

    /// <summary>The effective Tag: the stored one, else the class name (UE2 rule).</summary>
    public string Tag => Props.GetString("Tag") is { Length: > 0 } t ? t : ShortClass;

    public Actor Clone(string? newId = null) => new(newId ?? Id, Class)
    {
        Location = Location,
        Rotation = Rotation,
        DrawScale = DrawScale,
        DrawScale3D = DrawScale3D,
        PrePivot = PrePivot,
        Props = Props.Clone(),
        Shape = Shape?.Clone(),
        Match = Match,
        Legs = Legs,
        Window = Window?.Clone(),
        Door = Door?.Clone(),
        Editor = Editor.Clone(),
        Source = Source,
    };

    /// <summary>Local point to world: ((p - PrePivot) * scale) rotated, plus Location (UE2 actor transform).</summary>
    public Vec3 LocalToWorld(Vec3 p) => Rotation.Rotate((p - PrePivot).Scale(DrawScale3D) * DrawScale) + Location;

    public Vec3 WorldToLocal(Vec3 p)
    {
        Vec3 s = DrawScale3D * DrawScale;
        Vec3 local = Rotation.Unrotate(p - Location);
        return new Vec3(Div(local.X, s.X), Div(local.Y, s.Y), Div(local.Z, s.Z)) + PrePivot;
    }

    private static double Div(double a, double b) => Math.Abs(b) > 1e-12 ? a / b : 0;
}

/// <summary>The UE2 CSG operation of a brush.</summary>
public enum CsgOper
{
    /// <summary>Adds solid (fills space).</summary>
    Add,

    /// <summary>Subtracts (carves empty space). The UE2 world starts solid, so maps begin by carving.</summary>
    Subtract,
}

/// <summary>UE2 polygon / brush flags (EPolyFlags). Only the bits the editor uses by name are listed.</summary>
public static class PolyFlags
{
    public const int Invisible = 0x00000001;
    public const int Masked = 0x00000002;
    public const int Translucent = 0x00000004;
    public const int NotSolid = 0x00000008;
    public const int Environment = 0x00000010;
    public const int Semisolid = 0x00000020;
    public const int Modulated = 0x00000040;
    public const int FakeBackdrop = 0x00000080;
    public const int TwoSided = 0x00000100;
    public const int NoSmooth = 0x00000800;
    public const int SpecialLit = 0x00100000;
    public const int Unlit = 0x00400000;
    public const int Mirrored = 0x08000000;
    public const int Portal = 0x04000000;
    public const int AntiPortal = 0x02000000;
}

/// <summary>One face of a brush, in the brush's local space (UE2 FPoly).</summary>
public sealed class BrushFace
{
    /// <summary>Convex, coplanar, wound so the normal points out of the brush (UE2 convention: clockwise seen from outside).</summary>
    public List<Vec3> Vertices { get; set; } = new();

    /// <summary>Material asset path (<c>Package.Group.Name</c>), or null for the default material.</summary>
    public string? Material { get; set; }

    /// <summary>Per-face EPolyFlags.</summary>
    public int Flags { get; set; }

    /// <summary>Texture mapping (UE2 FPoly Base/TextureU/TextureV): u = dot(p - Base, TextureU) + PanU.</summary>
    public Vec3 TextureBase { get; set; }

    public Vec3 TextureU { get; set; }

    public Vec3 TextureV { get; set; }

    public int PanU { get; set; }

    public int PanV { get; set; }

    /// <summary>UE2's default lightmap texel scale.</summary>
    public const double DefaultLightMapScale = 32;

    /// <summary>Lightmap texel scale (UE2 default 32): the original's XBOX scale, which the PC game uses.</summary>
    public double LightMapScale { get; set; } = DefaultLightMapScale;

    /// <summary>The original's separate PS2 lightmap scale, kept for round trips; null when it equals
    /// <see cref="LightMapScale"/>.</summary>
    public double? LightMapScalePsx2 { get; set; }

    /// <summary>The poly's group name from the original (ItemName), kept for round trips.</summary>
    public string? ItemName { get; set; }

    public string? StairRole { get; set; }

    /// <summary>The original poly link (iLink), kept for round trips; -1 when none.</summary>
    public int Link { get; set; } = -1;

    public BrushFace Clone() => new()
    {
        Vertices = new List<Vec3>(Vertices),
        Material = Material,
        Flags = Flags,
        TextureBase = TextureBase,
        TextureU = TextureU,
        TextureV = TextureV,
        PanU = PanU,
        PanV = PanV,
        LightMapScale = LightMapScale,
        LightMapScalePsx2 = LightMapScalePsx2,
        ItemName = ItemName,
        StairRole = StairRole,
        Link = Link,
    };

    /// <summary>Newell normal of the face (unit length, or zero for a degenerate face).</summary>
    public Vec3 Normal()
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < Vertices.Count; i++)
        {
            Vec3 a = Vertices[i], b = Vertices[(i + 1) % Vertices.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3(x, y, z).Normalized();
    }
}

/// <summary>The geometry of a brush: its faces plus, for parametric pieces, how to regenerate them.</summary>
public sealed class BrushShape
{
    public List<BrushFace> Faces { get; set; } = new();

    /// <summary>The builder that made the shape and its parameters (cube, cylinder, stairs...), so the piece stays
    /// parametric across saves instead of decomposing into loose polygons. Null for hand-made or imported shapes.</summary>
    public BuilderSpec? Builder { get; set; }

    public BrushShape Clone() => new() { Faces = Faces.Select(f => f.Clone()).ToList(), Builder = Builder?.Clone() };

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (Vec3 v in Faces.SelectMany(f => f.Vertices))
        {
            min = Vec3.Min(min, v);
            max = Vec3.Max(max, v);
        }

        return Faces.Count == 0 ? (Vec3.Zero, Vec3.Zero) : (min, max);
    }
}

/// <summary>A builder name and its parameters (all numbers or strings).</summary>
public sealed class BuilderSpec
{
    public BuilderSpec(string kind) => Kind = kind;

    /// <summary>Builder kind, e.g. <c>cube</c>, <c>cylinder</c>, <c>cone</c>, <c>sheet</c>, <c>linearStair</c>,
    /// <c>curvedStair</c>, <c>spiralStair</c>, <c>tetrahedron</c>, or a blockout piece such as <c>room</c>.</summary>
    public string Kind { get; set; }

    public Dictionary<string, double> Numbers { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);

    public double Get(string key, double fallback) => Numbers.TryGetValue(key, out double v) ? v : fallback;

    public BuilderSpec Clone() => new(Kind)
    {
        Numbers = new Dictionary<string, double>(Numbers, StringComparer.Ordinal),
        Strings = new Dictionary<string, string>(Strings, StringComparer.Ordinal),
    };
}

/// <summary>A CSG brush of the level geometry (UE2 <c>Engine.Brush</c> actor). Order in the map is the CSG order.</summary>
public sealed class Brush
{
    public Brush(string id) => Id = id;

    public OpenScct.Editor.Core.Stairs.StairDesign? Stair { get; set; }

    public string Id { get; set; }

    public CsgOper Oper { get; set; } = CsgOper.Subtract;

    /// <summary>Brush-wide EPolyFlags (Semisolid, NotSolid, Portal...).</summary>
    public int PolyFlags { get; set; }

    public Vec3 Location { get; set; }

    public Rot Rotation { get; set; }

    public Vec3 PrePivot { get; set; }

    /// <summary>UE2 MainScale (applied before rotation).</summary>
    public Vec3 MainScale { get; set; } = Vec3.One;

    /// <summary>UE2 PostScale (applied after rotation).</summary>
    public Vec3 PostScale { get; set; } = Vec3.One;

    public BrushShape Shape { get; set; } = new();

    /// <summary>Other stored Brush actor properties (Group, Tag...), kept for round trips.</summary>
    public PropSet Props { get; set; } = new();

    public EditorMeta Editor { get; set; } = new();

    public string? Source { get; set; }

    public bool IsSemisolid => (PolyFlags & Model.PolyFlags.Semisolid) != 0;

    public bool IsNotSolid => (PolyFlags & Model.PolyFlags.NotSolid) != 0;

    /// <summary>A zone portal: the brush carries the portal flag, or every face of it does (a doorway's portal sheet).</summary>
    public bool IsPortal => (PolyFlags & Model.PolyFlags.Portal) != 0
        || (Shape.Faces.Count > 0 && Shape.Faces.TrueForAll(f => (f.Flags & Model.PolyFlags.Portal) != 0));

    public Brush Clone(string? newId = null) => new(newId ?? Id)
    {
        Stair = Stair?.Clone(),
        Oper = Oper,
        PolyFlags = PolyFlags,
        Location = Location,
        Rotation = Rotation,
        PrePivot = PrePivot,
        MainScale = MainScale,
        PostScale = PostScale,
        Shape = Shape.Clone(),
        Props = Props.Clone(),
        Editor = Editor.Clone(),
        Source = Source,
    };

    /// <summary>UE2 brush transform: ((p - PrePivot) * MainScale) rotated, * PostScale, + Location.</summary>
    public Vec3 LocalToWorld(Vec3 p) => Rotation.Rotate((p - PrePivot).Scale(MainScale)).Scale(PostScale) + Location;

    /// <summary>The faces in world space, wound outwards (a negative scale flips the winding back). Texture axes go
    /// through the inverse transpose of the brush transform (UE2 VectorXform), so textures stay where they were.</summary>
    public IEnumerable<BrushFace> WorldFaces()
    {
        var t = Geometry.ShapeTransform.For(this);
        foreach (BrushFace f in Shape.Faces)
        {
            BrushFace w = f.Clone();
            w.Vertices = t.FaceToWorld(f);
            w.TextureBase = t.ToWorld(f.TextureBase);
            w.TextureU = t.TextureAxisToWorld(f.TextureU);
            w.TextureV = t.TextureAxisToWorld(f.TextureV);
            yield return w;
        }
    }
}

/// <summary>The seven SCCT geometric event (GE) kinds, as named in sim/traversal.json and the editor's GE ADD.</summary>
public static class GeKinds
{
    public const string LedgeGrab = "LG";
    public const string ZipLine = "ZL";
    public const string Pipe = "PIPE";
    public const string Ladder = "LADDER";
    public const string HandOverHand = "HOH";
    public const string Pole = "POLE";
    public const string Fence = "FENCE";

    public static readonly IReadOnlyList<string> All = [LedgeGrab, ZipLine, Pipe, Ladder, HandOverHand, Pole, Fence];

    /// <summary>Kinds with a third point (the climbable's top/depth).</summary>
    public static bool HasThirdPoint(string kind) => kind is Ladder or Pipe;
}

/// <summary>Dimensions and material of optional geometry generated along a GE.</summary>
public sealed class GeAppearance
{
    /// <summary>Ladder width, or fence height, in Unreal units.</summary>
    public double Width { get; set; } = 48;

    public double Radius { get; set; } = 4;

    /// <summary>Distance between ladder rungs or fence uprights.</summary>
    public double Spacing { get; set; } = 24;

    public string? Material { get; set; }

    public GeAppearance Clone() => (GeAppearance)MemberwiseClone();
}

/// <summary>A geometric event: a traversal line the player can grab (ledge, pipe, zip line...).</summary>
public sealed class Ge
{
    public Ge(string id, string kind)
    {
        Id = id;
        Kind = kind;
    }

    public string Id { get; set; }

    public string Kind { get; set; }

    /// <summary>Two points, or three for ladders and pipes.</summary>
    public List<Vec3> Points { get; set; } = new();

    /// <summary>The GE ID bit (0 = always on, else one of 1..128 matched against LevelInfo.GEActivationMask).</summary>
    public int Mask { get; set; }

    /// <summary>0 common, 1 PS2 only, 2 Xbox only (original GEPlatform).</summary>
    public int Platform { get; set; }

    /// <summary>The original flag word, kept for round trips (0x2 is the editor's selection bit).</summary>
    public int Flags { get; set; }

    /// <summary>Optional generated geometry, rebuilt from the GE's points. Null keeps a traversal zone only.</summary>
    public GeAppearance? Appearance { get; set; }

    public EditorMeta Editor { get; set; } = new();

    public string? Source { get; set; }

    public Ge Clone(string? newId = null) => new(newId ?? Id, Kind)
    {
        Points = new List<Vec3>(Points),
        Mask = Mask,
        Platform = Platform,
        Flags = Flags,
        Appearance = Appearance?.Clone(),
        Editor = Editor.Clone(),
        Source = Source,
    };
}

/// <summary>An editor layer (like Photoshop layers): visibility and lock for everything on it.</summary>
public sealed class Layer
{
    public Layer(string name) => Name = name;

    public string Name { get; set; }

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    /// <summary>Display colour as #RRGGBB, or null.</summary>
    public string? Color { get; set; }

    public Layer Clone() => (Layer)MemberwiseClone();
}
