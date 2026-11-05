using System.Globalization;
using System.Text;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Reads UE2 map export text. Accepts what the stock and RE+ editors write (including the SCCT name-index notes
/// <c>§(n)</c>, which it strips) and what other tools write (no indentation, Package.Class names). Line based and
/// lenient: unknown lines are recorded as problems, never fatal.
/// </summary>
public static class T3dReader
{
    public static T3dDocument ReadFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Read(Decode(bytes));
    }

    /// <summary>T3D files are ANSI (Latin-1 here) unless they carry a UTF-16 or UTF-8 byte order mark.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    public static T3dDocument Read(string text)
    {
        var doc = new T3dDocument();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var r = new LineCursor(lines);
        while (r.Next() is { } line)
        {
            string t = line.Trim();
            if (t.Length == 0 || IsKeyword(t, "Begin Map") || IsKeyword(t, "End Map"))
            {
                continue;
            }

            if (IsKeyword(t, "Begin Actor"))
            {
                doc.Actors.Add(ReadActor(r, t, doc));
            }
            else if (IsKeyword(t, "Begin GE"))
            {
                while (r.Next() is { } g && !IsKeyword(g.Trim(), "End GE"))
                {
                    if (g.Trim().Length > 0)
                    {
                        doc.GeLines.Add(g.Trim());
                    }
                }
            }
            else if (IsKeyword(t, "Begin "))
            {
                List<string> block = ReadRawBlock(r, line);
                // An empty Begin Surface / End Surface pair carries nothing.
                if (block.Count > 2)
                {
                    doc.OtherBlocks.Add(block);
                }
            }
            else
            {
                doc.Problems.Add($"line {r.LineNumber}: unexpected '{Short(t)}'");
            }
        }

        return doc;
    }

    private static T3dActor ReadActor(LineCursor r, string header, T3dDocument doc)
    {
        Dictionary<string, string> h = HeaderValues(header);
        var actor = new T3dActor(h.GetValueOrDefault("Class") ?? "Actor", h.GetValueOrDefault("Name") ?? "") { Line = r.LineNumber };
        while (r.Next() is { } line)
        {
            string t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            if (IsKeyword(t, "End Actor"))
            {
                break;
            }

            if (IsKeyword(t, "Begin Brush"))
            {
                actor.Brush = ReadBrush(r, t, doc);
            }
            else if (IsKeyword(t, "Begin "))
            {
                actor.Objects.Add(ReadRawBlock(r, line));
            }
            else if (T3dValueParser.ParseField(T3dValueParser.StripNameNotes(t)) is { } field)
            {
                actor.Props.Add(field);
            }
            else
            {
                doc.Problems.Add($"line {r.LineNumber}: actor {actor.Name}: unreadable '{Short(t)}'");
            }
        }

        if (actor.Name.Length == 0 && actor.Find("Name") is { Value: T3dText name })
        {
            actor.Name = name.Value;
        }

        return actor;
    }

    private static T3dBrush ReadBrush(LineCursor r, string header, T3dDocument doc)
    {
        var brush = new T3dBrush(HeaderValues(header).GetValueOrDefault("Name") ?? "");
        while (r.Next() is { } line)
        {
            string t = line.Trim();
            if (IsKeyword(t, "End Brush"))
            {
                break;
            }

            if (IsKeyword(t, "Begin Polygon"))
            {
                brush.Polygons.Add(ReadPolygon(r, t, doc));
            }
            else if (t.Length > 0 && !IsKeyword(t, "Begin PolyList") && !IsKeyword(t, "End PolyList"))
            {
                doc.Problems.Add($"line {r.LineNumber}: brush {brush.Name}: unexpected '{Short(t)}'");
            }
        }

        return brush;
    }

    private static T3dPolygon ReadPolygon(LineCursor r, string header, T3dDocument doc)
    {
        Dictionary<string, string> h = HeaderValues(header);
        var poly = new T3dPolygon
        {
            Item = h.GetValueOrDefault("Item"),
            Texture = h.TryGetValue("Texture", out string? tex) ? MaterialPath(tex) : null,
            Flags = h.TryGetValue("Flags", out string? flags) && T3dFormat.TryParseInt(flags, out long f) ? unchecked((int)f) : 0,
            Link = h.TryGetValue("Link", out string? link) && T3dFormat.TryParseInt(link, out long l) ? (int)l : null,
            LightMapScale = h.TryGetValue("LightMapScale", out string? lms) && T3dFormat.TryParseFloat(lms, out float s) ? s : null,
            LightMapScalePsx2 = h.TryGetValue("LightMapScalePsx2", out string? ps2) && T3dFormat.TryParseFloat(ps2, out float s2) ? s2 : null,
        };
        while (r.Next() is { } line)
        {
            string t = line.Trim();
            if (IsKeyword(t, "End Polygon"))
            {
                break;
            }

            int space = t.IndexOfAny([' ', '\t']);
            string key = space < 0 ? t : t[..space];
            string rest = space < 0 ? "" : t[space..].Trim();
            switch (key.ToUpperInvariant())
            {
                case "ORIGIN":
                    poly.Origin = ParseVector(rest);
                    break;
                case "NORMAL":
                    poly.Normal = ParseVector(rest);
                    break;
                case "TEXTUREU":
                    poly.TextureU = ParseVector(rest);
                    break;
                case "TEXTUREV":
                    poly.TextureV = ParseVector(rest);
                    break;
                case "VERTEX":
                    poly.Vertices.Add(ParseVector(rest));
                    break;
                case "PAN":
                    Dictionary<string, string> pan = HeaderValues("Pan " + rest);
                    poly.PanU = pan.TryGetValue("U", out string? u) && int.TryParse(u, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int pu) ? pu : 0;
                    poly.PanV = pan.TryGetValue("V", out string? v) && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int pv) ? pv : 0;
                    break;
                default:
                    if (t.Length > 0)
                    {
                        doc.Problems.Add($"line {r.LineNumber}: polygon: unexpected '{Short(t)}'");
                    }

                    break;
            }
        }

        return poly;
    }

    /// <summary>Reads a nested <c>Begin X ... End X</c> block as raw lines (the first line is already read).</summary>
    private static List<string> ReadRawBlock(LineCursor r, string first)
    {
        var block = new List<string> { T3dValueParser.StripNameNotes(first.Trim()) };
        int depth = 1;
        while (depth > 0 && r.Next() is { } line)
        {
            string t = T3dValueParser.StripNameNotes(line.Trim());
            if (IsKeyword(t, "Begin "))
            {
                depth++;
            }
            else if (IsKeyword(t, "End "))
            {
                depth--;
            }

            if (t.Length > 0)
            {
                block.Add(t);
            }
        }

        return block;
    }

    /// <summary>The <c>Key=Value</c> pairs of a Begin line (values end at whitespace).</summary>
    /// <summary>The <c>Key=Value</c> pairs of a header line. A value in double quotes may hold spaces (the engine's
    /// Parse reads it to the closing quote), so a material name with spaces is read whole.</summary>
    public static Dictionary<string, string> HeaderValues(string line)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string s = T3dValueParser.StripNameNotes(line);
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=')
            {
                i++;
            }

            if (i >= s.Length || s[i] != '=' || i == start)
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                continue;
            }

            string key = s[start..i++];
            int end;
            string value;
            if (i < s.Length && s[i] == '"' && (end = s.IndexOf('"', i + 1)) > 0)
            {
                value = s[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                end = i;
                while (end < s.Length && !char.IsWhiteSpace(s[end]))
                {
                    end++;
                }

                value = s[i..end];
                i = end;
            }

            values[key] = value;
        }

        return values;
    }

    /// <summary>A material in a polygon header: <c>Pkg.Grp.Name</c> or <c>Class'Pkg.Grp.Name'</c>; None is no material.</summary>
    private static string? MaterialPath(string text)
    {
        int q = text.IndexOf('\'', StringComparison.Ordinal);
        string path = q >= 0 ? text[(q + 1)..].TrimEnd('\'') : text;
        return path.Length == 0 || path.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    /// <summary>A polygon vector <c>x,y,z</c>. A component that does not read is 0, as the engine's appAtof reads it,
    /// so one bad component (such as the "-+00000.000000" earlier editor builds wrote) loses only itself.</summary>
    public static Vec3 ParseVector(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return Vec3.Zero;
        }

        static float Component(string s) => T3dFormat.TryParseFloat(s, out float v) ? v : 0f;
        return new Vec3(Component(parts[0]), Component(parts[1]), Component(parts[2]));
    }

    private static bool IsKeyword(string trimmed, string keyword) =>
        trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
        && (trimmed.Length == keyword.Length || keyword.EndsWith(' ') || char.IsWhiteSpace(trimmed[keyword.Length]));

    private static string Short(string s) => s.Length > 80 ? s[..80] + "..." : s;

    private sealed class LineCursor(string[] lines)
    {
        private int _next;

        public int LineNumber => _next;

        public string? Next() => _next < lines.Length ? lines[_next++] : null;
    }
}
