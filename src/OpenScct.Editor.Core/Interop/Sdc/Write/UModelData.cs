using System.Numerics;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>One FBspNode (SDC-FORMAT.md section 8). <see cref="Unused"/> is the stack sphere the stock writer never
/// fills: kept as read so a copy is exact.</summary>
public sealed class UBspNode
{
    public Vector4 Plane;
    public ulong ZoneMask;
    public byte NodeFlags;
    public int VertPool, Surf, Back = -1, Front = -1, PlaneLink = -1, CollisionBound = -1, RenderBound = -1;
    public Vector4 ExclusiveSphereBound;
    public byte[] Unused = new byte[16];
    public byte Zone0, Zone1, NumVertices;
    public int Leaf0 = -1, Leaf1 = -1, Section = -1, FirstVertex = -1, LightMap = -1;
}

/// <summary>One FBspSection: the render vertices (40 bytes each: position, normal, UV, lightmap UV) and its
/// trailer.</summary>
public sealed class UBspSection
{
    public byte[] Vertices = [];
    public int VertexCount;
    public int Revision;
    public int Material;
    public int NumNodes;
    public uint PolyFlags;
    public int LightMapTexture = -1;
}

/// <summary>One light of an FLightMap (editor copies only): the light, its visibility bits and seven ints.</summary>
public sealed class ULightMapLight
{
    public int Light;
    public byte[] Bits = [];
    public byte[] Ints = new byte[28];
}

/// <summary>One FLightMap (editor copies only).</summary>
public sealed class ULightMapRecord
{
    public int[] Header = new int[7];
    public byte[] Matrix = new byte[64];
    public byte[] Axes = new byte[36];
    public List<ULightMapLight> Lights = new();
    public int Owner;
    public int Tail;
}

/// <summary>One FLightMapTexture: owner, the lightmaps packed into it, and the FStaticLightMapTexture (two lazy byte
/// arrays, format, size, revision).</summary>
public sealed class ULightMapTexture
{
    public int Owner;
    public List<int> LightMaps = new();
    public ulong U64;
    public int I32;
    public byte[] Data = [];
    public byte[] Data2 = [];
    public int Seek1, Seek2;
    public byte Format = 5;
    public int Width, Height, Revision;
}

/// <summary>
/// A whole UModel export (UModel::Serialize, SDC-FORMAT.md section 8), read and written in either layout: the world
/// model and the models of brushes, volumes and movers. Object references are kept as stored; <see cref="Write"/>
/// maps them through a function so a model copied into a new package points at its new objects.
/// </summary>
public sealed class UModelData
{
    private const uint PortalOrMirror = 0x0C000000;

    public Vector3 BoxMin, BoxMax;
    public bool BoxValid;
    public Vector4 Sphere;
    public List<Vector3> Vectors = new();
    public List<Vector3> Points = new();
    public List<UBspNode> Nodes = new();
    public List<FBspSurf> Surfs = new();
    public List<(int Vertex, int Side)> Verts = new();
    public int NumSharedSides;
    public List<(int Actor, ulong Connectivity, float LastRenderTime)> Zones = new();
    public int Polys;
    public List<(Vector3 Min, Vector3 Max, bool Valid)> Bounds = new();
    public List<int> LeafHulls = new();
    public List<(int Zone, int Permeating, int Volumetric)> Leaves = new();
    public List<int> Lights = new();
    public bool RootOutside;
    public bool Linked;
    public List<UBspSection> Sections = new();
    public List<ULightMapRecord> LightMaps = new();
    public List<ULightMapTexture> LightMapTextures = new();

    public static UModelData Read(UPackage pkg, UExport e, SdcLayout layout)
    {
        UReader r = pkg.Reader(e);
        return UPolysReader.Exact(pkg, e, r, () => Read(r, e.Flags, layout));
    }

