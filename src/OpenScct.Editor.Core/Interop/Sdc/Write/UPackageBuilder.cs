using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>One export of a package being written: its table record and how its data is made.</summary>
public sealed class UExportSlot
{
    public UExportSlot(string name, int classRef, uint flags)
    {
        Name = name;
        ClassRef = classRef;
        Flags = flags;
    }

    public string Name { get; set; }

    /// <summary>objref of the class (0 = Core.Class).</summary>
    public int ClassRef { get; set; }

    public int SuperRef { get; set; }

    /// <summary>objref of the outer (0 = the package).</summary>
    public int Outer { get; set; }

    public uint Flags { get; set; }

    /// <summary>Writes the export's data at the writer's position (its offset in the package); null with
    /// <see cref="Copied"/> set, or for an export with no data.</summary>
    public Action<UWriter>? Write { get; set; }

    /// <summary>Data copied unchanged from another package, with the offset it had there: it is placed at an offset
    /// with the same low byte, so its offset-obfuscated strings still read.</summary>
    public (byte[] Data, int Offset)? Copied { get; set; }
}

/// <summary>
/// Builds an SCCT package (SDC-FORMAT.md sections 1-3): the name, import and export tables and the export data, laid
/// out as the stock editor lays them out (summary, names, export data in export order, imports, exports), and the
/// zlib container a normal save writes (one chunk).
/// </summary>
public sealed class UPackageBuilder
{
    public const int Version = 300;

    /// <summary>The name flags the stock editor writes on most names.</summary>
    public const uint DefaultNameFlags = 0x20070010;

    private readonly List<(string Name, uint Flags, int Hash)> _names = new();
    private readonly Dictionary<string, int> _nameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _exactNameIndex = new(StringComparer.Ordinal);
    private readonly List<UImport> _imports = new();
    private readonly Dictionary<(string, string, int, string), int> _importIndex = new();

    public UPackageBuilder(string name) => PackageName = name;

    /// <summary>The package name (the file name without extension): the root of the export paths.</summary>
    public string PackageName { get; }

    /// <summary>Names and strings XOR-obfuscated by offset, as every .sdc of the corpus is.</summary>
    public bool Obfuscated { get; init; } = true;

    public uint PackageFlags { get; set; } = 1;

    public List<UExportSlot> Exports { get; } = new();

    public IReadOnlyList<UImport> Imports => _imports;

    public int NameCount => _names.Count;

    /// <summary>The index of a name, added when new. Names ignore case, as UE2 names do, but a copied table can hold
    /// one name in two spellings: the exact spelling is used when the table has it, so a value keeps its entry.</summary>
    public int Name(string name)
    {
        if (_exactNameIndex.TryGetValue(name, out int i) || _nameIndex.TryGetValue(name, out i))
        {
            return i;
        }

        return AddName(name, DefaultNameFlags, 0);
    }

    /// <summary>Adds a name in table order (a copied package's names keep their indices).</summary>
    public int AddName(string name, uint flags, int hash)
    {
        if (name.Length is 0 or > 1023)
        {
            throw new ArgumentException($"'{name}' is not a usable name");
        }

        _names.Add((name, flags, hash));
        int i = _names.Count - 1;
        _nameIndex.TryAdd(name, i);
        _exactNameIndex.TryAdd(name, i);
        return i;
    }

    public int NameHash(int index) => _names[index].Hash;

    /// <summary>Sets the second compact written after every reference to a name (the loader reads and drops it).</summary>
    public void SetNameHash(int index, int hash) => _names[index] = _names[index] with { Hash = hash };

    /// <summary>Adds an import record as it is (a copied package's imports keep their indices); its objref.</summary>
    public int AddImport(UImport imp)
    {
        Name(imp.ClassPackage);
        Name(imp.ClassName);
        Name(imp.Name);
        _imports.Add(imp);
        _importIndex.TryAdd(Key(imp), _imports.Count - 1);
        return -_imports.Count;
    }

    /// <summary>The objref of an import, added when new.</summary>
    public int Import(string classPackage, string className, int outer, string name) =>
        _importIndex.TryGetValue(Key(new UImport(classPackage, className, outer, name)), out int i)
            ? -(i + 1)
            : AddImport(new UImport(classPackage, className, outer, name));

    /// <summary>The import of a package (or group) object, Core.Package.</summary>
    public int ImportPackage(string name, int outer = 0) => Import("Core", "Package", outer, name);

    /// <summary>The import of a script class, <c>Package.Class</c>.</summary>
    public int ImportClass(string package, string className) => Import("Core", "Class", ImportPackage(package), className);

