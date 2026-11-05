using Godot;
using OpenScct.Editor.Core.Compile.Output;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>
/// PNG textures for the viewports and the asset browser, loaded once each: the file is decoded and mipmapped on a
/// worker thread, and the texture is made on the main thread. Callers get the texture through a callback, so a view
/// draws with its fallback until the real one arrives.
/// </summary>
public static class TextureCache
{
    private static readonly Dictionary<string, Texture2D?> s_loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<Texture2D>>> s_waiting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> s_grids = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_workers = new(Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4));
    private static int s_generation;

    /// <summary>Calls <paramref name="ready"/> with the texture of <paramref name="file"/> (at once when loaded, else
    /// later on the main thread). A file that is missing or does not decode never calls back.</summary>
    public static void Request(string file, Action<Texture2D> ready)
    {
        if (s_loaded.TryGetValue(file, out Texture2D? done))
        {
            if (done is not null)
            {
                ready(done);
            }

            return;
        }

        if (s_waiting.TryGetValue(file, out List<Action<Texture2D>>? list))
        {
            list.Add(ready);
            return;
        }

        s_waiting[file] = [ready];
        int generation = s_generation;
        BackgroundJobs.Start(async cancel =>
        {
            await s_workers.WaitAsync(cancel).ConfigureAwait(false);
            Image? image = null;
            try
            {
                // Never once the editor is closing: the engine may be shutting down.
                EditorLifetime.TryEngine(() => image = LoadImage(file, mipmaps: true));
            }
            finally
            {
                s_workers.Release();
            }

            BackgroundJobs.Post(() => Finish(file, image, generation));
        });
    }

    /// <summary>True once <paramref name="file"/> has finished loading (or failed): its callbacks have run.</summary>
    public static bool IsSettled(string file) => s_loaded.ContainsKey(file);

    private static void Finish(string file, Image? image, int generation)
    {
        if (generation != s_generation)
        {
            // Read before a Clear: the file may have changed since.
            return;
        }

        Texture2D? texture = image is null ? null : ImageTexture.CreateFromImage(image);
        s_loaded[file] = texture;
        if (s_waiting.Remove(file, out List<Action<Texture2D>>? list) && texture is not null)
        {
            foreach (Action<Texture2D> ready in list)
            {
                ready(texture);
            }
        }
    }

    /// <summary>A PNG as an image, or null when it is missing or broken. On a worker thread, call it inside
    /// <see cref="EditorLifetime.TryEngine"/>.</summary>
    public static Image? LoadImage(string file, bool mipmaps)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var image = new Image();
            if (image.LoadPngFromBuffer(File.ReadAllBytes(file)) != Error.Ok)
            {
                return null;
            }

            if (mipmaps)
            {
                image.GenerateMipmaps();
            }

            return image;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The stand-in grid texture of a material with no image (the compiler's own grid, so a build looks
    /// the same).</summary>
    public static Texture2D Grid(string material)
    {
        if (!s_grids.TryGetValue(material, out Texture2D? t))
        {
            var image = new Image();
            image.LoadPngFromBuffer(GridTexture.Make(material));
            image.GenerateMipmaps();
            t = ImageTexture.CreateFromImage(image);
            s_grids[material] = t;
        }

        return t;
    }

    /// <summary>Forgets loaded textures (after map-local textures were re-imported); loads in flight are dropped.</summary>
    public static void Clear()
    {
        s_generation++;
        s_loaded.Clear();
        s_waiting.Clear();
    }
}