    private static UModelData Read(UReader r, uint flags, SdcLayout layout)
    {
        bool editor = layout == SdcLayout.Editor;
        var m = new UModelData();
        UPropertyReader.SkipStateFrame(r, flags);
        UPropertyReader.SkipTagged(r);
        (m.BoxMin, m.BoxMax, m.BoxValid) = r.Box();
        m.Sphere = r.Vector4();
        int n = r.Count(12, "Vectors");
        for (int i = 0; i < n; i++)
        {
            m.Vectors.Add(r.Vector());
        }

        n = r.Count(12, "Points");
        for (int i = 0; i < n; i++)
        {
            m.Points.Add(r.Vector());
        }

        n = r.Count(62, "Nodes");
        for (int i = 0; i < n; i++)
        {
            var node = new UBspNode
            {
                Plane = r.Vector4(),
                ZoneMask = r.U64(),
                NodeFlags = r.U8(),
                VertPool = r.Compact(),
                Surf = r.Compact(),
                Back = r.Compact(),
                Front = r.Compact(),
                PlaneLink = r.Compact(),
                CollisionBound = r.Compact(),
                RenderBound = r.Compact(),
                ExclusiveSphereBound = r.Vector4(),
                Unused = r.Span(16).ToArray(),
                Zone0 = r.U8(),
                Zone1 = r.U8(),
                NumVertices = r.U8(),
                Leaf0 = r.I32(),
                Leaf1 = r.I32(),
                Section = r.I32(),
                FirstVertex = r.I32(),
                LightMap = r.I32(),
            };
            m.Nodes.Add(node);
        }

        n = r.Count(30, "Surfs");
        for (int i = 0; i < n; i++)
        {
            int material = r.ObjRef();
            uint polyFlags = r.U32();
            int pBase = r.Compact(), vNormal = r.Compact(), vU = r.Compact(), vV = r.Compact();
            Vector4 plane = r.Vector4();
            float lmsPsx2 = r.F32(), lmsXbox = r.F32();
            FPoly? poly = null;
            int actor = 0, brushPoly = -1;
            if (editor || (polyFlags & PortalOrMirror) != 0)
            {
                poly = FPoly.Read(r);
                if (editor)
                {
                    actor = r.ObjRef();
                    brushPoly = r.Compact();
                }
            }

            m.Surfs.Add(new FBspSurf(material, polyFlags, pBase, vNormal, vU, vV, plane, lmsPsx2, lmsXbox, poly, actor, brushPoly));
        }

        n = r.Count(2, "Verts");
        for (int i = 0; i < n; i++)
        {
            m.Verts.Add((r.Compact(), r.Compact()));
        }

        m.NumSharedSides = r.I32();
        int zones = r.I32();
        if (zones is < 0 or > 64)
        {
            throw new InvalidDataException($"bad zone count {zones} at {r.Pos - 4}");
        }

        for (int i = 0; i < zones; i++)
        {
            m.Zones.Add((r.ObjRef(), r.U64(), r.F32()));
        }

        m.Polys = r.ObjRef();
        n = r.Count(25, "Bounds");
        for (int i = 0; i < n; i++)
        {
            m.Bounds.Add(r.Box());
        }

        n = r.Count(4, "LeafHulls");
        for (int i = 0; i < n; i++)
        {
            m.LeafHulls.Add(r.I32());
        }

        n = r.Count(3, "Leaves");
        for (int i = 0; i < n; i++)
        {
            m.Leaves.Add((r.Compact(), r.Compact(), r.Compact()));
        }

        n = r.Count(1, "Lights");
        for (int i = 0; i < n; i++)
        {
            m.Lights.Add(r.ObjRef());
        }

        int rootOutside = r.I32(), linked = r.I32();
        if (rootOutside is not (0 or 1) || linked is not (0 or 1))
        {
            throw new InvalidDataException($"RootOutside/Linked are not booleans at {r.Pos - 8}");
        }

        m.RootOutside = rootOutside == 1;
        m.Linked = linked == 1;
        n = r.Count(18, "Sections");
        for (int i = 0; i < n; i++)
        {
            int count = r.Count(40, "section vertices");
            m.Sections.Add(new UBspSection
            {
                VertexCount = count,
                Vertices = r.Span(40 * count).ToArray(),
                Revision = r.I32(),
                Material = r.ObjRef(),
                NumNodes = r.I32(),
                PolyFlags = r.U32(),
                LightMapTexture = r.I32(),
            });
        }

        if (editor)
        {
            n = r.Count(100, "LightMaps");
            for (int i = 0; i < n; i++)
            {
                var lm = new ULightMapRecord();
                for (int k = 0; k < 7; k++)
                {
                    lm.Header[k] = r.Compact();
                }

                lm.Matrix = r.Span(64).ToArray();
                lm.Axes = r.Span(36).ToArray();
                int lights = r.Count(30, "lightmap lights");
                for (int k = 0; k < lights; k++)
                {
                    int light = r.ObjRef();
                    byte[] bits = r.Span(r.Count(1, "visibility bits")).ToArray();
                    lm.Lights.Add(new ULightMapLight { Light = light, Bits = bits, Ints = r.Span(28).ToArray() });
                }

                lm.Owner = r.ObjRef();
                lm.Tail = r.I32();
                m.LightMaps.Add(lm);
            }
        }

        n = r.Count(20, "LightMapTextures");
        for (int i = 0; i < n; i++)
        {
            var t = new ULightMapTexture { Owner = r.ObjRef() };
            int ids = r.Count(4, "lightmap ids");
            for (int k = 0; k < ids; k++)
            {
                t.LightMaps.Add(r.I32());
            }

            t.U64 = r.U64();
            t.I32 = r.I32();
            t.Seek1 = r.I32();
            t.Data = r.Span(r.Count(1, "lightmap data")).ToArray();
            t.Seek2 = r.I32();
            t.Data2 = r.Span(r.Count(1, "lightmap data")).ToArray();
            t.Format = r.U8();
            t.Width = r.I32();
            t.Height = r.I32();
            t.Revision = r.I32();
            m.LightMapTextures.Add(t);
        }

        return m;
    }