    private static (string, string, int, string) Key(UImport i) =>
        (i.ClassPackage.ToUpperInvariant(), i.ClassName.ToUpperInvariant(), i.Outer, i.Name.ToUpperInvariant());

    public UImport ImportAt(int objref) => _imports[-objref - 1];

    /// <summary>Zero bytes after the export table: a normal stock save ends with 64, an autosave with none. The loader
    /// never reads them.</summary>
    public int Trailer { get; set; } = 64;

    /// <summary>Where the export data starts at the earliest: some saves leave zero bytes after the name table, kept
    /// so an unchanged map writes back the same.</summary>
    public int DataStart { get; set; }

    /// <summary>The unpacked package. <paramref name="guid"/> null makes one from the content (the same content
    /// gets the same GUID; any change gets a new one, as every stock save does).</summary>
    public byte[] Build(Guid? guid = null)
    {
        // Offsets key the obfuscation, so the data is written in place after the name table; the names it uses must
        // all be in that table. A first pass into a scratch writer collects them (writers are pure functions of the
        // project, so the second pass writes the same names).
        var offsets = new int[Exports.Count];
        var sizes = new int[Exports.Count];
        WriteData(new UWriter(this), offsets, sizes);
        int nameCount = _names.Count;
        var w = new UWriter(this);
        w.Bytes(new byte[64]);
        int nameOffset = w.Pos;
        WriteNames(w);
        if (w.Pos < DataStart)
        {
            w.Bytes(new byte[DataStart - w.Pos]);
        }

        WriteData(w, offsets, sizes);
        foreach (UImport imp in _imports)
        {
            Name(imp.ClassPackage);
            Name(imp.ClassName);
            Name(imp.Name);
        }

        if (_names.Count != nameCount)
        {
            throw new InvalidOperationException("the export data used names its first pass did not");
        }

        int importOffset = w.Pos;
        foreach (UImport imp in _imports)
        {
            w.Name(imp.ClassPackage);
            w.Name(imp.ClassName);
            w.I32(imp.Outer);
            w.Name(imp.Name);
        }

        int exportOffset = w.Pos;
        for (int i = 0; i < Exports.Count; i++)
        {
            UExportSlot e = Exports[i];
            w.Compact(e.ClassRef);
            w.Compact(e.SuperRef);
            w.I32(e.Outer);
            w.Name(e.Name);
            w.U32(e.Flags);
            w.Compact(sizes[i]);
            if (sizes[i] > 0)
            {
                w.Compact(offsets[i]);
            }
        }

        w.Bytes(new byte[Trailer]);
        byte[] data = w.ToArray();
        Span<byte> h = data;
        BinaryPrimitives.WriteUInt32LittleEndian(h, SdcContainer.PackageTag);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], PackageFlags);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], _names.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], nameOffset);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], Exports.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], exportOffset);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], _imports.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], importOffset);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[56..], Exports.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], _names.Count);
        byte[] id = (guid ?? new Guid(SHA256.HashData(data.AsSpan(64)).AsSpan(0, 16))).ToByteArray();
        id.CopyTo(h[36..]);
        return data;
    }

    private void WriteData(UWriter w, int[] offsets, int[] sizes)
    {
        for (int i = 0; i < Exports.Count; i++)
        {
            UExportSlot e = Exports[i];
            Name(e.Name);
            if (e.Copied is { } copied)
            {
                w.AlignTo256(copied.Offset & 0xFF);
                offsets[i] = w.Pos;
                w.Bytes(copied.Data);
            }
            else
            {
                offsets[i] = w.Pos;
                e.Write?.Invoke(w);
            }

            sizes[i] = w.Pos - offsets[i];
        }
    }

    private void WriteNames(UWriter w)
    {
        foreach ((string name, uint flags, int _) in _names)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(name + "\0");
            w.Compact(bytes.Length);
            int start = w.Pos;
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Obfuscated ? (byte)(bytes[i] ^ (byte)(start + i)) : bytes[i];
            }

            w.Bytes(bytes);
            w.U32(flags);
        }
    }

    /// <summary>The .sdc file: the package as one zlib chunk (<c>u32 unpacked, u32 packed, zlib</c>), as a normal
    /// stock save writes it.</summary>
    public static byte[] Pack(byte[] package)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(package);
        }

        byte[] file = new byte[8 + packed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, (uint)package.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)packed.Length);
        packed.GetBuffer().AsSpan(0, (int)packed.Length).CopyTo(file.AsSpan(8));
        return file;
    }
}
