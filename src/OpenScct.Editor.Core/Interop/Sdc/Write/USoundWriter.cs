using System.Buffers.Binary;
using System.Text;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Ordinary embedded PCM sound data (USound::Serialize, SCCT editor 0x11117A70).
/// FileType FName, source FString, sound flags, radius, TLazyArray audio. Composite/stream-bank sounds are not synthesised.</summary>
internal static class USoundWriter
{
    public static Action<UWriter> Prepare(string file, string name)
    {
        byte[] wave = File.ReadAllBytes(file);
        if (wave.Length < 12 || Encoding.ASCII.GetString(wave, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wave, 8, 4) != "WAVE"
            || BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4)) != wave.Length - 8)
            throw new InvalidDataException("The original engine needs a valid PCM WAV sound. Convert this sound to WAV before exporting.");
        bool format = false, data = false;
        for (int offset = 12; offset + 8 <= wave.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4));
            if (size > wave.Length - offset - 8) throw new InvalidDataException("The WAV contains a truncated audio chunk.");
            string tag = Encoding.ASCII.GetString(wave, offset, 4);
            if (tag == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("The WAV format chunk is incomplete.");
                ReadOnlySpan<byte> f = wave.AsSpan(offset + 8, (int)size);
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]), bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(f) != 1 || channels is < 1 or > 2 || bits is not (8 or 16))
                    throw new InvalidDataException("Use an 8-bit or 16-bit PCM WAV with one or two channels for the original game.");
                format = true;
            }
            if (tag == "data" && size > 0) data = true;
            offset = checked(offset + 8 + (int)size + (int)(size & 1));
        }
        if (!format || !data) throw new InvalidDataException("The WAV has no supported format or audio data.");
        return w =>
        {
            w.Name("None"); w.Name("wav"); w.FString(name);
            w.U32(0); w.F32(0);
            int end = w.Pos; w.I32(0); w.Compact(wave.Length); w.Bytes(wave); w.PatchI32(end, w.Pos);
        };
    }
}