    /// <summary>Writes the model in <paramref name="layout"/>; every object reference goes through
    /// <paramref name="map"/>. A cooked copy drops the surfs' brush links and polys (but on portals and mirrors) and
    /// the lightmap records.</summary>
    public void Write(UWriter w, SdcLayout layout, Func<int, int> map)
    {
        bool editor = layout == SdcLayout.Editor;
        w.Name("None");
        w.Box(BoxMin, BoxMax, BoxValid);
        w.Vector4(Sphere);
        w.Compact(Vectors.Count);
        Vectors.ForEach(w.Vector);
        w.Compact(Points.Count);
        Points.ForEach(w.Vector);
        w.Compact(Nodes.Count);
        foreach (UBspNode n in Nodes)
        {
            w.Vector4(n.Plane);
            w.U64(n.ZoneMask);
            w.U8(n.NodeFlags);
            w.Compact(n.VertPool);
            w.Compact(n.Surf);
            w.Compact(n.Back);
            w.Compact(n.Front);
            w.Compact(n.PlaneLink);
            w.Compact(n.CollisionBound);
            w.Compact(n.RenderBound);
            w.Vector4(n.ExclusiveSphereBound);
            w.Bytes(n.Unused);
            w.U8(n.Zone0);
            w.U8(n.Zone1);
            w.U8(n.NumVertices);
            w.I32(n.Leaf0);
            w.I32(n.Leaf1);
            w.I32(n.Section);
            w.I32(n.FirstVertex);
            w.I32(n.LightMap);
        }

        w.Compact(Surfs.Count);
        foreach (FBspSurf s in Surfs)
        {
            w.ObjRef(map(s.Material));
            w.U32(s.PolyFlags);
            w.Compact(s.Base);
            w.Compact(s.Normal);
            w.Compact(s.TextureU);
            w.Compact(s.TextureV);
            w.Vector4(s.Plane);
            w.F32(s.LightMapScalePsx2);
            w.F32(s.LightMapScaleXbox);
            if (editor || (s.PolyFlags & PortalOrMirror) != 0)
            {
                WritePoly(w, s.Poly ?? EmptyPoly, map);
                if (editor)
                {
                    w.ObjRef(map(s.Actor));
                    w.Compact(s.BrushPoly);
                }
            }
        }

        w.Compact(Verts.Count);
        foreach ((int vertex, int side) in Verts)
        {
            w.Compact(vertex);
            w.Compact(side);
        }

        w.I32(NumSharedSides);
        w.I32(Zones.Count);
        foreach ((int actor, ulong connectivity, float time) in Zones)
        {
            w.ObjRef(map(actor));
            w.U64(connectivity);
            w.F32(time);
        }

        w.ObjRef(map(Polys));
        w.Compact(Bounds.Count);
        foreach ((Vector3 min, Vector3 max, bool valid) in Bounds)
        {
            w.Box(min, max, valid);
        }

        w.Compact(LeafHulls.Count);
        LeafHulls.ForEach(w.I32);
        (List<int> lights, int[] at) = KeptLights(map);
        w.Compact(Leaves.Count);
        foreach ((int zone, int permeating, int volumetric) in Leaves)
        {
            w.Compact(zone);
            w.Compact(permeating >= 0 && permeating <= Lights.Count ? at[permeating] : permeating);
            w.Compact(volumetric >= 0 && volumetric <= Lights.Count ? at[volumetric] : volumetric);
        }

        w.Compact(lights.Count);
        lights.ForEach(w.ObjRef);

        w.I32(RootOutside ? 1 : 0);
        w.I32(Linked ? 1 : 0);
        w.Compact(Sections.Count);
        foreach (UBspSection s in Sections)
        {
            w.Compact(s.VertexCount);
            w.Bytes(s.Vertices);
            w.I32(s.Revision);
            w.ObjRef(map(s.Material));
            w.I32(s.NumNodes);
            w.U32(s.PolyFlags);
            w.I32(s.LightMapTexture);
        }

        if (editor)
        {
            w.Compact(LightMaps.Count);
            foreach (ULightMapRecord lm in LightMaps)
            {
                foreach (int h in lm.Header)
                {
                    w.Compact(h);
                }

                w.Bytes(lm.Matrix);
                w.Bytes(lm.Axes);
                List<ULightMapLight> kept = lm.Lights.Where(l => l.Light == 0 || map(l.Light) != 0).ToList();
                w.Compact(kept.Count);
                foreach (ULightMapLight l in kept)
                {
                    w.ObjRef(map(l.Light));
                    w.Compact(l.Bits.Length);
                    w.Bytes(l.Bits);
                    w.Bytes(l.Ints);
                }

                w.ObjRef(map(lm.Owner));
                w.I32(lm.Tail);
            }
        }

        w.Compact(LightMapTextures.Count);
        foreach (ULightMapTexture t in LightMapTextures)
        {
            w.ObjRef(map(t.Owner));
            w.Compact(t.LightMaps.Count);
            t.LightMaps.ForEach(w.I32);
            w.U64(t.U64);
            w.I32(t.I32);
            LazyArray(w, t.Seek1, t.Data);
            LazyArray(w, t.Seek2, t.Data2);
            w.U8(t.Format);
            w.I32(t.Width);
            w.I32(t.Height);
            w.I32(t.Revision);
        }
    }

