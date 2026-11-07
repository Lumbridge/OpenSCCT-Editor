using Godot;

namespace OpenScctEditor.Viewports;

/// <summary>Keeps the native mesh arguments owned until Godot has finished reading them.</summary>
public static class MeshUpload
{
    public static void AddSurface(ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays)
    {
        // Godot 4.7.2 creates temporary collections for omitted arguments, then passes their raw handles to
        // native code without keeping the owners alive. A GC during the upload can free those collections.
        var blendShapes = new Godot.Collections.Array<Godot.Collections.Array>();
        using var lods = new Godot.Collections.Dictionary();
        mesh.AddSurfaceFromArrays(primitive, arrays, blendShapes, lods);
        GC.KeepAlive(blendShapes);
        GC.KeepAlive(arrays);
        GC.KeepAlive(mesh);
    }
}
