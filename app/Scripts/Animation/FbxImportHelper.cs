using Godot;

namespace OpenScctEditor.Animation;

public static class FbxImportHelper
{
    public static string Convert(string path, string? outputPath = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenSCCT-Animation", "fbx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string output = outputPath ?? Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".glb");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var importer = new FbxDocument(); var state = new FbxState { CreateAnimations = true };
        Error error = importer.AppendFromFile(Path.GetFullPath(path), state);
        if (error != Error.Ok) throw new InvalidDataException("FBX import failed: " + error);
        if (importer.GenerateScene(state) is not Node3D scene) throw new InvalidDataException("FBX has no importable scene.");
        try
        {
            var exporter = new GltfDocument(); var gltf = new GltfState { CreateAnimations = true };
            error = exporter.AppendFromScene(scene, gltf);
            if (error == Error.Ok) error = exporter.WriteToFilesystem(gltf, output);
            if (error != Error.Ok) throw new InvalidDataException("FBX conversion failed: " + error);
        }
        finally { scene.Free(); }
        return output;
    }
    public static bool AnswerProbe(SceneTree tree)
    {
        string? input = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--animation-fbx=", StringComparison.Ordinal));
        if (input is null) return false;
        try
        {
            string output = OS.GetCmdlineUserArgs().First(a => a.StartsWith("--animation-output=", StringComparison.Ordinal))[19..];
            _ = Convert(input[16..], output); tree.Quit(0);
        }
        catch (Exception ex) { GD.PrintErr(ex.Message); tree.Quit(1); }
        return true;
    }
}
