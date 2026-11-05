using System.Globalization;
using System.Text;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Writes UE2 map export text in the layout the stock editor exports (4-space property indent, polygon vectors as
/// <c>%+013.6f</c>, CRLF), which its File &gt; Import and <c>MAP IMPORT</c> read back.
/// </summary>
public static class T3dWriter
{
    public static string Write(T3dDocument doc)
    {
        var sb = new StringBuilder();
        Line(sb, 0, "Begin Map");
        foreach (T3dActor a in doc.Actors)
        {
            WriteActor(sb, a);
        }

        foreach (List<string> block in doc.OtherBlocks)
        {
            WriteRaw(sb, 0, block);
        }

        Line(sb, 0, "Begin Surface");
        Line(sb, 0, "End Surface");
        if (doc.GeLines.Count > 0)
        {
            Line(sb, 0, "Begin GE");
            foreach (string ge in doc.GeLines)
            {
                Line(sb, 4, ge);
            }

            Line(sb, 0, "End GE");
        }

        Line(sb, 0, "End Map");
        return sb.ToString();
    }

    /// <summary>Writes the document as a Latin-1 file (the stock editor reads T3D as ANSI).</summary>
    public static void WriteFile(T3dDocument doc, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(Write(doc)));
    }

    private static void WriteActor(StringBuilder sb, T3dActor a)
    {
        Line(sb, 0, $"Begin Actor Class={a.Class} Name={a.Name}");
        foreach (List<string> obj in a.Objects)
        {
            WriteRaw(sb, 4, obj);
        }

        foreach (T3dField f in a.Props)
        {
            Line(sb, 4, f.ToString());
        }

        if (a.Brush is { } brush)
        {
            Line(sb, 4, $"Begin Brush Name={brush.Name}");
            Line(sb, 7, "Begin PolyList");
            foreach (T3dPolygon p in brush.Polygons)
            {
                WritePolygon(sb, p);
            }

            Line(sb, 7, "End PolyList");
            Line(sb, 4, "End Brush");
            Line(sb, 4, $"Brush=Model'MyLevel.{brush.Name}'");
        }

        Line(sb, 0, "End Actor");
    }

    private static void WritePolygon(StringBuilder sb, T3dPolygon p)
    {
        var header = new StringBuilder("Begin Polygon");
        if (!string.IsNullOrEmpty(p.Item))
        {
            header.Append(" Item=").Append(HeaderValue(p.Item));
        }

        if (!string.IsNullOrEmpty(p.Texture))
        {
            header.Append(" Texture=").Append(HeaderValue(p.Texture));
        }

        if (p.Flags != 0)
        {
            header.Append(" Flags=").Append(p.Flags.ToString(CultureInfo.InvariantCulture));
        }

        if (p.Link is int link)
        {
            header.Append(" Link=").Append(link.ToString(CultureInfo.InvariantCulture));
        }

        if (p.LightMapScale is float lms)
        {
            header.Append(" LightMapScale=").Append(T3dFormat.Float(lms));
        }

        if (p.LightMapScalePsx2 is float ps2)
        {
            header.Append(" LightMapScalePsx2=").Append(T3dFormat.Float(ps2));
        }

        Line(sb, 10, header.ToString());
        Line(sb, 13, "Origin   " + T3dFormat.PolyVector(p.Origin.X, p.Origin.Y, p.Origin.Z));
        Line(sb, 13, "Normal   " + T3dFormat.PolyVector(p.Normal.X, p.Normal.Y, p.Normal.Z));
        Line(sb, 13, "TextureU " + T3dFormat.PolyVector(p.TextureU.X, p.TextureU.Y, p.TextureU.Z));
        Line(sb, 13, "TextureV " + T3dFormat.PolyVector(p.TextureV.X, p.TextureV.Y, p.TextureV.Z));
        if (p.PanU != 0 || p.PanV != 0)
        {
            Line(sb, 13, string.Create(CultureInfo.InvariantCulture, $"Pan      U={p.PanU} V={p.PanV}"));
        }

        foreach (Model.Vec3 v in p.Vertices)
        {
            Line(sb, 13, "Vertex   " + T3dFormat.PolyVector(v.X, v.Y, v.Z));
        }

        Line(sb, 10, "End Polygon");
    }

    /// <summary>A header value; one with spaces goes in double quotes, which the engine's Parse reads whole.</summary>
    private static string HeaderValue(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    /// <summary>Writes a raw block back, indenting nested lines by depth.</summary>
    private static void WriteRaw(StringBuilder sb, int indent, List<string> block)
    {
        int depth = 0;
        foreach (string raw in block)
        {
            if (raw.StartsWith("End ", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
            }

            Line(sb, indent + Math.Max(0, depth) * 4, raw);
            if (raw.StartsWith("Begin ", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
        }
    }

    private static void Line(StringBuilder sb, int indent, string text) => sb.Append(' ', indent).Append(text).Append("\r\n");
}
