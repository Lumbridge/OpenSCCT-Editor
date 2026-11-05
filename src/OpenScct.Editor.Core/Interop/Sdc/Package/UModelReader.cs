using System.Numerics;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Which serializer layout a package was saved with (SDC-FORMAT.md section 7). Nothing in the file records
/// it; <see cref="SdcLayoutDetector.Detect"/> tells them apart.</summary>
public enum SdcLayout
{
    /// <summary>The stock editor's own copy (Packages\MapsEd, autosaves): brushes, surf links and lightmap records.</summary>
    Editor,

    /// <summary>The copy the game loads (Packages\Maps), saved for a platform target.</summary>
    Cooked,
}

/// <summary>One FBspSurf. <see cref="Poly"/> is set on every surf of an editor copy (its source brush face in world
/// space) and on portal or mirror surfs of a cooked copy; <see cref="Actor"/> and <see cref="BrushPoly"/> (the brush
/// and the index into its Polys) only in an editor copy.</summary>
public sealed record FBspSurf(int Material, uint PolyFlags, int Base, int Normal, int TextureU, int TextureV, Vector4 Plane,
    float LightMapScalePsx2, float LightMapScaleXbox, FPoly? Poly, int Actor, int BrushPoly);

/// <summary>One FBspNode as the lighting needs it (SDC-FORMAT.md section 8). <see cref="Sphere"/> is the
/// ExclusiveSphereBound (xyz centre, w radius); <see cref="Back"/>, <see cref="Front"/> and <see cref="Coplanar"/> are
/// node indices and the leaves leaf indices, -1 for none.</summary>
public sealed record FBspNode(Vector4 Plane, Vector4 Sphere, int VertPool, int Surf, int Back, int Front, int Coplanar,
    byte BackZone, byte FrontZone, byte NumVertices, int BackLeaf, int FrontLeaf, int LightMap);

/// <summary>One FLightMap of an editor copy: its lightmap texture, surf and zone, its place and size in the texture,
/// and the lights the editor baked into it (objrefs).</summary>
public sealed record FLightMapRecord(int Texture, int Surf, int Zone, int X, int Y, int Width, int Height, IReadOnlyList<int> Lights);

/// <summary>What a Model read keeps.</summary>
public enum UModelDetail
{
    /// <summary>Counts only.</summary>
    Summary,

    /// <summary>Counts and the surfs.</summary>
    Surfs,

    /// <summary>The surfs and what the static lighting needs as well: nodes, points, verts, leaves, the light table and
    /// an editor copy's lightmap light lists.</summary>
    Lighting,
}

/// <summary>A UModel export (UModel::Serialize, SDC-FORMAT.md section 8): counts of every array, the zone table,
/// the Polys reference, the surfs unless read as a summary, and the lighting tables when read for them.</summary>
public sealed class UModel
{
    public int Vectors { get; init; }

    public int Points { get; init; }

    public int Nodes { get; init; }

    public int SurfCount { get; init; }

    /// <summary>The surfs (empty when read as a summary).</summary>
    public IReadOnlyList<FBspSurf> Surfs { get; init; } = [];

    public int Verts { get; init; }

    public int NumSharedSides { get; init; }

    /// <summary>The zone actors (objrefs) of the zone table.</summary>
    public IReadOnlyList<int> Zones { get; init; } = [];

    /// <summary>The model's UPolys export (objref; 0 when none).</summary>
    public int Polys { get; init; }

    public int Bounds { get; init; }

    public int LeafHulls { get; init; }

    public int Leaves { get; init; }

    public int Lights { get; init; }

    public bool RootOutside { get; init; }

    public bool Linked { get; init; }

    public int Sections { get; init; }

    /// <summary>FLightMap records (editor copies only; 0 in a cooked copy).</summary>
    public int LightMaps { get; init; }

    public int LightMapTextures { get; init; }

    /// <summary>The nodes (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<FBspNode> NodeList { get; init; } = [];

    /// <summary>The points (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<Vector3> PointList { get; init; } = [];

