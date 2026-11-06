using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// Lets a script or a screenshot run look at the editor and act on it: <c>ui.shot</c> captures the window, a control
/// or the views (from the editor's own image, so it works off-screen), <c>ui.input</c> sends synthetic mouse and
/// key input, <c>ui.tree</c> lists the visible controls. The live server and <see cref="ShotRunner"/> both call
/// these, so a scripted tour and a script see exactly the same thing.
/// </summary>
[ShellPart(Order = 105)]
public sealed partial class UiDriver : Node, IShellPart
{
    public static UiDriver? Instance { get; private set; }

    /// <summary>Where <c>ui.shot</c> writes when the call names no folder.</summary>
    public string OutputFolder { get; set; } = UserData.ShotsFolder;

    private Window Root => GetTree().Root;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        Name = "UiDriver";
        window.AddChild(this);
    }

    /// <summary>Waits until <paramref name="frames"/> more frames have been drawn.</summary>
    public async Task Frames(int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }

    /// <summary>Runs a <c>ui.*</c> call. Every failure comes back as an API error: the editor keeps running and the
    /// client's connection stays open whatever it sends.</summary>
    public async Task<ApiReply> Call(string method, SvJsonObject args)
    {
        try
        {
            return ApiReply.Ok(method switch
            {
                "ui.shot" => await Shot(args),
                "ui.input" => await Input(args),
                "ui.tree" => Tree(args),
                _ => throw new ApiException($"unknown method {method}", "method"),
            });
        }
        catch (ApiException ex)
        {
            return ApiReply.Error(ex);
        }
        catch (TimeoutException) when (method == "ui.shot")
        {
            return ApiReply.Error(new ApiException("The editor did not draw a frame within five seconds; restore its window and try again."));
        }
        catch (Exception ex) when (method == "ui.shot" && ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ApiReply.Error(new ApiException($"cannot write the screenshot: {ex.Message}", "dir"));
        }
        catch (Exception ex)
        {
            return ApiReply.Error(ApiException.Internal(ex));
        }
    }

    public SvJsonObject Tree(SvJsonObject args) =>
        UiTree.Dump(Root, ApiJson.GetString(args, "under"), ApiJson.GetBool(args, "all", false), (int)ApiJson.Number(args, "limit", 400));

    /// <summary>Captures after the next drawn frame, so input sent just before is in the picture. Arguments:
    /// <c>name</c>, <c>dir</c>, <c>crop</c> (a target, see <see cref="UiTree.Resolve"/>), <c>views</c> (also each view
    /// as shown and its bare 3D frame), <c>lint</c> (default true).</summary>
    public async Task<SvJsonObject> Shot(SvJsonObject args)
    {
        if (!BackgroundMode.Active || ApiJson.GetBool(args, "viewportsOnly", false))
        {
            return await ViewportsShot(args);
        }

        await Frames(1);
        string dir = Path.GetFullPath(ApiJson.GetString(args, "dir") ?? OutputFolder);
        string name = Safe(ApiJson.GetString(args, "name") ?? DateTime.Now.ToString("HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        Image image = Root.GetTexture().GetImage();
        string file = Path.Combine(dir, name + ".png");
        Save(image, file);

        var result = ApiJson.Obj(
            ("path", ApiJson.Str(file)),
            ("size", ApiJson.Arr([SvJsonNode.Integer(image.GetWidth()), SvJsonNode.Integer(image.GetHeight())])),
            ("scale", ApiJson.Num(Root.ContentScaleFactor)));
        if (args["crop"] is { } crop && !ApiJson.IsNull(crop))
        {
            string cropFile = Path.Combine(dir, name + ".crop.png");
            Rect2I region = Pixels(UiTree.Resolve(Root, crop), image);
            if (region.Size.X <= 0 || region.Size.Y <= 0)
            {
                throw new ApiException($"the crop target {crop} is outside the window", "crop");
            }

            Save(image.GetRegion(region), cropFile);
            result.Add("crop", ApiJson.Str(cropFile));
        }

        if (ApiJson.GetBool(args, "views", false) && ViewportLayout.Instance is { } layout)
        {
            var views = new SvJsonArray();
            foreach (EditorViewport v in layout.Views.Where(v => v.IsVisibleInTree()))
            {
                string shown = Path.Combine(dir, $"{name}.view-{v.Kind}.png");
                string raw = Path.Combine(dir, $"{name}.view-{v.Kind}.raw.png");
                Save(image.GetRegion(Pixels(UiTree.RectInRoot(v), image)), shown);
                Save(v.Sub.GetTexture().GetImage(), raw);
                views.Add(ApiJson.Obj(("view", ApiJson.Str(v.Kind.ToString())), ("mode", ApiJson.Str(v.Mode.ToString())),
                    ("shown", ApiJson.Str(shown)), ("raw", ApiJson.Str(raw))));
            }

            result.Add("views", views);
        }

        if (ApiJson.GetBool(args, "lint", true))
        {
            result.Add("lint", UiLint.Run(Root));
        }

        return result;
    }

    /// <summary>Captures only visible map viewports, without reading the window image, changing the camera, selecting
    /// objects, focusing the app, opening panels or running UI lint. The same path can be tested off-screen.</summary>
    private async Task<SvJsonObject> ViewportsShot(SvJsonObject args)
    {
        if (args["crop"] is { } crop && !ApiJson.IsNull(crop))
        {
            throw new ApiException("Viewport-only captures do not take crop; use the returned view images.", "crop");
        }

        await Frames(1).WaitAsync(TimeSpan.FromSeconds(5));
        if (ViewportLayout.Instance is not { } layout)
        {
            throw new ApiException("No map viewports are available.");
        }

        var visible = layout.Views.Where(v => v.IsVisibleInTree()).OrderBy(v => v == layout.Active ? 0 : 1).ToList();
        if (visible.Count == 0) throw new ApiException("No map viewport is visible.");
        string dir = Path.GetFullPath(ApiJson.GetString(args, "dir") ?? OutputFolder);
        string name = Safe(ApiJson.GetString(args, "name") ?? DateTime.Now.ToString("HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var views = new SvJsonArray();
        string? first = null;
        foreach (EditorViewport view in visible)
        {
            using Image frame = view.Sub.GetTexture().GetImage();
            string path = Path.Combine(dir, $"{name}.view-{view.Kind}.raw.png");
            Save(frame, path);
            first ??= path;
            views.Add(ApiJson.Obj(("view", ApiJson.Str(view.Kind.ToString())), ("mode", ApiJson.Str(view.Mode.ToString())),
                ("raw", ApiJson.Str(path)), ("width", SvJsonNode.Integer(frame.GetWidth())), ("height", SvJsonNode.Integer(frame.GetHeight())),
                ("camera", view.Cam.IsOrtho
                    ? ApiJson.Obj(("center", ApiJson.Vec(view.Cam.Center)), ("unitsPerPixel", ApiJson.Num(view.Cam.UnitsPerPixel)))
                    : ApiJson.Obj(("loc", ApiJson.Vec(view.Cam.Location)), ("rot", ApiJson.Rotator(view.Cam.Rotation)))),
                ("skyPreview", ApiJson.Bool((view.Cam.Camera.CullMask & VisualLayers.SkyBackdrop) != 0))));
        }

        return ApiJson.Obj(("path", ApiJson.Str(first!)), ("scope", ApiJson.Str("viewports")),
            ("map", ApiJson.Str(EditorContext.Instance.Project.Id)), ("views", views));
    }

    /// <summary>Sends input steps (<c>steps</c>, or the arguments as one step). A step names its kind in <c>op</c>:
    /// move, click, rightClick, doubleClick, press, release, drag, scroll, key, type, hold, hover, escape. Positions
    /// are targets (<c>at</c>, <c>to</c>) with an optional <c>offset</c> [fx, fy] inside the target (default its
    /// centre). <c>frames</c> after each step, default 1.</summary>
    public async Task<SvJsonObject> Input(SvJsonObject args)
    {
        IEnumerable<SvJsonObject> steps = args["steps"] is SvJsonArray list ? list.OfType<SvJsonObject>() : [args];
        int done = 0;
        foreach (SvJsonObject step in steps)
        {
            await Step(step);
            await Frames(Math.Max(1, (int)ApiJson.Number(step, "frames", 1)));
            done++;
        }

        return ApiJson.Obj(("done", SvJsonNode.Integer(done)),
            ("pointer", ApiJson.Arr([ApiJson.Num(EditorPointer.LastPosition.X), ApiJson.Num(EditorPointer.LastPosition.Y)])),
            ("hovered", Root.GuiGetHoveredControl() is { } h ? ApiJson.Str(UiTree.PathOf(Root, h)) : null),
            ("popup", Root.GetEmbeddedSubwindows().LastOrDefault(w => w.Visible) is { } p ? ApiJson.Str(UiTree.PathOf(Root, p)) : null));
    }

    /// <summary>The keys each input op reads, besides <c>op</c>, <c>frames</c> and <c>mods</c>. A misspelt key fails the
    /// step instead of quietly using a default.</summary>
    private static readonly Dictionary<string, string[]> s_inputKeys = new(StringComparer.Ordinal)
    {
        ["move"] = ["at", "offset", "steps"],
        ["click"] = ["at", "offset", "button"],
        ["rightClick"] = ["at", "offset"],
        ["doubleClick"] = ["at", "offset"],
        ["press"] = ["at", "offset", "button"],
        ["release"] = ["at", "offset", "button"],
        ["drag"] = ["at", "offset", "to", "toOffset", "button", "steps", "hold"],
        ["scroll"] = ["at", "offset", "clicks"],
        ["key"] = ["key"],
        ["type"] = ["text"],
        ["hold"] = ["key", "down"],
        ["hover"] = ["at", "offset", "seconds"],
        ["escape"] = [],
    };

    /// <summary>True for the ops <see cref="Input"/> takes.</summary>
    public static bool IsInputOp(string op) => s_inputKeys.ContainsKey(op);

    /// <summary>Fails when <paramref name="step"/> has a key that <paramref name="allowed"/> (plus op and frames) does not name.</summary>
    public static void CheckKeys(SvJsonObject step, string op, IEnumerable<string> allowed)
    {
        var known = new HashSet<string>(allowed, StringComparer.Ordinal) { "op", "frames" };
        if (step.Select(kv => kv.Key).FirstOrDefault(k => !known.Contains(k)) is { } unknown)
        {
            known.Remove("op");
            throw new ApiException($"{op} does not take '{unknown}' (it takes {string.Join(", ", known.Order(StringComparer.Ordinal))})", unknown);
        }
    }

    private async Task Step(SvJsonObject step)
    {
        string op = ApiJson.GetString(step, "op") ?? throw new ApiException("each input step needs an op", "op");
        if (!s_inputKeys.TryGetValue(op, out string[]? keys))
        {
            throw new ApiException($"unknown input op '{op}' (one of {string.Join(", ", s_inputKeys.Keys)})", "op");
        }

        CheckKeys(step, op, op == "escape" ? keys : [.. keys, "mods"]);
        KeyModifierMask mods = Modifiers(ApiJson.GetString(step, "mods"));
        switch (op)
        {
            case "move":
                UiInput.Move(Root, Point(step, "at"), (int)ApiJson.Number(step, "steps", 1));
                break;
            case "click":
                UiInput.Click(Root, Point(step, "at"), Button(step), mods);
                break;
            case "rightClick":
                UiInput.Click(Root, Point(step, "at"), MouseButton.Right, mods);
                break;
            case "doubleClick":
            {
                Vector2 at = Point(step, "at");
                UiInput.Click(Root, at, MouseButton.Left, mods);
                UiInput.Button(Root, at, MouseButton.Left, true, mods, doubleClick: true);
                UiInput.Button(Root, at, MouseButton.Left, false, mods);
                break;
            }

            case "press":
                UiInput.Button(Root, Point(step, "at"), Button(step), true, mods);
                break;
            case "release":
                UiInput.Button(Root, step["at"] is null ? EditorPointer.LastPosition : Point(step, "at"), Button(step), false, mods);
                break;
            case "drag":
            {
                MouseButton button = Button(step);
                UiInput.Button(Root, Point(step, "at"), button, true, mods);
                await Frames(1);
                UiInput.Move(Root, Point(step, "to"), (int)ApiJson.Number(step, "steps", 8));
                await Frames(1);
                if (ApiJson.GetBool(step, "hold", false))
                {
                    break;
                }

                UiInput.Button(Root, EditorPointer.LastPosition, button, false, mods);
                break;
            }

            case "scroll":
            {
                Vector2 at = Point(step, "at");
                int clicks = (int)ApiJson.Number(step, "clicks", 1);
                for (int i = 0; i < Math.Abs(clicks); i++)
                {
                    UiInput.Click(Root, at, clicks > 0 ? MouseButton.WheelUp : MouseButton.WheelDown, mods);
                }

                break;
            }

            case "key":
            {
                string chord = ApiJson.GetString(step, "key") ?? throw new ApiException("key needs a key, such as \"Ctrl+Z\"", "key");
                if (!UiInput.Key(chord))
                {
                    throw new ApiException($"not a key: {chord}", "key");
                }

                break;
            }

            case "type":
                UiInput.Type(ApiJson.GetString(step, "text") ?? "");
                break;
            case "hold":
            {
                string name = ApiJson.GetString(step, "key") ?? "";
                Key key = OS.FindKeycodeFromString(name);
                if (key == Key.None)
                {
                    throw new ApiException($"not a key: {name}", "key");
                }

                UiInput.Hold(key, ApiJson.GetBool(step, "down", true));
                break;
            }

            case "hover":
                UiInput.Move(Root, Point(step, "at"), 2);
                // Tooltips appear after the theme's delay; wait for it plus a frame.
                await ToSignal(GetTree().CreateTimer(Math.Clamp(ApiJson.Number(step, "seconds", 0.8), 0, 30)), Godot.Timer.SignalName.Timeout);
                break;
            case "escape":
                UiInput.ReleaseAll(Root);
                UiInput.Key("Escape");
                foreach (Window w in Root.GetEmbeddedSubwindows().Where(w => w is Popup && w.Visible).Reverse())
                {
                    w.Hide();
                }

                break;
        }
    }

    /// <summary>A target's point: the centre, or <c>offset</c> [fx, fy] as fractions of the target's size
    /// (<c>toOffset</c> for a drag's <c>to</c>).</summary>
    private Vector2 Point(SvJsonObject step, string key)
    {
        SvJsonNode target = step[key] ?? throw new ApiException($"this step needs '{key}'", key);
        Rect2 r = UiTree.Resolve(Root, target);
        Vector2 f = new(0.5f, 0.5f);
        if (step[key == "to" ? "toOffset" : "offset"] is SvJsonArray { Count: 2 } o)
        {
            f = new Vector2((float)ApiJson.RequireNumber(o[0], "offset"), (float)ApiJson.RequireNumber(o[1], "offset"));
        }

        return r.Position + r.Size * f;
    }

    private static MouseButton Button(SvJsonObject step) => ApiJson.GetString(step, "button") switch
    {
        null or "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        { } b => throw new ApiException($"button is left, right or middle, not {b}", "button"),
    };

    private static KeyModifierMask Modifiers(string? text)
    {
        KeyModifierMask mods = 0;
        foreach (string m in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= m.ToLowerInvariant() switch
            {
                "ctrl" => KeyModifierMask.MaskCtrl,
                "shift" => KeyModifierMask.MaskShift,
                "alt" => KeyModifierMask.MaskAlt,
                _ => throw new ApiException($"modifiers are ctrl, shift and alt, not {m}", "mods"),
            };
        }

        return mods;
    }

    /// <summary>A canvas rectangle as image pixels, clamped to the image.</summary>
    private Rect2I Pixels(Rect2 canvas, Image image)
    {
        Transform2D t = Root.GetFinalTransform();
        Rect2 r = new Rect2(t * canvas.Position, Vector2.Zero).Expand(t * canvas.End);
        var px = new Rect2I((Vector2I)r.Position.Round(), (Vector2I)r.Size.Round());
        return px.Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
    }

    private static void Save(Image image, string file)
    {
        Error e = image.SavePng(file);
        if (e != Error.Ok)
        {
            throw new ApiException($"cannot write {file}: {e}");
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));
}
