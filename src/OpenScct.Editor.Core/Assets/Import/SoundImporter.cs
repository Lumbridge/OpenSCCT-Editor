using System.Text.Json.Nodes;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>Copies WAV/OGG sounds into the map, using a unique name so imports never replace existing files.</summary>
public static class SoundImporter
{
    public static string Import(string projectFolder, string sourceFile)
    {
        string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (extension is not (".wav" or ".ogg")) throw new ArgumentException("Choose a WAV or Ogg Vorbis sound.");
        using (var file = File.OpenRead(sourceFile))
        {
            Span<byte> header = stackalloc byte[12];
            int read = file.Read(header);
            if (extension == ".wav" ? read < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)
                : read < 4 || !header[..4].SequenceEqual("OggS"u8))
                throw new InvalidDataException("The file is not a valid " + extension + " sound.");
        }
        ProjectAssets assets = ProjectAssets.For(projectFolder);
        JsonObject manifest = assets.Edit();
        JsonObject sounds = (manifest["sounds"] ??= new JsonObject()).AsObject();
        string stem = AssetPaths.Safe(Path.GetFileNameWithoutExtension(sourceFile));
        string name = stem;
        string path;
        string relative;
        int suffix = 2;
        do
        {
            path = $"MyLevel.Sounds.{name}";
            relative = $"sounds/{name}{extension}";
            if (!sounds.Any(e => e.Key.Equals(path, StringComparison.OrdinalIgnoreCase)) && !File.Exists(assets.FilePath(relative))) break;
            name = stem + "_" + suffix++;
        } while (true);
        string target = assets.FilePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceFile, target, overwrite: false);
        sounds[path] = new JsonObject { ["file"] = relative };
        ProjectAssets.Save(projectFolder, manifest);
        return path;
    }
}
