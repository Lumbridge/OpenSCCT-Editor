using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Identity;

/// <summary>The project's picture files: <c>pictures/</c> holds the images file recipes name, and
/// <c>.cache/pictures/</c> the camera pictures <c>ose mapshot</c> rendered (keyed by recipe and bookmark, each stamped
/// with the map it was rendered from; safe to delete).</summary>
public static class ProjectPictures
{
    public const string Folder = "pictures";
    public const string CacheFolder = ".cache/pictures";

    /// <summary>The image types a picture file can be.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <summary>The full path of a recipe's file: absolute as given, else relative to the project folder (null when
    /// the project has no folder yet and the path is relative).</summary>
    public static string? Resolve(string? projectFolder, string file)
    {
        if (Path.IsPathFullyQualified(file))
        {
            return file;
        }

        return projectFolder is null ? null : Path.GetFullPath(Path.Combine(projectFolder, file));
    }

    /// <summary>Copies an image into the project's <c>pictures/</c> folder (a free name from its own) and returns the
    /// path the recipe stores (<c>pictures/menu.png</c>). A file already in the folder is used where it is.</summary>
    public static string Import(string projectFolder, string sourceFile)
    {
        string folder = Path.Combine(Path.GetFullPath(projectFolder), Folder);
        string source = Path.GetFullPath(sourceFile);
        string ext = Path.GetExtension(source).ToLowerInvariant();
        if (!Extensions.Contains(ext))
        {
            throw new ArgumentException($"{Path.GetFileName(source)} is not a {RgbaImage.Formats} file");
        }

        if (string.Equals(Path.GetDirectoryName(source), folder, StringComparison.OrdinalIgnoreCase))
        {
            return Folder + "/" + Path.GetFileName(source);
        }

        Directory.CreateDirectory(folder);
        string stem = Path.GetFileNameWithoutExtension(source);
        string name = stem + ext;
        for (int i = 2; File.Exists(Path.Combine(folder, name)); i++)
        {
            name = $"{stem}-{i}{ext}";
        }

        File.Copy(source, Path.Combine(folder, name));
        return Folder + "/" + name;
    }

    /// <summary>
    /// Copies the pictures recipes name by full path (an import's: the source map's or the shared catalog's) into the
    /// project's <c>pictures/</c> folder and points the recipes there, so the project carries its pictures wherever it
    /// goes. Run on a project's first save; a picture linked later on purpose (<c>ose identity ... --link</c>) stays.
    /// </summary>
    public static void Adopt(MapProject project, string projectFolder)
    {
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((_, PictureRecipe recipe) in project.Identity.Pictures.Slots())
        {
            if (recipe.From != PictureSource.File || recipe.File is not { } file || !Path.IsPathFullyQualified(file) || !File.Exists(file)
                || !Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                continue;
            }

            if (!copied.TryGetValue(file, out string? local))
            {
                copied[file] = local = Import(projectFolder, file);
            }

            recipe.File = local;
        }
    }

    /// <summary>Save As and autosave: the picture files go with the map (the cache does not).</summary>
    public static void CopyFolder(string fromProject, string toProject)
    {
        string from = Path.Combine(Path.GetFullPath(fromProject), Folder);
        string to = Path.Combine(Path.GetFullPath(toProject), Folder);
        if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
