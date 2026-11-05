namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// A source export kept as stored but free to change slots: its tagged properties (and the soft-body tail) were read
/// with a <see cref="UPropertyTrace"/>, so every object reference in it is known. Written again, each reference goes
/// through the new package's numbering, the tag sizes around a reference that changed width follow, and the strings
/// (obfuscated by offset) are keyed to where the object now sits. With nothing to change the bytes are the stored ones.
/// </summary>
internal sealed class URelocatable
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly bool _obfuscated;
    private readonly List<(int At, int End)> _refs;
    private readonly List<(int At, int End)> _strings;
    private readonly List<UTagSpan> _tags;

    private URelocatable(byte[] data, int offset, bool obfuscated, UPropertyTrace trace)
    {
        _data = data;
        _offset = offset;
        _obfuscated = obfuscated;
        _refs = trace.Refs.Select(r => (r.At - offset, r.End - offset)).ToList();
        _strings = trace.Strings.Select(s => (s.At - offset, s.End - offset)).ToList();
        _tags = trace.Tags.Select(t => new UTagSpan(t.Info - offset, t.SizeAt - offset, t.SizeEnd - offset, t.ValueStart - offset, t.ValueEnd - offset))
            .OrderBy(t => t.ValueEnd - t.ValueStart).ToList();
    }

    /// <summary>The object's references (stored objrefs), state frame included.</summary>
    public IEnumerable<int> References => _refs.Select(r => new UReader(_data, r.At, r.End, null).Compact());

    /// <summary>
    /// Reads an export whose every reference can be found: an optional state frame, the tagged properties (no value
    /// kept raw) and nothing after them but a soft body's state (SDC-FORMAT.md section 16). Null for anything else.
    /// </summary>
    public static URelocatable? Read(UPackage pkg, UExport e, string? className, UeSchema? schema)
    {
        if (e.Size <= 0)
        {
            return null;
        }

        var trace = new UPropertyTrace();
        try
        {
            // Copied data keeps its own bytes, so its names are not noted for the objects written from the project.
            UReader r = pkg.Reader(e);
            r.NotesNames = false;
            if ((e.Flags & UExport.HasStack) != 0)
            {
                // FStateFrame: the node and state node, probe mask, latent action, and the code offset with a node.
                int node = TracedRef(r, trace);
                TracedRef(r, trace);
                r.U64();
                r.I32();
                if (node != 0)
                {
                    r.Compact();
                }
            }

            new UPropertyReader(schema, (_, _) => null) { Trace = trace }.ReadObject(r, className, e.Name);
            if (r.Pos != r.End && !SoftBodyTail(r, e, pkg, trace))
            {
                return null;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return trace.Opaque ? null : new URelocatable(pkg.Data.AsSpan(e.Offset, e.Size).ToArray(), e.Offset, pkg.Obfuscated, trace);
    }

    private const int SoftBodySettings = 161;

    private static int TracedRef(UReader r, UPropertyTrace trace)
    {
        int at = r.Pos;
        int objref = r.ObjRef();
        trace.Refs.Add((at, r.Pos));
        return objref;
    }

    /// <summary>UESoftBody::Serialize after the properties (ESBPatch, ESBStripDoor): bounding box and sphere, two ints,
    /// the owning actor, an int, the particles (72 bytes each), the links (12 bytes each; scctpkg level.py _sb_state
    /// reads this far), then a fixed 161-byte block of settings (no compact index, so no reference).</summary>
    private static bool SoftBodyTail(UReader r, UExport e, UPackage pkg, UPropertyTrace trace)
    {
        if (pkg.ClassName(e) is not ("ESBPatch" or "ESBStripDoor"))
        {
            return false;
        }

        r.Skip(25 + 16 + 8);
        TracedRef(r, trace);
        r.I32();
        r.Skip(72 * r.Count(72, "soft body particles"));
        r.Skip(12 * r.Count(12, "soft body links"));
        return r.Remaining == SoftBodySettings;
    }

    /// <summary>The object's bytes with every reference mapped, for writing at absolute offset <paramref name="at"/>.</summary>
    public byte[] Write(Func<int, int> map, int at)
    {
        // Each reference that changes is an edit; a tag whose value changed length gets a new size (and info byte when
        // the size's encoding changes), inner tags first so their headers count in the tags around them.
        var edits = new List<(int At, int End, byte[] Bytes)>();
        foreach ((int start, int end) in _refs)
        {
            int objref = new UReader(_data, start, end, null).Compact();
            int mapped = map(objref);
            if (mapped != objref)
            {
                edits.Add((start, end, CompactBytes(mapped)));
            }
        }

        if (edits.Count > 0)
        {
            foreach (UTagSpan t in _tags)
            {
                int delta = edits.Where(x => x.At >= t.ValueStart && x.End <= t.ValueEnd).Sum(x => x.Bytes.Length - (x.End - x.At));
                if (delta == 0)
                {
                    continue;
                }

                byte info = _data[t.Info];
                int size = OldSize(info, t.SizeAt);
                int code = SizeCode(size + delta);
                edits.Add((t.Info, t.Info + 1, [(byte)((info & 0x8F) | (code << 4))]));
                edits.Add((t.SizeAt, t.SizeEnd, SizeBytes(code, size + delta)));
            }
        }

        if (edits.Count == 0 && (!_obfuscated || _strings.Count == 0 || (byte)at == (byte)_offset))
        {
            return _data;
        }

        // An insertion (a size field where the tag had none) goes before an edit starting at the same byte.
        edits.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.End.CompareTo(b.End));
        var output = new List<byte>(_data.Length + 16);
        var shifts = new List<(int OldAt, int Delta)>();
        int pos = 0, total = 0;
        foreach ((int start, int end, byte[] bytes) in edits)
        {
            output.AddRange(_data.AsSpan(pos, start - pos).ToArray());
            output.AddRange(bytes);
            total += bytes.Length - (end - start);
            shifts.Add((end, total));
            pos = end;
        }

        output.AddRange(_data.AsSpan(pos).ToArray());
        byte[] result = output.ToArray();
        if (_obfuscated)
        {
            foreach ((int start, int end) in _strings)
            {
                int delta = shifts.LastOrDefault(s => s.OldAt <= start).Delta;
                int dataStart = start + CompactLength(_data, start);
                for (int i = dataStart; i < end; i++)
                {
                    result[i + delta] ^= (byte)((byte)(_offset + i) ^ (byte)(at + i + delta));
                }
            }
        }

        return result;
    }

    private int OldSize(byte info, int sizeAt) => ((info >> 4) & 7) switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        3 => 12,
        4 => 16,
        5 => _data[sizeAt],
        6 => BitConverter.ToUInt16(_data, sizeAt),
        _ => BitConverter.ToInt32(_data, sizeAt),
    };

    /// <summary>FPropertyTag's size code: the five fixed sizes, else a byte, word or int.</summary>
    private static int SizeCode(int size) => size switch
    {
        1 => 0,
        2 => 1,
        4 => 2,
        12 => 3,
        16 => 4,
        <= 0xFF => 5,
        <= 0xFFFF => 6,
        _ => 7,
    };

    private static byte[] SizeBytes(int code, int size) => code switch
    {
        <= 4 => [],
        5 => [(byte)size],
        6 => BitConverter.GetBytes((ushort)size),
        _ => BitConverter.GetBytes(size),
    };

    private static int CompactLength(byte[] data, int at)
    {
        var r = new UReader(data, at, data.Length, null);
        r.Compact();
        return r.Pos - at;
    }

    /// <summary>A compact index's bytes (as <see cref="UWriter.Compact"/> writes it).</summary>
    public static byte[] CompactBytes(int v)
    {
        var bytes = new List<byte>(5);
        long a = Math.Abs((long)v);
        byte b = (byte)((v < 0 ? 0x80 : 0) | (int)(a & 0x3F));
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

        return bytes.ToArray();
    }
}
