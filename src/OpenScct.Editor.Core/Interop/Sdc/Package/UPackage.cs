using System.Buffers.Binary;
using System.Text;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>An import record: ClassPackage, ClassName, Outer (objref form), ObjectName.</summary>
public sealed record UImport(string ClassPackage, string ClassName, int Outer, string Name);

/// <summary>An export record (SDC-FORMAT.md section 3). <see cref="Index"/> is 0-based; its objref is Index + 1.</summary>
public sealed record UExport(int Index, int ClassRef, int SuperRef, int Outer, string Name, uint Flags, int Size, int Offset)
{
    public const uint HasStack = 0x02000000;
}

/// <summary>
/// An unpacked SCCT package (version 300, or 175 for two community maps): the summary, the name, import and export
/// tables, object paths and readers over export data (SDC-FORMAT.md sections 2-4). Autosaves whose summary was never
/// patched (all counts 0) are recovered from the tables at the end of the file.
/// </summary>
public sealed class UPackage
{
    private UPackage(string name, byte[] data, int chunks)
    {
        Name = name;
        Data = data;
        Chunks = chunks;
    }

    /// <summary>The package name: the file name without extension (object paths start with it).</summary>
    public string Name { get; }

    public byte[] Data { get; }

    /// <summary>zlib chunks in the file (0 for a raw package).</summary>
    public int Chunks { get; }

    public int Version { get; private set; }

    public int Licensee { get; private set; }

    public uint Flags { get; private set; }

    /// <summary>True when names and strings are XOR-obfuscated by offset (every .sdc of the corpus).</summary>
    public bool Obfuscated { get; private set; }

    /// <summary>Set when the summary held no counts and the tables were found by scanning (unpatched autosave).</summary>
    public bool Recovered { get; private set; }

    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>The flags stored with each name (a writer copying the table keeps them).</summary>
    public IReadOnlyList<uint> NameFlags => _nameFlags;

    private readonly List<uint> _nameFlags = new();

    private int[]? _secondCompacts;

    /// <summary>The second compact stored with each name, as far as reads have met it (0 where not met): the loader
    /// drops it, the stock editor writes its session's global name index there. A writer keeps it so a package
    /// written back matches its source byte for byte.</summary>
    public int SecondCompact(int nameIndex) => _secondCompacts is { } s && nameIndex < s.Length ? s[nameIndex] : 0;

    internal void NoteSecondCompact(int nameIndex, int value)
    {
        _secondCompacts ??= new int[Names.Count];
        if (nameIndex < _secondCompacts.Length && _secondCompacts[nameIndex] == 0)
        {
            _secondCompacts[nameIndex] = value;
        }
    }

    public IReadOnlyList<UImport> Imports { get; private set; } = [];

    public IReadOnlyList<UExport> Exports { get; private set; } = [];

    /// <summary>Loads a package file. <paramref name="name"/> overrides the package name (the file name): the game
    /// names a package after its file, so a map staged under another file name roots its object paths there.</summary>
    public static UPackage Load(string path, string? name = null)
    {
        (byte[] data, int chunks) = SdcContainer.Unwrap(File.ReadAllBytes(path));
        return Parse(name ?? Path.GetFileNameWithoutExtension(path), data, chunks);
    }

    public static UPackage Parse(string name, byte[] data, int chunks = 0)
    {
        var pkg = new UPackage(name, data, chunks);
        try
        {
            pkg.ReadTables();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{name}: unreadable package tables ({ex.Message})", ex);
        }

        return pkg;
    }

    // ---- lookups ---------------------------------------------------------------------------------------------

    /// <summary>The export an object reference names; a reference outside the export table is a damaged package.</summary>
    public UExport Export(int objref) => objref > 0 && objref <= Exports.Count
        ? Exports[objref - 1]
        : throw new InvalidDataException($"{Name}: object reference {objref} is not one of the {Exports.Count} exports");

    /// <summary>The import an object reference names; a reference outside the import table is a damaged package.</summary>
    public UImport Import(int objref) => objref < 0 && -objref <= Imports.Count
        ? Imports[-objref - 1]
        : throw new InvalidDataException($"{Name}: object reference {objref} is not one of the {Imports.Count} imports");

    /// <summary>A reader over an export's data (empty for a size-0 export).</summary>
    public UReader Reader(UExport e) => e.Size <= 0 ? new UReader(Data, 0, 0, this) : new UReader(Data, e.Offset, e.Offset + e.Size, this);

    /// <summary>The name of an object reference ("None" for 0).</summary>
    public string ObjectName(int objref) => objref switch
    {
        0 => "None",
        > 0 => Export(objref).Name,
        _ => Import(objref).Name,
    };

