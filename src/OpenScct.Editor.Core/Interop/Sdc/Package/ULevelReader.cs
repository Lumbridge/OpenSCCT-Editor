using System.Numerics;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>A GE of the level's partition: kind code (POLE 1, FENCE 2, HOH 4, LG 8, LADDER 16, PIPE 32, ZL 64),
/// flags, platform, ID (activation mask bit) and two or three points.</summary>
public sealed record SdcGe(int Code, int Flags, int Platform, int Id, Vector3[] Points)
{
    public static readonly IReadOnlyDictionary<int, (string Kind, int Points)> Kinds = new Dictionary<int, (string, int)>
    {
        [1] = ("POLE", 2), [2] = ("FENCE", 2), [4] = ("HOH", 2), [8] = ("LG", 2), [16] = ("LADDER", 3), [32] = ("PIPE", 3), [64] = ("ZL", 2),
    };

    public string Kind => Kinds[Code].Kind;
}

/// <summary>The Level export (ULevel::Serialize, SDC-FORMAT.md section 5). Same layout in both copies.</summary>
public sealed class ULevel
{
    /// <summary>Level.Actors (objrefs, an entry can be 0): [0] LevelInfo, in an editor copy [1] the builder brush; the
    /// order is the actor order and the CSG order of the brushes.</summary>
    public IReadOnlyList<int> Actors { get; init; } = [];

    public int ActorsMax { get; init; }

    public string UrlProtocol { get; init; } = "";

    public string UrlHost { get; init; } = "";

    public string UrlMap { get; init; } = "";

    public string UrlPortal { get; init; } = "";

    public IReadOnlyList<string> UrlOptions { get; init; } = [];

    public int UrlPort { get; init; }

    public int UrlValid { get; init; }

    /// <summary>The PS2 world model (ULevel +0x144), 0 when none. Only some editor copies keep one.</summary>
    public int ModelPsx2 { get; init; }

    /// <summary>The PC world model (ULevel +0x14C), 0 when none.</summary>
    public int ModelXbox { get; init; }

    /// <summary>The world the PC game and the stock editor load: ModelXBOX, else ModelPSX2.</summary>
    public int WorldModel => ModelXbox != 0 ? ModelXbox : ModelPsx2;

    public float TimeSeconds { get; init; }

    public int FirstDeleted { get; init; }

    public IReadOnlyList<int> TextBlocks { get; init; } = [];

    public IReadOnlyList<(string Key, string Value)> TravelInfo { get; init; } = [];

    /// <summary>The GE partition's version (4), or 0 when the level has none.</summary>
    public int GePartitionVersion { get; init; }

    public IReadOnlyList<SdcGe> Ges { get; init; } = [];

    /// <summary>The GE partition's bytes as stored (it holds no references or strings, so a writer can copy it when
    /// the GE list is unchanged); empty when the level has none.</summary>
    public byte[] PartitionBytes { get; init; } = [];
}

/// <summary>Reads the Level export, including the GE partition (a port of scctformats <c>ge.py</c>).</summary>
public static class ULevelReader
{
    private const int VerTravelInfo = 0x3E;
    private const int VerGePartition = 0x7B;
    private const int VerGePlatform = 0xAA;
    private const int VerGeId = 0xAE;

    public static UExport Find(UPackage pkg) =>
        pkg.ExportsOfClass("Level").FirstOrDefault(e => e.Size > 0)
        ?? throw new InvalidDataException($"{pkg.Name}: no Level export (not a map)");

    public static ULevel Read(UPackage pkg, UExport e)
    {
        UReader r = pkg.Reader(e);
        return UPolysReader.Exact(pkg, e, r, () => Read(r, pkg.Version));
    }