    /// <summary>Each vert's point index (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<int> VertPoints { get; init; } = [];

    /// <summary>Each leaf's zone and the start of its run in <see cref="LightList"/>, -1 for none
    /// (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<(int Zone, int Permeating)> LeafList { get; init; } = [];

    /// <summary>The light table: objrefs, each leaf's run ending at a 0 (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<int> LightList { get; init; } = [];

    /// <summary>An editor copy's lightmaps with their surfs and lights (<see cref="UModelDetail.Lighting"/> only).</summary>
    public IReadOnlyList<FLightMapRecord> LightMapList { get; init; } = [];

    /// <summary>The baked lightmap atlases (read with <c>lighting: true</c>; empty otherwise).</summary>
    public IReadOnlyList<LightMapAtlas> Atlases { get; init; } = [];

    /// <summary>The lights the FLightMap records list (objrefs; editor copies read with <c>lighting: true</c>): the
    /// lights the last LIGHT APPLY baked into the BSP, each with a shadow bitmap of its own.</summary>
    public IReadOnlySet<int> BakedLights { get; init; } = new HashSet<int>();
}

/// <summary>One FLightMapTexture's atlas (SDC-FORMAT.md section 8): the lightmap ids packed into it and its texels,
/// BGRA8, as the cook wrote them.</summary>
public sealed record LightMapAtlas(IReadOnlyList<int> LightMapIds, int Width, int Height, int Format, byte[] Data)
{
    /// <summary>SHA-1 of the texels: the same bake gives the same bytes in the editor copy and the cooked copy.</summary>
    public string Hash => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Data));
}

/// <summary>Reads Model exports in one layout. Every read must end on the export's last byte.</summary>
public sealed class UModelReader
{
    private const uint PortalOrMirror = 0x0C000000;

    public UModelReader(SdcLayout layout) => Layout = layout;

    public SdcLayout Layout { get; }

    /// <param name="summary">Skip the surf records' payload (their sizes still have to be walked) and keep counts only.</param>
    /// <param name="lighting">Keep the lightmap atlases' texels and the lights the FLightMap records list.</param>
    public UModel Read(UPackage pkg, UExport e, bool summary = false, bool lighting = false) =>
        Read(pkg, e, summary ? UModelDetail.Summary : UModelDetail.Surfs, lighting);

    /// <param name="bake">Keep the lightmap atlases' texels and the lights the FLightMap records list as well.</param>
    public UModel Read(UPackage pkg, UExport e, UModelDetail detail, bool bake = false)
    {
        UReader r = pkg.Reader(e);
        return UPolysReader.Exact(pkg, e, r, () => Read(r, e.Flags, detail, bake));
    }