    /// <summary>
    /// The dotted path of an object reference: an export is rooted at this package (<c>AeroD.Light12</c>); an import's
    /// outer chain already ends at its package (<c>Engine.S_Light</c>). "" for 0.
    /// </summary>
    public string PathOf(int objref)
    {
        if (objref == 0)
        {
            return "";
        }

        var parts = new List<string>();
        int r = objref;
        for (int guard = 0; r < 0 && guard < 32; guard++)
        {
            UImport i = Import(r);
            parts.Add(i.Name);
            r = i.Outer;
        }

        for (int guard = 0; r > 0 && guard < 32; guard++)
        {
            UExport e = Export(r);
            parts.Add(e.Name);
            r = e.Outer;
            if (r <= 0)
            {
                parts.Add(Name);
            }
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>The class of an export as (package, class): class ref 0 is Core.Class; a map-local class is this package's.</summary>
    public (string Package, string Class) ClassOf(UExport e)
    {
        if (e.ClassRef == 0)
        {
            return ("Core", "Class");
        }

        if (e.ClassRef > 0)
        {
            return (Name, Export(e.ClassRef).Name);
        }

        UImport imp = Import(e.ClassRef);
        return (imp.Outer < 0 ? Import(imp.Outer).Name : "", imp.Name);
    }

    public string ClassName(UExport e) => ClassOf(e).Class;

    public IEnumerable<UExport> ExportsOfClass(string className) =>
        Exports.Where(e => ClassName(e).Equals(className, StringComparison.OrdinalIgnoreCase));

    /// <summary>The index of a name, ignoring case (exact case wins), or -1.</summary>
    public int FindName(string name)
    {
        int fallback = -1;
        for (int i = 0; i < Names.Count; i++)
        {
            if (Names[i] == name)
            {
                return i;
            }

            if (fallback < 0 && Names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                fallback = i;
            }
        }

        return fallback;
    }

    // ---- tables ----------------------------------------------------------------------------------------------

    private void ReadTables()
    {
        byte[] d = Data;
        if (d.Length < 64 || BinaryPrimitives.ReadUInt32LittleEndian(d) != SdcContainer.PackageTag)
        {
            throw new InvalidDataException($"{Name}: not an Unreal package");
        }

        Version = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4));
        Licensee = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6));
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
        int nameCount = I32(12), nameOffset = I32(16), exportCount = I32(20), exportOffset = I32(24);
        int importCount = I32(28), importOffset = I32(32);
        if (nameCount == 0 && nameOffset == 0 && exportCount == 0 && importCount == 0)
        {
            RecoverTables();
            return;
        }

        if (nameCount < 0 || exportCount < 0 || importCount < 0)
        {
            throw new InvalidDataException($"{Name}: negative table counts in the summary");
        }

        Obfuscated = !IsPlainName(d, nameOffset);
        Names = ReadNames(new UReader(d, nameOffset, d.Length, this), nameCount);
        Imports = ReadImports(new UReader(d, importOffset, d.Length, this), importCount);
        Exports = ReadExports(new UReader(d, exportOffset, d.Length, this), exportCount, Imports.Count);
    }

    private int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at));

    /// <summary>A plain (not obfuscated) name table starts with printable text ending in a NUL.</summary>
    private static bool IsPlainName(byte[] d, int offset)
    {
        var r = new UReader(d, offset, d.Length, null);
        int n = r.Compact();
        if (n is <= 1 or > 1024 || n > r.Remaining)
        {
            return false;
        }

        ReadOnlySpan<byte> s = r.Span(n);
        return s[^1] == 0 && !s[..^1].ContainsAnyExceptInRange((byte)32, (byte)126);
    }

    private List<string> ReadNames(UReader r, int count)
    {
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int at = r.Pos;
            int n = r.Compact();
            if (n is <= 0 or > 1024)
            {
                throw new InvalidDataException($"{Name}: bad name length {n} at {at}");
            }

            names.Add(DecodeName(r, n));
            _nameFlags.Add(r.U32());
        }

        return names;
    }

    private string DecodeName(UReader r, int length)
    {
        int start = r.Pos;
        byte[] bytes = r.Span(length).ToArray();
        if (Obfuscated)
        {
            for (int j = 0; j < bytes.Length; j++)
            {
                bytes[j] ^= (byte)(start + j);
            }
        }

        return Encoding.Latin1.GetString(bytes).TrimEnd('\0');
    }

    private List<UImport> ReadImports(UReader r, int count)
    {
        var imports = new List<UImport>(count);
        for (int i = 0; i < count; i++)
        {
            imports.Add(ReadImport(r));
        }

        return imports;
    }

    private static UImport ReadImport(UReader r) => new(r.Name(), r.Name(), r.I32(), r.Name());

    private List<UExport> ReadExports(UReader r, int count, int importCount)
    {
        var exports = new List<UExport>(count);
        for (int i = 0; i < count; i++)
        {
            UExport e = ReadExport(r, i);
            if (e.ClassRef < -importCount || e.ClassRef > count || e.Size < 0 || (e.Size > 0 && (e.Offset < 0 || (long)e.Offset + e.Size > Data.Length)))
            {
                throw new InvalidDataException($"{Name}: export {i} ({e.Name}) has a bad class reference or data range");
            }

            exports.Add(e);
        }

        return exports;
    }

    private static UExport ReadExport(UReader r, int index)
    {
        int cls = r.Compact();
        int super = r.Compact();
        int outer = r.I32();
        string name = r.Name();
        uint flags = r.U32();
        int size = r.Compact();
        int offset = size > 0 ? r.Compact() : 0;
        return new UExport(index, cls, super, outer, name, flags, size, offset);
    }

    // ---- autosave recovery (SDC-FORMAT.md section 2) ----------------------------------------------------------

    /// <summary>
    /// Autosaves write the summary before the tables and never patch it. The names start at 64; the import table is
    /// found by scanning for its <c>Core.Package</c> (or <c>Core.Class</c>) record, outer 0, and taking the earliest
    /// split after which the export records end exactly at the end of the file with every export's data before the
    /// import table.
    /// </summary>
    private void RecoverTables()
    {
        byte[] d = Data;
        Obfuscated = !IsPlainName(d, 64);
        var names = new List<string>();
        var r = new UReader(d, 64, d.Length, this);
        while (r.Remaining > 0)
        {
            int at = r.Pos;
            int n;
            try
            {
                n = r.Compact();
            }
            catch (InvalidDataException)
            {
                break;
            }

            if (n is < 2 or > 64 || n + 4 > r.Remaining)
            {
                r.Pos = at;
                break;
            }

            string name = DecodeName(r, n);
            if (name.Length != n - 1 || name.Any(c => c is < ' ' or > '~'))
            {
                r.Pos = at;
                break;
            }

            names.Add(name);
            _nameFlags.Add(r.U32());
        }

        Names = names;
        int namesEnd = r.Pos;
        int core = FindName("Core");
        if (core < 0)
        {
            throw new InvalidDataException($"{Name}: the summary has no table counts and no name table could be recovered");
        }

        byte[] pattern = EncodeCompact(core);
        for (int s = d.AsSpan(namesEnd).IndexOf(pattern) + namesEnd; s >= namesEnd; s = NextMatch(d, pattern, s))
        {
            if (!IsPackageImport(d, s))
            {
                continue;
            }

            var ir = new UReader(d, s, d.Length, this);
            var imports = new List<UImport>();
            while (ir.Remaining > 0)
            {
                int at = ir.Pos;
                try
                {
                    UImport imp = ReadImport(ir);
                    // An import's outer is another import (or none); it may come later in the table.
                    if (imp.Outer > 0)
                    {
                        ir.Pos = at;
                        break;
                    }

                    imports.Add(imp);
                }
                catch (InvalidDataException)
                {
                    ir.Pos = at;
                    break;
                }

                if (TryExportsToEnd(ir.Pos, s, imports.Count) is { } exports)
                {
                    Imports = imports;
                    Exports = exports;
                    Recovered = true;
                    return;
                }
            }
        }

        throw new InvalidDataException($"{Name}: the summary has no table counts (an unpatched autosave) and the tables could not be recovered");
    }

    private static int NextMatch(byte[] d, byte[] pattern, int after)
    {
        int i = d.AsSpan(after + 1).IndexOf(pattern);
        return i < 0 ? -1 : after + 1 + i;
    }

    private bool IsPackageImport(byte[] d, int at)
    {
        try
        {
            var r = new UReader(d, at, d.Length, this);
            if (r.Name() != "Core")
            {
                return false;
            }

            string cls = r.Name();
            if ((cls != "Package" && cls != "Class") || r.I32() != 0)
            {
                return false;
            }

            r.Name();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private List<UExport>? TryExportsToEnd(int start, int importTable, int importCount)
    {
        var r = new UReader(Data, start, Data.Length, this);
        var exports = new List<UExport>();
        try
        {
            while (r.Remaining > 0)
            {
                UExport e = ReadExport(r, exports.Count);
                if (e.ClassRef < -importCount || e.Size < 0 || e.Offset < 0 || (long)e.Offset + e.Size > importTable)
                {
                    return null;
                }

                exports.Add(e);
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return exports.Count > 0 && exports.All(e => e.ClassRef <= exports.Count) ? exports : null;
    }

    private static byte[] EncodeCompact(int v)
    {
        var bytes = new List<byte>();
        int a = Math.Abs(v);
        byte b = (byte)((v < 0 ? 0x80 : 0) | (a & 0x3F));
        a >>= 6;
        if (a != 0)
        {
            b |= 0x40;
        }

        bytes.Add(b);
        while (a != 0)
        {
            b = (byte)(a & 0x7F);
            a >>= 7;
            if (a != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }

        return [.. bytes];
    }
}
