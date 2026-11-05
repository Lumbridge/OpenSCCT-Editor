using System.Security.Cryptography;
using System.Text;
using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Assets;

/// <summary>
/// Thumbnails for the asset browser. Materials and textures are their image scaled down (on a worker thread). Meshes
/// are rendered one at a time in an offscreen SubViewport with their real textures, then kept on disk under
/// %LOCALAPPDATA%/OpenSCCT-Editor/cache/thumbs (keyed by the mesh path and its file's size and date), so each mesh
/// renders once. Headless runs have no renderer: meshes then only get thumbnails already on disk. Sounds have none.
/// A mesh render waiting its turn is skipped once nobody wants it any more (its tile left the grid: a new search, another
/// tab), so the tiles on screen now never wait behind the renders of a grid already gone.
/// </summary>
public sealed partial class AssetThumbnails : Node
{
    public const int Size = 64;
    private const int LoadTimeoutFrames = 600;
    private const int SettleFrames = 3;

    private readonly Dictionary<string, Texture2D?> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Waiter>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Key, AssetEntry Entry)> _meshQueue = new();
    private readonly SemaphoreSlim _workers = new(2);
    private SubViewport? _viewport;
    /// <summary>Changes when mesh renders change look (lighting), so cached renders are made again.</summary>
    private const int RenderVersion = 2;

    private MeshInstance3D? _mesh;
    private Camera3D? _camera;
    private Job? _job;

    /// <summary>Bumped by <see cref="Clear"/>, so loads still running then are dropped when they finish.</summary>
    private int _generation;

    public static AssetThumbnails? Instance { get; private set; }

    public static string CacheFolder => Path.Combine(EditorCache.Root, "thumbs");

    private static bool CanRender => DisplayServer.GetName() != "headless";

    public static AssetThumbnails Create()
    {
        Instance = new AssetThumbnails { Name = "AssetThumbnails" };
        return Instance;
    }

    /// <summary>Calls <paramref name="ready"/> with the entry's thumbnail on the main thread (at once when made). An
    /// entry with no picture never calls back. <paramref name="wanted"/> says whether the caller still wants it: a mesh
    /// render nobody wants any more by its turn is dropped (asked again later, it queues again).</summary>
    public void Request(AssetEntry e, Action<Texture2D> ready, Func<bool>? wanted = null)
    {
        string key = $"{e.Kind}|{e.Path}";
        if (_done.TryGetValue(key, out Texture2D? t))
        {
            if (t is not null)
            {
                ready(t);
            }

            return;
        }

        var waiter = new Waiter(ready, wanted);
        if (_waiting.TryGetValue(key, out List<Waiter>? list))
        {
            list.Add(waiter);
            return;
        }

        switch (e.Kind)
        {
            case AssetKind.Material or AssetKind.Texture when e.Texture is { } file:
                _waiting[key] = [waiter];
                LoadScaled(key, file, EditorTheme.Tile);
                break;
            case AssetKind.Mesh when e.File is not null:
                _waiting[key] = [waiter];
                string cached = DiskFile(e);
                if (File.Exists(cached))
                {
                    LoadScaled(key, cached, EditorTheme.TileMesh);
                }
                else if (CanRender)
                {
                    _meshQueue.Enqueue((key, e));
                }
                else
                {
                    Finish(key, null);
                }

                break;
        }
    }

    /// <summary>Forgets the made thumbnails (after map-local assets changed); the disk cache stays.</summary>
    public void Clear()
    {
        _done.Clear();
        _waiting.Clear();
        _meshQueue.Clear();
        _job = null;
        _generation++;
    }

    public override void _Process(double delta)
    {
        if (_job is null)
        {
            while (_meshQueue.TryDequeue(out (string Key, AssetEntry Entry) next))
            {
                if (StillWanted(next.Key))
                {
                    Start(next);
                    break;
                }

                _waiting.Remove(next.Key);
            }

            return;
        }

        Step(_job);
    }

    /// <summary>Loads a picture on a worker thread, scaled down and put on a tile of <paramref name="fill"/>.</summary>
    private void LoadScaled(string key, string file, Color fill)
    {
        int generation = _generation;
        BackgroundJobs.Start(async cancel =>
        {
            await _workers.WaitAsync(cancel).ConfigureAwait(false);
            Image? image = null;
            try
            {
                // Engine images on this worker, never once the editor is closing (the engine may be shutting down).
                EditorLifetime.TryEngine(() =>
                {
                    image = TextureCache.LoadImage(file, mipmaps: false);
                    if (image is not null)
                    {
                        Fit(image);
                        image = PictureTile.Put(image, Size, fill);
                    }
                });
            }
            finally
            {
                _workers.Release();
            }

            BackgroundJobs.Post(() =>
            {
                if (generation == _generation)
                {
                    Finish(key, image is null ? null : ImageTexture.CreateFromImage(image));
                }
            });
        });
    }

    private void Finish(string key, Texture2D? texture)
    {
        _done[key] = texture;
        if (_waiting.Remove(key, out List<Waiter>? list) && texture is not null)
        {
            foreach (Waiter w in list)
            {
                w.Ready(texture);
            }
        }
    }

    /// <summary>Whether anyone still waits for a queued render (forgetting those who no longer do).</summary>
    private bool StillWanted(string key)
    {
        if (!_waiting.TryGetValue(key, out List<Waiter>? list))
        {
            return false;
        }

        list.RemoveAll(w => w.Wanted is { } wanted && !wanted());
        return list.Count > 0;
    }

    private readonly record struct Waiter(Action<Texture2D> Ready, Func<bool>? Wanted);

    private static void Fit(Image image)
    {
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        int w = image.GetWidth(), h = image.GetHeight();
        float k = Math.Min(1f, (float)Size / Math.Max(w, h));
        if (k < 1)
        {
            image.Resize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), Image.Interpolation.Bilinear);
        }
    }

    // ---- mesh rendering ---------------------------------------------------------------------------------------------

    private void Start((string Key, AssetEntry Entry) next)
    {
        EnsureViewport();
        var job = new Job(next.Key, next.Entry);
        _job = job;
        StaticMeshes.Request(next.Entry.Path, mesh => job.Mesh = mesh);
    }

    private void Step(Job job)
    {
        job.Frames++;
        if (job.Mesh is null)
        {
            if (job.Frames > LoadTimeoutFrames || StaticMeshes.IsMissing(job.Entry.Path))
            {
                // The mesh is missing or broken: no thumbnail.
                Finish(job.Key, null);
                _job = null;
            }

            return;
        }

        if (!job.Shown)
        {
            Show(job.Mesh);
            job.Shown = true;
            job.Frames = 0;
            return;
        }

        // Wait for the section textures, then a few frames so the render target has them.
        bool texturesIn = job.Mesh.Materials.All(m => m.TextureFile is null || TextureCache.IsSettled(m.TextureFile));
        if (!texturesIn && job.Frames < LoadTimeoutFrames)
        {
            job.Settled = 0;
            return;
        }

        if (job.Settled++ == 0)
        {
            _viewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            return;
        }

        if (job.Settled < SettleFrames)
        {
            return;
        }

        Image image = _viewport!.GetTexture().GetImage();
        Fit(image);
        _mesh!.Mesh = null;
        _job = null;
        string file = DiskFile(job.Entry);
        BackgroundJobs.Start(_ =>
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                EditorLifetime.TryEngine(() => image.SavePng(file));
            }
            catch (IOException)
            {
                // The cache is an optimisation; a failed write only means rendering again next time.
            }
        });
        // The disk keeps the bare render; the browser shows it on a tile.
        Finish(job.Key, ImageTexture.CreateFromImage(PictureTile.Put((Image)image.Duplicate(), Size, EditorTheme.TileMesh)));
    }

    private void Show(LoadedMesh mesh)
    {
        _mesh!.Mesh = mesh.Mesh;
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            _mesh.SetSurfaceOverrideMaterial(i, mesh.Materials[i].Lit);
        }

        // Frame the bounds from the front-left, a little above, like a catalogue shot.
        Vector3 min = Space.ToGodot(mesh.Min), max = Space.ToGodot(mesh.Max);
        Vector3 centre = (min + max) / 2;
        float radius = Math.Max(0.01f, (max - min).Length() / 2);
        float distance = radius / Mathf.Sin(Mathf.DegToRad(_camera!.Fov / 2)) * 1.02f;
        Vector3 dir = new Vector3(-1, 0.75f, 1.2f).Normalized();
        _camera.Near = Math.Max(0.01f, distance - radius * 2);
        _camera.Far = distance + radius * 2;
        _camera.LookAtFromPosition(centre + dir * distance, centre, Vector3.Up);
    }

    private void EnsureViewport()
    {
        if (_viewport is not null)
        {
            return;
        }

        _viewport = new SubViewport
        {
            Name = "ThumbnailViewport",
            Size = new Vector2I(Size * 2, Size * 2),
            OwnWorld3D = true,
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        _camera = new Camera3D { Fov = 30, Current = true };
        _mesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.63f, 0.68f),
            AmbientLightEnergy = 1.35f,
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        // A studio set-up: a key light from the camera's side, a softer fill from the other side and a rim from behind,
        // so dark meshes (foliage, metal) still show their shape against the tile.
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -35, 0), LightEnergy = 1.5f });
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-20, 120, 0), LightEnergy = 0.7f });
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30, 200, 0), LightEnergy = 0.6f });
        _viewport.AddChild(_camera);
        _viewport.AddChild(_mesh);
        AddChild(_viewport);
    }

    /// <summary>The disk cache file of a mesh thumbnail: its path, file size and date, hashed.</summary>
    private static string DiskFile(AssetEntry e)
    {
        var info = new FileInfo(e.File!);
        string id = $"{RenderVersion}|{e.Path}|{e.Mesh}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{Size}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToUpperInvariant())))[..24].ToLowerInvariant();
        return Path.Combine(CacheFolder, hash + ".png");
    }

    private sealed class Job(string key, AssetEntry entry)
    {
        public string Key { get; } = key;

        public AssetEntry Entry { get; } = entry;

        public LoadedMesh? Mesh { get; set; }

        public bool Shown { get; set; }

        public int Frames { get; set; }

        public int Settled { get; set; }
    }
}