    /// <summary>
    /// The per-leaf light lists (each leaf's Permeating and Volumetric index the first of its lights, the list ends at
    /// a None) with every light this write leaves out taken out of its list rather than written as None, which would
    /// end the list early; <c>at</c> maps a stored index to the written one.
    /// </summary>
    private (List<int> Lights, int[] At) KeptLights(Func<int, int> map)
    {
        var lights = new List<int>(Lights.Count);
        int[] at = new int[Lights.Count + 1];
        for (int i = 0; i < Lights.Count; i++)
        {
            at[i] = lights.Count;
            int mapped = Lights[i] == 0 ? 0 : map(Lights[i]);
            if (Lights[i] == 0 || mapped != 0)
            {
                lights.Add(mapped);
            }
        }

        at[Lights.Count] = lights.Count;
        return (lights, at);
    }

    /// <summary>A TLazyArray: its seek field (0 as the stock editor leaves it in maps), the count and the bytes.</summary>
    private static void LazyArray(UWriter w, int seek, byte[] data)
    {
        w.I32(seek == 0 ? 0 : w.Pos + 4 + UWriter.CompactSize(data.Length) + data.Length);
        w.Compact(data.Length);
        w.Bytes(data);
    }

    private static readonly FPoly EmptyPoly = new(default, default, default, default, [], 0, 0, 0, "None", -1, -1, 32, 32);

    /// <summary>An FPoly (the same record in surfs and in Polys exports), its references mapped.</summary>
    public static void WritePoly(UWriter w, FPoly p, Func<int, int> map)
    {
        w.Compact(p.Vertices.Length);
        w.Vector(p.Base);
        w.Vector(p.Normal);
        w.Vector(p.TextureU);
        w.Vector(p.TextureV);
        foreach (Vector3 v in p.Vertices)
        {
            w.Vector(v);
        }

        w.U32(p.PolyFlags);
        w.ObjRef(map(p.Actor));
        w.ObjRef(map(p.Material));
        w.Name(p.ItemName.Length == 0 ? "None" : p.ItemName);
        w.Compact(p.Link);
        w.Compact(p.BrushPoly);
        w.F32(p.LightMapScalePsx2);
        w.F32(p.LightMapScaleXbox);
    }

    /// <summary>A UPolys export's data: "None", i32 Num, i32 Max, the polys.</summary>
    public static void WritePolys(UWriter w, IReadOnlyList<FPoly> polys, Func<int, int> map)
    {
        w.Name("None");
        w.I32(polys.Count);
        w.I32(polys.Count);
        foreach (FPoly p in polys)
        {
            WritePoly(w, p, map);
        }
    }
}