    private static ULevel Read(UReader r, int version)
    {
        if (r.Name() != "None")
        {
            throw new InvalidDataException("the Level export has tagged properties");
        }

        int num = r.I32(), max = r.I32();
        if (num < 0 || num > r.Remaining)
        {
            throw new InvalidDataException($"bad actor count {num}");
        }

        var actors = new int[num];
        for (int i = 0; i < num; i++)
        {
            actors[i] = r.ObjRef();
        }

        string protocol = r.FString(), host = r.FString(), map = r.FString(), portal = r.FString();
        var options = new string[r.Count(1, "URL options")];
        for (int i = 0; i < options.Length; i++)
        {
            options[i] = r.FString();
        }

        int port = r.I32(), valid = r.I32();
        int psx2 = r.I32() != 0 ? r.ObjRef() : 0;
        int xbox = r.I32() != 0 ? r.ObjRef() : 0;
        float time = r.F32();
        int firstDeleted = r.ObjRef();
        var textBlocks = new int[16];
        for (int i = 0; i < 16; i++)
        {
            textBlocks[i] = r.ObjRef();
        }

        var travel = new List<(string, string)>();
        if (version > VerTravelInfo)
        {
            int n = r.Count(2, "TravelInfo");
            for (int i = 0; i < n; i++)
            {
                travel.Add((r.FString(), r.FString()));
            }
        }

        int partitionVersion = 0;
        var ges = new List<SdcGe>();
        int partitionStart = r.Pos;
        if (version > VerGePartition)
        {
            partitionVersion = ReadPartition(r, version, ges);
        }

        byte[] partition = r.Pos > partitionStart ? r.Bytes(partitionStart, r.Pos - partitionStart) : [];

        return new ULevel
        {
            Actors = actors,
            ActorsMax = max,
            UrlProtocol = protocol,
            UrlHost = host,
            UrlMap = map,
            UrlPortal = portal,
            UrlOptions = options,
            UrlPort = port,
            UrlValid = valid,
            ModelPsx2 = psx2,
            ModelXbox = xbox,
            TimeSeconds = time,
            FirstDeleted = firstDeleted,
            TextBlocks = textBlocks,
            TravelInfo = travel,
            GePartitionVersion = partitionVersion,
            Ges = ges,
            PartitionBytes = partition,
        };
    }

    /// <summary>i32 version (4), bounds, the GEs, the leaf index list and the k-d tree in pre-order.</summary>
    private static int ReadPartition(UReader r, int version, List<SdcGe> ges)
    {
        int partitionVersion = r.I32();
        if (partitionVersion < 4)
        {
            throw new InvalidDataException($"GE partition version {partitionVersion} is older than 4: not supported");
        }

        r.Skip(24); // bounds min and max
        int n = r.Count(30, "GEs");
        for (int i = 0; i < n; i++)
        {
            int code = r.I32();
            if (!SdcGe.Kinds.TryGetValue(code, out (string Kind, int Points) kind))
            {
                throw new InvalidDataException($"GE {i}: unknown kind {code} at {r.Pos - 4}");
            }

            int flags = r.I32();
            int platform = version >= VerGePlatform ? r.I32() : 0;
            int id = version >= VerGeId ? r.U8() : 0;
            var points = new Vector3[kind.Points];
            for (int k = 0; k < points.Length; k++)
            {
                points[k] = r.Vector();
            }

            ges.Add(new SdcGe(code, flags, platform, id, points));
        }

        int m = r.Count(4, "GE indices");
        for (int i = 0; i < m; i++)
        {
            int gi = r.I32();
            if (gi < 0 || gi >= n)
            {
                throw new InvalidDataException($"GE tree index {gi} is outside the GE list ({n})");
            }
        }

        // The k-d tree, pre-order: 1 empty, 2/4/8 a split (f32, then both children), 16 a leaf (i32 first, i32 end).
        for (int pending = 1; pending > 0; pending--)
        {
            int type = r.I32();
            switch (type)
            {
                case 1:
                    break;
                case 16:
                    int first = r.I32(), end = r.I32();
                    if (first < 0 || first > end || end > m)
                    {
                        throw new InvalidDataException($"GE tree leaf {first}..{end} is outside the index list ({m})");
                    }

                    break;
                case 2 or 4 or 8:
                    r.F32();
                    pending += 2;
                    break;
                default:
                    throw new InvalidDataException($"unknown GE tree node type {type} at {r.Pos - 4}");
            }
        }

        return partitionVersion;
    }
}
