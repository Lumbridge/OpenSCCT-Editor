using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>PCM waveform and seek target; compressed clips still show a transport timeline.</summary>
public partial class SoundWaveform : Control
{
    private float[] _peaks = [];
    public double Fraction { get; set; }
    public event Action<double>? SeekRequested;

    public SoundWaveform()
    {
        CustomMinimumSize = new Vector2(0, 64);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Click or drag to seek through the preview.";
    }

    public void SetStream(AudioStream? stream)
    {
        _peaks = [];
        if (stream is AudioStreamWav wav && wav.Format is AudioStreamWav.FormatEnum.Format8Bits or AudioStreamWav.FormatEnum.Format16Bits)
        {
            byte[] data = wav.Data;
            int stride = wav.Format == AudioStreamWav.FormatEnum.Format16Bits ? 2 : 1;
            int samples = data.Length / stride;
            _peaks = new float[Math.Min(480, samples)];
            for (int bucket = 0; bucket < _peaks.Length; bucket++)
            {
                int first = (int)((long)bucket * samples / _peaks.Length);
                int last = (int)((long)(bucket + 1) * samples / _peaks.Length);
                for (int i = first; i < last; i++)
                {
                    float value = stride == 2 ? Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2))) / 32768f
                        : Math.Abs(data[i] - 128) / 128f;
                    _peaks[bucket] = Math.Max(_peaks[bucket], value);
                }
            }
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), EditorTheme.Panel);
        DrawLine(new(0, Size.Y / 2), new(Size.X, Size.Y / 2), EditorTheme.TextDim);
        for (int i = 0; i < _peaks.Length; i++)
        {
            float x = (i + 0.5f) / _peaks.Length * Size.X;
            float h = Math.Max(1, _peaks[i] * (Size.Y / 2 - 5));
            DrawLine(new(x, Size.Y / 2 - h), new(x, Size.Y / 2 + h), EditorTheme.Accent);
        }
        float cursor = (float)Math.Clamp(Fraction, 0, 1) * Size.X;
        DrawLine(new(cursor, 0), new(cursor, Size.Y), Colors.White, 2);
    }

    public override void _GuiInput(InputEvent input)
    {
        Vector2? at = input switch
        {
            InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click => click.Position,
            InputEventMouseMotion move when (move.ButtonMask & MouseButtonMask.Left) != 0 => move.Position,
            _ => null,
        };
        if (at is not { } point || Size.X <= 0) return;
        SeekRequested?.Invoke(Math.Clamp(point.X / Size.X, 0, 1));
        AcceptEvent();
    }
}
