using Godot;
using OpenScct.Content.Materials;
using OpenScctEditor.Shell;
using OpenScctEditor.Core;
using OpenScctGame.Render.Materials;

namespace OpenScctEditor.Assets;

public static class AuthoredTextures
{
    private static readonly Dictionary<string, Texture2D> s_loaded = new();
    private static readonly Dictionary<string, List<Action<Texture2D>>> s_waiting = new();
    private static int s_generation;
    public static void Request(string file, string slot, MaterialTexture binding, MaterialAnimation animation, Action<Texture2D> ready)
    {
        string key = file + ":" + AuthoredTextureImage.Key(slot, binding, animation);
        if (s_loaded.TryGetValue(key, out var known)) { ready(known); return; }
        if (s_waiting.TryGetValue(key, out var waiting)) { waiting.Add(ready); return; }
        s_waiting[key] = [ready];
        int generation = s_generation;
        BackgroundJobs.Start(cancel =>
        {
            Image? image = null;
            try { EditorLifetime.TryEngine(() => image = AuthoredTextureImage.Decode(File.ReadAllBytes(file), slot, binding, animation)); }
            catch (Exception ex) { BackgroundJobs.Post(() => EditorContext.Instance.Log("Material texture: " + ex.Message, OpenScct.Editor.Core.Services.Severity.Error)); }
            BackgroundJobs.Post(() =>
            {
                if (generation != s_generation) { image?.Dispose(); return; }
                if (!s_waiting.Remove(key, out var callbacks) || image is null) return;
                Texture2D texture = ImageTexture.CreateFromImage(image); image.Dispose(); s_loaded[key] = texture;
                foreach (var callback in callbacks) callback(texture);
            });
        });
    }
    public static void Clear() { s_generation++; s_loaded.Clear(); s_waiting.Clear(); }
}