    private UModel Read(UReader r, uint flags, UModelDetail detail, bool bake)
    {
        bool editor = Layout == SdcLayout.Editor;
        bool summary = detail == UModelDetail.Summary, lighting = detail == UModelDetail.Lighting;
        UPropertyReader.SkipStateFrame(r, flags);
        UPropertyReader.SkipTagged(r);
        r.Skip(25 + 16); // FBox BoundingBox, FSphere BoundingSphere (UPrimitive)
        int vectors = r.Count(12, "Vectors");
        r.Skip(12 * vectors);
        int points = r.Count(12, "Points");
        var pointList = lighting ? new Vector3[points] : null;
        if (pointList is null)
        {
            r.Skip(12 * points);
        }
        else
        {
            for (int i = 0; i < points; i++)
            {
                pointList[i] = r.Vector();
            }
        }

        int nodes = r.Count(62, "Nodes");
        var nodeList = lighting ? new FBspNode[nodes] : null;
        for (int i = 0; i < nodes; i++)
        {
            if (nodeList is null)
            {
                r.Skip(25); // FPlane, u64 ZoneMask, u8 NodeFlags
                for (int k = 0; k < 7; k++)
                {
                    r.Compact();
                }

                // ExclusiveSphereBound, an unused stack sphere, iZone[2], NumVertices, iLeaf[2], iSection, iFirstVertex, iLightMap
                r.Skip(16 + 16 + 3 + 8 + 12);
                continue;
            }

            Vector4 plane = r.Vector4();
            r.Skip(9); // u64 ZoneMask, u8 NodeFlags
            int vertPool = r.Compact(), surf = r.Compact(), back = r.Compact(), front = r.Compact(), coplanar = r.Compact();
            r.Compact(); // iCollisionBound
            r.Compact(); // iRenderBound
            Vector4 sphere = r.Vector4();
            r.Skip(16); // the unused stack sphere
            byte backZone = r.U8(), frontZone = r.U8(), numVertices = r.U8();
            int backLeaf = r.I32(), frontLeaf = r.I32();
            r.Skip(8); // iSection, iFirstVertex
            nodeList[i] = new FBspNode(plane, sphere, vertPool, surf, back, front, coplanar, backZone, frontZone, numVertices, backLeaf, frontLeaf,
                r.I32());
        }

        int surfCount = r.Count(30, "Surfs");
        var surfs = summary ? null : new List<FBspSurf>(surfCount);
        for (int i = 0; i < surfCount; i++)
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

            surfs?.Add(new FBspSurf(material, polyFlags, pBase, vNormal, vU, vV, plane, lmsPsx2, lmsXbox, poly, actor, brushPoly));
        }

        int verts = r.Count(2, "Verts");
        var vertPoints = lighting ? new int[verts] : null;
        for (int i = 0; i < verts; i++)
        {
            int point = r.Compact();
            r.Compact(); // iSide
            if (vertPoints is not null)
            {
                vertPoints[i] = point;
            }
        }

        int sharedSides = r.I32();
        int numZones = r.I32();
        if (numZones is < 0 or > 64)
        {
            throw new InvalidDataException($"bad zone count {numZones} at {r.Pos - 4}");
        }

        var zones = new int[numZones];
        for (int i = 0; i < numZones; i++)
        {
            zones[i] = r.ObjRef(); // ZoneActor, then u64 Connectivity, f32 LastRenderTime
            r.Skip(12);
        }

        int polys = r.ObjRef();
        int bounds = r.Count(25, "Bounds");
        r.Skip(25 * bounds);
        int hulls = r.Count(4, "LeafHulls");
        r.Skip(4 * hulls);
        int leaves = r.Count(3, "Leaves");
        var leafList = lighting ? new (int, int)[leaves] : null;
        for (int i = 0; i < leaves; i++)
        {
            int zone = r.Compact(), permeating = r.Compact();
            r.Compact(); // iVolumetric
            if (leafList is not null)
            {
                leafList[i] = (zone, permeating);
            }
        }

        int lights = r.Count(1, "Lights");
        var lightList = lighting ? new int[lights] : null;
        for (int i = 0; i < lights; i++)
        {
            int light = r.ObjRef();
            if (lightList is not null)
            {
                lightList[i] = light;
            }
        }

        int rootOutside = r.I32(), linked = r.I32();
        if (rootOutside is not (0 or 1) || linked is not (0 or 1))
        {
            throw new InvalidDataException($"RootOutside/Linked are not booleans at {r.Pos - 8}");
        }

        int sections = r.Count(18, "Sections");
        for (int i = 0; i < sections; i++)
        {
            int n = r.Count(40, "section vertices");
            r.Skip(40 * n);
            r.I32();
            r.ObjRef();
            r.I32();
            r.U32();
            r.I32();
        }

