using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// A UE2 map in export text (<c>Begin Map ... End Map</c>), as the stock editor's <c>MAP EXPORT</c> writes it and its
/// File &gt; Import reads it: actors with their property lines, brush models with polygons, and the SCCT GE list.
/// </summary>
public sealed class T3dDocument
{
    /// <summary>Actors in file order (the level's actor order).</summary>
    public List<T3dActor> Actors { get; } = new();

    /// <summary>The <c>GE ADD ...</c> lines of the <c>Begin GE</c> block, without indentation.</summary>
    public List<string> GeLines { get; } = new();

    /// <summary>Map-level blocks the reader does not model (<c>Begin Surface</c> with content, ...), as raw lines with
    /// their Begin and End lines, so they can be written back.</summary>
    public List<List<string>> OtherBlocks { get; } = new();

    /// <summary>Problems met while reading (line number and text), for import findings.</summary>
    public List<string> Problems { get; } = new();
}

/// <summary>One <c>Begin Actor Class=... Name=...</c> block.</summary>
public sealed class T3dActor
{
    public T3dActor(string className, string name)
    {
        Class = className;
        Name = name;
    }

    /// <summary>The class as written (<c>Brush</c>, <c>Engine.Light</c>...).</summary>
    public string Class { get; set; }

    public string Name { get; set; }

    /// <summary>The property lines in file order (a static or dynamic array has one line per element).</summary>
    public List<T3dField> Props { get; } = new();

    /// <summary>The brush model (<c>Begin Brush</c>) of brushes, volumes and brush movers.</summary>
    public T3dBrush? Brush { get; set; }

    /// <summary>Sub-objects (<c>Begin Object</c>: emitters' particle systems...) as raw lines, Begin and End included.</summary>
    public List<List<string>> Objects { get; } = new();

    /// <summary>The 1-based line of the Begin Actor line.</summary>
    public int Line { get; set; }

    public T3dField? Find(string name) => Props.Find(p => p.Index is null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A <c>Begin Brush Name=...</c> model: its polygons in the brush's local space.</summary>
public sealed class T3dBrush
{
    public T3dBrush(string name) => Name = name;

    public string Name { get; set; }

    public List<T3dPolygon> Polygons { get; } = new();
}

/// <summary>A <c>Begin Polygon</c> (UE2 FPoly).</summary>
public sealed class T3dPolygon
{
    public string? Item { get; set; }

    /// <summary>The material path (<c>Package.Group.Name</c>), or null.</summary>
    public string? Texture { get; set; }

    public int Flags { get; set; }

    /// <summary>The poly link, or null when the line has none.</summary>
    public int? Link { get; set; }

    /// <summary>Lightmap scale when written (<c>LightMapScale=</c>), else null.</summary>
    public float? LightMapScale { get; set; }

    /// <summary>The original's separate PS2 lightmap scale when written (<c>LightMapScalePsx2=</c>, the editor's own
    /// key, which the stock polygon reader skips), else null.</summary>
    public float? LightMapScalePsx2 { get; set; }

    public Vec3 Origin { get; set; }

    public Vec3 Normal { get; set; }

    public Vec3 TextureU { get; set; }

    public Vec3 TextureV { get; set; }

    public int PanU { get; set; }

    public int PanV { get; set; }

    public List<Vec3> Vertices { get; } = new();
}
