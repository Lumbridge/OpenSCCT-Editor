using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Resource closure for the source console; carried vial geometry is in world.glb's runtime library.</summary>
internal static class Ps2ObjectiveBuild
{
    public static void Write(CompileContext context)
    {
        if (!Ps2ObjectiveAssets.Used(context.Project)) return;
        ProjectAssets? assets = context.Library.Project;
        if (assets?.Manifest["ps2Objectives"] is not JsonObject) assets = Ps2ObjectiveAssets.Bundled;
        if (assets?.Manifest["ps2Objectives"]?["resources"] is not JsonArray resources)
        {
            context.Error("ps2.assets", "This project's PS2 objective assets are missing. Save it with the complete Editor bundle before building.");
            return;
        }
        foreach (JsonNode? required in assets.Manifest["ps2Objectives"]!["requiredFiles"]!.AsArray())
            if (!File.Exists(assets.FilePath(required!.GetValue<string>())))
                context.Error("ps2.assets", "A required PS2 objective asset is missing: " + required.GetValue<string>());
        foreach (JsonNode? resource in resources)
        {
            string source = resource!["file"]!.GetValue<string>();
            string output = resource["output"]!.GetValue<string>();
            if (!source.StartsWith("ps2-objectives/", StringComparison.Ordinal) || source.Contains("..", StringComparison.Ordinal) ||
                !output.StartsWith("render/chars/nd133_1mesh/", StringComparison.Ordinal) || output.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid PS2 objective resource path: " + source);
            string path = assets.FilePath(source);
            if (!File.Exists(path))
            {
                context.Error("ps2.assets", "The PS2 objective resource is missing from the project: " + source);
                continue;
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (context.Files.TryGetValue(output, out byte[]? previous) && !previous.AsSpan().SequenceEqual(bytes))
                context.Error("ps2.assets-conflict", "A different skeletal asset already occupies " + output);
            else context.Files[output] = bytes;
        }
        context.Count("ps2.resources", resources.Count);
    }
}