        var lightMapList = lighting ? new List<FLightMapRecord>() : null;
        var bakedLights = new HashSet<int>();
        int lightMaps = editor ? ReadLightMaps(r, lightMapList, bake ? bakedLights : null) : 0;
        int textures = r.Count(20, "LightMapTextures");
        var atlases = new List<LightMapAtlas>();
        for (int i = 0; i < textures; i++)
        {
            r.ObjRef();
            int ids = r.Count(4, "lightmap ids");
            var idList = new int[ids];
            for (int k = 0; k < ids; k++)
            {
                idList[k] = r.I32();
            }

            r.Skip(8 + 4);
            // Two lazy arrays: the texels, then one the cook leaves empty.
            byte[]? data = null;
            for (int k = 0; k < 2; k++)
            {
                r.I32();
                int n = r.Count(1, "lightmap data");
                if (k == 0 && bake)
                {
                    data = r.Span(n).ToArray();
                }
                else
                {
                    r.Skip(n);
                }
            }

            int format = r.U8();
            int width = r.I32(), height = r.I32();
            r.I32();
            if (data is not null && data.Length == width * height * 4)
            {
                atlases.Add(new LightMapAtlas(idList, width, height, format, data));
            }
        }

        return new UModel
        {
            Vectors = vectors,
            Points = points,
            Nodes = nodes,
            SurfCount = surfCount,
            Surfs = surfs ?? [],
            Verts = verts,
            NumSharedSides = sharedSides,
            Zones = zones,
            Polys = polys,
            Bounds = bounds,
            LeafHulls = hulls,
            Leaves = leaves,
            Lights = lights,
            RootOutside = rootOutside == 1,
            Linked = linked == 1,
            Sections = sections,
            LightMaps = lightMaps,
            LightMapTextures = textures,
            NodeList = nodeList ?? [],
            PointList = pointList ?? [],
            VertPoints = vertPoints ?? [],
            LeafList = leafList ?? [],
            LightList = lightList ?? [],
            LightMapList = lightMapList ?? [],
            Atlases = atlases,
            BakedLights = bakedLights,
        };
    }

    /// <summary>The editor-only FLightMap array; with <paramref name="list"/>, each lightmap's record, and with
    /// <paramref name="baked"/>, the lights its records list.</summary>
    private static int ReadLightMaps(UReader r, List<FLightMapRecord>? list, HashSet<int>? baked)
    {
        int n = r.Count(100, "LightMaps");
        for (int i = 0; i < n; i++)
        {
            int texture = r.Compact(), surf = r.Compact(), zone = r.Compact(), x = r.Compact(), y = r.Compact(), width = r.Compact(), height = r.Compact();

            r.SkipMatrix();
            r.Skip(36);
            int lights = r.Count(30, "lightmap lights");
            var refs = new int[lights];
            for (int k = 0; k < lights; k++)
            {
                refs[k] = r.ObjRef();
                if (refs[k] > 0)
                {
                    baked?.Add(refs[k]);
                }

                r.Skip(r.Count(1, "visibility bits"));
                r.Skip(28);
            }

            list?.Add(new FLightMapRecord(texture, surf, zone, x, y, width, height, refs));

            r.ObjRef();
            r.I32();
        }

        return n;
    }
}

/// <summary>Tells an editor copy from a cooked one (SDC-FORMAT.md section 7): the smallest Model export parses in
/// exactly one layout. Folders and names are never trusted.</summary>
public static class SdcLayoutDetector
{
    public static SdcLayout Detect(UPackage pkg)
    {
        UExport? smallest = pkg.ExportsOfClass("Model").Where(e => e.Size > 0).MinBy(e => e.Size);
        if (smallest is null)
        {
            return SdcLayout.Cooked; // no BSP at all: the layout does not matter
        }

        bool editor = Fits(pkg, smallest, SdcLayout.Editor);
        bool cooked = Fits(pkg, smallest, SdcLayout.Cooked);
        return (editor, cooked) switch
        {
            (true, false) => SdcLayout.Editor,
            (false, true) => SdcLayout.Cooked,
            _ => throw new InvalidDataException(
                $"{pkg.Name}: model {smallest.Name} (export {smallest.Index}) parses in {(editor ? "both layouts" : "neither layout")}; the package layout is unknown"),
        };
    }

    private static bool Fits(UPackage pkg, UExport model, SdcLayout layout)
    {
        try
        {
            new UModelReader(layout).Read(pkg, model, summary: true);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
